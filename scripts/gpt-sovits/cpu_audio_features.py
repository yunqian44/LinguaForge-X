"""Extract the pinned CNHuBERT v2 training features on CPU with TorchAudio."""
from __future__ import annotations

import json
import os
from pathlib import Path
import runpy
import subprocess
import time
import wave


def load_hubert(directory: Path):
    import torch
    from torchaudio.models import hubert_base

    # hubert_base is the architecture of our catalogued chinese-hubert-base.
    # Strict loading checks every convolution, attention block and projection.
    config = json.loads((directory / "config.json").read_text(encoding="utf-8"))
    expected = {"model_type": "hubert", "do_stable_layer_norm": False,
                "feat_proj_layer_norm": True, "feat_extract_norm": "group",
                "num_attention_heads": 12, "conv_stride": [5, 2, 2, 2, 2, 2, 2],
                "num_conv_pos_embedding_groups": 16, "hidden_act": "gelu",
                "feat_extract_activation": "gelu", "layer_norm_eps": 1e-5}
    if any(config.get(key) != value for key, value in expected.items()):
        raise ValueError("CPU 提取器需要当前版本随附的 chinese-hubert-base 模型配置。")
    model = hubert_base().eval()
    weights = torch.load(directory / "pytorch_model.bin", map_location="cpu", weights_only=True)
    converted = {}
    for key, tensor in weights.items():
        if key == "masked_spec_embed":  # Used only for augmentation during HuBERT pretraining.
            continue
        if key.startswith("feature_projection."):
            key = "encoder." + key
        elif key.startswith("encoder."):
            key = "encoder.transformer." + key[len("encoder."):]
        key = key.replace("pos_conv_embed.conv.weight_g", "pos_conv_embed.conv.parametrizations.weight.original0")
        key = key.replace("pos_conv_embed.conv.weight_v", "pos_conv_embed.conv.parametrizations.weight.original1")
        converted[key] = tensor
    model.load_state_dict(converted, strict=True)  # fp16 checkpoint -> fp32 CPU model
    return model


def prepare_wave(path: Path, *, check_training_length: bool = True):
    import numpy as np
    import soxr

    # The one-sentence training contract caps samples below 54 seconds.
    # Read just beyond that limit so overlong input is rejected without filling RAM.
    duration_limit = ["-t", "54.01"] if check_training_length else []
    command = ["ffmpeg", "-nostdin", "-v", "error", "-i", str(path), *duration_limit,
               "-threads", "1", "-ac", "1", "-ar", "32000", "-f", "f32le", "pipe:1"]
    decoded = subprocess.run(command, check=True, stdout=subprocess.PIPE, timeout=90)
    audio = np.frombuffer(decoded.stdout, dtype="<f4")
    if not len(audio):
        raise ValueError(f"{path.name} 没有可解码的音频采样。")
    if check_training_length and not 0.6 * 32000 < len(audio) < 54 * 32000:
        raise ValueError(f"{path.name} 不在训练样本要求的 0.6～54 秒范围内。")
    peak = np.max(np.abs(audio))
    if not np.isfinite(peak) or not 0 < peak <= 2.2:
        raise ValueError(f"{path.name} 静音或包含超出范围的采样值。")
    # Preserve upstream's two distinct scales: PCM for S2 and float input for HuBERT.
    pcm = ((audio / peak * (0.95 * 0.5 * 32768)) + 0.5 * 32768 * audio).astype("<i2")
    hubert32 = (audio / peak * (0.95 * 0.5 * 1145.14)) + 0.5 * 1145.14 * audio
    hubert16 = soxr.resample(hubert32, 32000, 16000, quality="HQ")
    # librosa.resample(..., fix=True) pads to ceil(len * rate_ratio).
    expected = (len(audio) + 1) // 2
    if len(hubert16) < expected:
        hubert16 = np.pad(hubert16, (0, expected - len(hubert16)))
    return pcm, hubert16[:expected]


