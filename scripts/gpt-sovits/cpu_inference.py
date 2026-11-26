"""Launch the pinned api_v2 on CPU with its original CLI and model files."""
from __future__ import annotations

import hashlib
import os
from pathlib import Path
import runpy
import sys


# Layout and feature selection of integration commit 1b47ca07 (LF line endings).
# Other API versions continue through their own entry point.
PINNED_SOURCES = {
    "api_v2.py": "44e067698a61014cdff3bda1b2fbff97252440e5879c429d38119fb0010d2b42",
    "GPT_SoVITS/TTS_infer_pack/TTS.py": "a07f54d9bb28fddeef6a82f3e24a877241483043905fe455bfe28a5bc32a38f5",
    "GPT_SoVITS/TTS_infer_pack/TextPreprocessor.py": "3628df1ea494ad8d78fe5d4c1266175cb6bb1650c971dbd953fcd3ba87d80548",
    "GPT_SoVITS/module/models.py": "b0a06b387f747705ae8eaf0cda69d4d2c8583b43e7caac887acb7f505b4599f3",
    "GPT_SoVITS/module/modules.py": "1d501146e0e736d7a4aad552f5311cb37d1f1cf3ed6f95138225aa4a08e75933",
}


def supported_runtime(root):
    for relative, digest in PINNED_SOURCES.items():
        source = root / relative
        if not source.is_file() or hashlib.sha256(source.read_bytes().replace(b"\r\n", b"\n")).hexdigest() != digest:
            return False
    return True


def feature_backbone(model):
    """Keep the exact BERT layer selected by upstream hidden_states[-3]."""
    from transformers.models.bert.modeling_bert import BertForMaskedLM, BertModel

    if type(model) is not BertForMaskedLM or model.training or model.config.is_decoder:
        return None
    bert = model.bert
    if type(bert) is not BertModel or bert.pooler is not None or next(bert.parameters()).device.type != "cpu":
        return None
    layers = bert.encoder.layer
    if len(layers) < 3 or len(layers) != bert.config.num_hidden_layers:
        return None
    # hidden_states includes the input embedding, so -3 is the output of
    # layer N-2. All retained modules are the original, loaded FP32 weights.
    bert.encoder.layer = layers[:-2]
    bert.config.num_hidden_layers = len(bert.encoder.layer)
    bert._linguaforge_feature_backbone = True
    return bert


def phone_features(backbone, tokenizer, text, word2ph):
    import torch

    if len(word2ph) != len(text):
        raise ValueError("BERT 分词与字音映射长度不一致。")
    with torch.no_grad():
        inputs = tokenizer(text, return_tensors="pt")
        output = backbone(**inputs, output_hidden_states=False, output_attentions=False, return_dict=True)
        # Upstream indexes the first len(word2ph) token rows; tokenizers can
        # return an extra subword. Keep that behavior for custom vocabularies.
        characters = output.last_hidden_state[0, 1:-1][:len(word2ph)]
        if len(characters) != len(word2ph):
            raise ValueError("BERT 生成的词元不足以对应全部字音。")
        repeats = torch.tensor(word2ph, dtype=torch.long)
        return characters.repeat_interleave(repeats, dim=0).T


def freeze_decoder_weights(decoder):
    """Fold the legacy WeightNorm hooks once on the inference-only v2 generator."""
    from torch.nn.utils.weight_norm import WeightNorm, remove_weight_norm

    if decoder.training:
        return
    for layer in decoder.modules():
        if any(isinstance(hook, WeightNorm) and hook.name == "weight" for hook in layer._forward_pre_hooks.values()):
            remove_weight_norm(layer)


