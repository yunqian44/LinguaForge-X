"""Check CPU inference equivalence without downloading or loading production weights."""
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

import cpu_inference as cpu


class InferenceTests(unittest.TestCase):
    def test_unrecognized_runtime_runs_original_cli(self):
        with tempfile.TemporaryDirectory() as directory:
            entry = Path(directory).resolve() / "api_v2.py"
            entry.write_text("# user runtime", encoding="utf-8")
            with patch.object(Path, "cwd", return_value=entry.parent), \
                 patch.object(sys, "argv", ["cpu_inference.py", "-p", "9988"]), \
                 patch.object(sys, "path", list(sys.path)), \
                 patch.object(cpu.runpy, "run_path") as run, \
                 patch.object(cpu, "install_optimizations") as optimize:
                cpu.main()
                run.assert_called_once_with(str(entry), run_name="__main__")
                self.assertEqual(sys.argv, [str(entry), "-p", "9988"])
                optimize.assert_not_called()

    def test_third_last_hidden_features_match_original_model(self):
        try:
            import torch
            from transformers import BertConfig, BertForMaskedLM
        except ImportError:
            self.skipTest("Requires the installed CPU inference dependencies")

        config = BertConfig(hidden_size=16, intermediate_size=32, num_hidden_layers=4,
                            num_attention_heads=2, vocab_size=32)
        model = BertForMaskedLM(config)
        self.assertIsNone(cpu.feature_backbone(model))
        self.assertEqual(len(model.bert.encoder.layer), 4)
        self.assertIsNone(cpu.feature_backbone(torch.nn.Identity()))
        model.eval()
        inputs = {"input_ids": torch.tensor([[2, 7, 8, 9, 3]]), "attention_mask": torch.ones(1, 5)}
        counts = [1, 3, 2]
        with torch.no_grad():
            selected = model(**inputs, output_hidden_states=True).hidden_states[-3][0, 1:-1]
            expected = torch.cat([row.repeat(n, 1) for row, n in zip(selected, counts)]).T
        prefix = cpu.feature_backbone(model)
        self.assertEqual(len(prefix.encoder.layer), 2)
        actual = cpu.phone_features(prefix, lambda text, **opts: inputs, "测试音", counts)
        self.assertTrue(torch.equal(expected, actual))
        # Preserve the upstream loop's treatment of tokenizers producing more
        # token rows than characters (and fail on insufficient token rows).
        extra = cpu.phone_features(prefix, lambda text, **opts: inputs, "测试", counts[:2])
        self.assertTrue(torch.equal(expected[:, :4], extra))
        with self.assertRaisesRegex(ValueError, "词元不足"):
            cpu.phone_features(prefix, lambda text, **opts: inputs, "测试音色", [1, 3, 2, 2])

    def test_folding_weight_norm_preserves_samples_and_skips_training(self):
        try:
            import torch
            from torch.nn.utils import weight_norm
        except ImportError:
            self.skipTest("Requires PyTorch")

        layer = weight_norm(torch.nn.Conv1d(2, 2, 3))
        decoder = torch.nn.Sequential(layer, torch.nn.ReLU())
        cpu.freeze_decoder_weights(decoder)
        self.assertTrue(hasattr(layer, "weight_g"))
        decoder.eval()
        with torch.no_grad():
            audio = torch.linspace(-1, 1, 32).view(1, 2, 16)
            expected = decoder(audio)
            cpu.freeze_decoder_weights(decoder)
            self.assertFalse(hasattr(layer, "weight_g"))
            self.assertTrue(torch.equal(expected, decoder(audio)))
            cpu.freeze_decoder_weights(decoder)
            self.assertTrue(torch.equal(expected, decoder(audio)))

    def test_vocoder_retains_temporal_geometry_and_conditioning(self):
        try:
            import torch
            from torch import nn
        except ImportError:
            self.skipTest("Requires PyTorch")
        if not (torch.backends.mkldnn.enabled and torch.backends.mkldnn.is_available()):
            self.skipTest("Requires oneDNN")

        class Generator(nn.Module):
            def __init__(self):
                super().__init__()
                self.conv = nn.Conv1d(4, 4, 3, padding=2, groups=2, dilation=2, bias=False)
                self.cond = nn.Conv1d(4, 4, 1)
                self.up = nn.ConvTranspose1d(4, 2, 4, stride=2, padding=2, output_padding=1, dilation=2, groups=2)

            def forward(self, x, g=None):
                x = self.conv(x)
                return self.up(x if g is None else x + self.cond(g))

        model = Generator().eval()
        x = torch.randn(2, 4, 17)
        g = torch.randn(2, 4, 1)
        with torch.no_grad():
            expected = [model(x, g), model(x)]
            rng = torch.get_rng_state()
            packed = cpu.channels_last_decoder(model)
            self.assertIsNot(packed, model)
            self.assertTrue(torch.equal(rng, torch.get_rng_state()))
            for condition, reference in zip([g, None], expected):
                audio = packed(x, condition)
                self.assertEqual(audio.shape, reference.shape)
                torch.testing.assert_close(audio, reference, rtol=1e-5, atol=1e-6)
            with torch.backends.mkldnn.flags(enabled=False):
                torch.testing.assert_close(packed(x, g), expected[0], rtol=1e-5, atol=1e-6)


if __name__ == "__main__":
    unittest.main()