def save_features(wav_path: Path, tensor_path: Path, pcm, tensor) -> None:
    import torch

    # Interactive preprocessing can be stopped between utterances. Publish the
    # tensor last so a subsequent workbench run can safely resume completed rows.
    wav_tmp = wav_path.with_name(f"{wav_path.name}.{os.getpid()}.tmp")
    tensor_tmp = tensor_path.with_name(f"{tensor_path.name}.{os.getpid()}.tmp")
    try:
        with wave.open(str(wav_tmp), "wb") as wav:
            wav.setparams((1, 2, 32000, 0, "NONE", "not compressed"))
            wav.writeframes(pcm.tobytes())
        torch.save(tensor, tensor_tmp)
        wav_tmp.replace(wav_path)
        tensor_tmp.replace(tensor_path)
    finally:
        wav_tmp.unlink(missing_ok=True)
        tensor_tmp.unlink(missing_ok=True)


def cached_pair_is_valid(wav_path: Path, tensor_path: Path, input_mtime: int) -> bool:
    import torch
    import pickle

    if not wav_path.is_file() or not tensor_path.is_file():
        return False
    if not input_mtime <= wav_path.stat().st_mtime_ns <= tensor_path.stat().st_mtime_ns:
        return False
    try:
        with wave.open(str(wav_path), "rb") as wav:
            frames = wav.getnframes()
            if (wav.getframerate(), wav.getnchannels(), wav.getsampwidth()) != (32000, 1, 2):
                return False
            if frames <= 0:
                return False
            wav.setpos(frames - 1)
            if len(wav.readframes(1)) != 2:
                return False
        features = torch.load(tensor_path, weights_only=True, map_location="cpu")
        # Pinned 16 kHz HuBERT conv encoder: 400-sample receptive field, stride 320.
        steps = ((frames + 1) // 2 - 400) // 320 + 1
        return (isinstance(features, torch.Tensor) and features.shape == (1, 768, steps)
                and features.is_floating_point() and bool(torch.isfinite(features).all()))
    except (EOFError, IndexError, OSError, RuntimeError, ValueError, wave.Error, pickle.UnpicklingError):
        return False


def main(webui: bool = False) -> None:
    model_directory = Path(os.environ["cnhubert_base_dir"]).resolve()
    if webui:
        bundled_model = Path("GPT_SoVITS/pretrained_models/chinese-hubert-base").resolve()
        if model_directory != bundled_model:
            # The official workbench accepts user-supplied alternative HuBERT models.
            entry = Path("GPT_SoVITS/prepare_datasets/2-get-hubert-wav32k.py").resolve()
            runpy.run_path(str(entry), run_name="__main__")
            return
    started = time.monotonic()
    print("正在加载 CPU CNHuBERT 权重。", flush=True)
    import torch

    model = load_hubert(model_directory)
    output = Path(os.environ["opt_dir"])
    wav_dir = output / "5-wav32k"
    feature_dir = output / "4-cnhubert"
    wav_dir.mkdir(parents=True, exist_ok=True)
    feature_dir.mkdir(parents=True, exist_ok=True)
    rows = Path(os.environ["inp_text"]).read_text(encoding="utf-8-sig").splitlines()
    rows = [row for row in rows if row.strip()]
    part, total = int(os.environ.get("i_part", "0")), int(os.environ.get("all_parts", "1"))
    rows = rows[part::total]
    print(f"模型加载耗时 {time.monotonic() - started:.1f} 秒，本进程处理 {len(rows)} 段录音。", flush=True)
    model_mtime = (model_directory / "pytorch_model.bin").stat().st_mtime_ns
    with torch.inference_mode():
        for index, row in enumerate(rows, 1):
            path = Path(row.split("|", 1)[0].strip().strip('"'))
            if os.environ.get("inp_wav_dir"):
                path = Path(os.environ["inp_wav_dir"]) / path.name
            wav_path, tensor_path = wav_dir / path.name, feature_dir / (path.name + ".pt")
            if webui and cached_pair_is_valid(wav_path, tensor_path, max(model_mtime, path.stat().st_mtime_ns)):
                continue
            pcm, samples = prepare_wave(path, check_training_length=not webui)
            features, _ = model(torch.from_numpy(samples).unsqueeze(0))
            hidden = features.transpose(1, 2).contiguous()
            if not torch.isfinite(hidden).all():
                raise ValueError(f"{path.name} 的 CNHuBERT 特征包含无效值。")
            save_features(wav_path, tensor_path, pcm, hidden)
            print(f"已提取音频特征 {index}/{len(rows)}，{hidden.shape[-1]} 帧。", flush=True)


if __name__ == "__main__":
    import argparse
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--webui", action="store_true", help="Use the interactive workbench's input contract.")
    main(parser.parse_args().webui)