def channels_last_decoder(decoder):
    """Run the v2 generator on NCHW tensors of height 1, retaining its NCT API."""
    import torch
    from torch import nn
    from torch.nn import functional as F

    if decoder.training or not (torch.backends.mkldnn.is_available() and torch.backends.mkldnn.enabled):
        return decoder
    ops = (nn.Conv1d, nn.ConvTranspose1d)
    children = [(parent, name, layer) for parent in decoder.modules()
                for name, layer in parent.named_children() if isinstance(layer, ops)]
    if not children or any(type(layer) not in ops or layer.weight.dtype != torch.float32 or
                           layer.weight.device.type != "cpu" or layer.padding_mode != "zeros" or
                           isinstance(layer.padding, str) for _, _, layer in children):
        return decoder

    class TemporalConv(nn.Module):
        def __init__(self, layer):
            super().__init__()
            # Reuse loaded parameters, without invoking a random initializer or
            # keeping both 1-D and 2-D copies of the trained decoder in memory.
            self.weight = nn.Parameter(layer.weight.detach().unsqueeze(2).contiguous(memory_format=torch.channels_last),
                                       requires_grad=False)
            self.bias = layer.bias
            self.transpose = isinstance(layer, nn.ConvTranspose1d)
            self.stride, self.padding, self.dilation = layer.stride[0], layer.padding[0], layer.dilation[0]
            self.groups = layer.groups
            self.output_padding = layer.output_padding[0] if self.transpose else 0

        def forward(self, x):
            if x.ndim == 3:
                weight = self.weight.squeeze(2)
                if self.transpose:
                    return F.conv_transpose1d(x, weight, self.bias, self.stride, self.padding,
                                              self.output_padding, self.groups, self.dilation)
                return F.conv1d(x, weight, self.bias, self.stride, self.padding, self.dilation, self.groups)
            if self.transpose:
                return F.conv_transpose2d(x, self.weight, self.bias, (1, self.stride), (0, self.padding),
                                          (0, self.output_padding), self.groups, (1, self.dilation))
            return F.conv2d(x, self.weight, self.bias, (1, self.stride), (0, self.padding),
                            (1, self.dilation), self.groups)

    class ChannelsLastGenerator(nn.Module):
        def __init__(self):
            super().__init__()
            self.generator = decoder

        def forward(self, x, g=None):
            if x.device.type != "cpu" or x.dtype != torch.float32 or not torch.backends.mkldnn.enabled:
                return self.generator(x, g)
            x = x.unsqueeze(2).contiguous(memory_format=torch.channels_last)
            if g is not None:
                g = g.unsqueeze(2).contiguous(memory_format=torch.channels_last)
            return self.generator(x, g).squeeze(2)

    for parent, name, layer in children:
        setattr(parent, name, TemporalConv(layer))
    return ChannelsLastGenerator().eval()


def install_optimizations(tts_module):
    # TTS looks up TextPreprocessor in its own module when constructing a pipeline.
    class CpuTextPreprocessor(tts_module.TextPreprocessor):
        def get_bert_feature(self, text, word2ph):
            if getattr(self.bert_model, "_linguaforge_feature_backbone", False):
                return phone_features(self.bert_model, self.tokenizer, text, word2ph)
            return super().get_bert_feature(text, word2ph)

    class CpuTTS(tts_module.TTS):
        def init_bert_weights(self, base_path):
            super().init_bert_weights(base_path)
            if str(self.configs.device) == "cpu" and self.configs.version == "v2":
                bert = feature_backbone(self.bert_model)
                if bert is not None:
                    self.bert_model = bert

        def init_vits_weights(self, weights_path):
            super().init_vits_weights(weights_path)
            if str(self.configs.device) == "cpu" and self.configs.version == "v2":
                decoder = getattr(self.vits_model, "dec", None)
                if type(decoder) is tts_module.Generator:
                    freeze_decoder_weights(decoder)
                    self.vits_model.dec = channels_last_decoder(decoder)

    tts_module.TextPreprocessor = CpuTextPreprocessor
    tts_module.TTS = CpuTTS


def main():
    root = Path.cwd().resolve()
    entry = root / "api_v2.py"
    sys.argv[0] = str(entry)
    sys.path.insert(0, str(root))
    if supported_runtime(root):
        # On a small CPU, idle OpenMP workers compete with the other inference
        # engines. Respect explicit choices from the service administrator.
        os.environ.setdefault("OMP_WAIT_POLICY", "PASSIVE")
        os.environ.setdefault("KMP_BLOCKTIME", "0")
        # Match the upstream entry's initialization before importing any ML libraries.
        from tools.portable_runtime import activate

        activate(change_cwd=True)
        sys.path.append(str(root / "GPT_SoVITS"))
        from GPT_SoVITS.TTS_infer_pack import TTS as tts_module

        install_optimizations(tts_module)
        print("LFX_CPU_INFERENCE v2: BERT hidden-state prefix, channels-last v2 decoder, "
              f"OMP_WAIT_POLICY={os.environ['OMP_WAIT_POLICY']}, KMP_BLOCKTIME={os.environ['KMP_BLOCKTIME']}",
              flush=True)
    runpy.run_path(str(entry), run_name="__main__")


if __name__ == "__main__":
    main()
