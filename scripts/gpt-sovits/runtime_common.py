"""Local-only helpers shared by the GPT-SoVITS training and API launchers."""
from __future__ import annotations

import json
import os
from pathlib import Path
import subprocess
import sys

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8")

UPSTREAM_COMMIT = "1b47ca07ef43a284e0b8bd7f29d3e1e3ff76082a"
MODEL_VERSION = "v2"
PRETRAINED = "GPT_SoVITS/pretrained_models"
GPT_BASE = PRETRAINED + "/gsv-v2final-pretrained/s1bert25hz-5kh-longer-epoch=12-step=369668.ckpt"
SOVITS_BASE = PRETRAINED + "/gsv-v2final-pretrained/s2G2333k.pth"
SOVITS_DISCRIMINATOR = PRETRAINED + "/gsv-v2final-pretrained/s2D2333k.pth"
BERT_BASE = PRETRAINED + "/chinese-roberta-wwm-ext-large"
HUBERT_BASE = PRETRAINED + "/chinese-hubert-base"


def progress(stage: str, value: float, message: str) -> None:
    print("LFX_PROGRESS " + json.dumps({"stage": stage, "progress": round(value * 100, 2),
                                       "message": message}, ensure_ascii=False), flush=True)


def write_json(path: Path, value: dict) -> None:
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding="utf-8")
    temporary.replace(path)


def required_file(path: Path) -> Path:
    if not path.is_file() or path.stat().st_size == 0:
        raise ValueError(f"缺少文件或文件为空：{path}")
    return path


def validate_runtime(root: Path, *, training: bool = False) -> None:
    required_file(root / "api_v2.py")
    for relative in (GPT_BASE, SOVITS_BASE, BERT_BASE + "/config.json",
                     BERT_BASE + "/pytorch_model.bin", HUBERT_BASE + "/config.json",
                     HUBERT_BASE + "/pytorch_model.bin", "GPT_SoVITS/text/G2PWModel/g2pW.onnx",
                     PRETRAINED + "/fast_langdetect/lid.176.bin"):
        required_file(root / relative)
    if training:
        for relative in (SOVITS_DISCRIMINATOR, "GPT_SoVITS/s1_train.py", "GPT_SoVITS/s2_train.py",
                         "GPT_SoVITS/configs/s1longer-v2.yaml", "GPT_SoVITS/configs/s2.json",
                         "GPT_SoVITS/prepare_datasets/1-get-text.py",
                         "GPT_SoVITS/prepare_datasets/2-get-hubert-wav32k.py",
                         "GPT_SoVITS/prepare_datasets/3-get-semantic.py"):
            required_file(root / relative)


def choose_device(requested: str) -> tuple[str, bool]:
    if requested not in {"auto", "cpu", "cuda"}:
        raise ValueError("device 只支持 auto、cpu、cuda。")
    try:
        import torch
    except ImportError as error:
        raise ValueError("此 Python 未安装 PyTorch，请先运行 Setup-GptSoVits.ps1，或选择已配置的运行环境。") from error
    available = torch.cuda.is_available()
    if requested == "cuda" and not available:
        raise ValueError("选择了 CUDA，但当前 Python/PyTorch 无法使用 NVIDIA GPU。可选择 CPU 或检查驱动与 PyTorch。")
    device = "cuda" if available and requested != "cpu" else "cpu"
    # Float32 is deliberate for CPU and safer for varying consumer GPUs.
    return device, False


def local_environment(root: Path, device: str) -> dict[str, str]:
    env = os.environ.copy()
    env.update({"version": MODEL_VERSION, "is_half": "False", "PYTHONUTF8": "1",
                "PYTHONUNBUFFERED": "1", "PYTHONNOUSERSITE": "1",
                "HF_HUB_OFFLINE": "1", "TRANSFORMERS_OFFLINE": "1",
                "HF_HUB_DISABLE_TELEMETRY": "1", "GRADIO_ANALYTICS_ENABLED": "False",
                "OMP_NUM_THREADS": "2", "MKL_NUM_THREADS": "2", "OPENBLAS_NUM_THREADS": "2",
                "NUMBA_NUM_THREADS": "2",
                "_CUDA_VISIBLE_DEVICES": "0" if device == "cuda" else "",
                "CUDA_VISIBLE_DEVICES": "0" if device == "cuda" else "",
                "PYTHONPATH": os.pathsep.join(str(root / p) for p in
                                             ("", "GPT_SoVITS", "GPT_SoVITS/BigVGAN", "tools"))})
    cache = root / ".linguaforge" / "cache"
    env.update({"HF_HOME": str(cache / "huggingface"), "MODELSCOPE_CACHE": str(cache / "modelscope"),
                "NLTK_DATA": str(root / "runtime" / "nltk_data")})
    ffmpeg_dirs = [root, root / "ffmpeg" / "bin", root / "runtime" / "bin"]
    env["PATH"] = os.pathsep.join(map(str, ffmpeg_dirs)) + os.pathsep + env.get("PATH", "")
    return env


def run_upstream(root: Path, relative: str, arguments: list[str], env: dict[str, str]) -> None:
    # Arguments are passed without a shell: recording paths/text never become executable code.
    process = subprocess.Popen([sys.executable, "-u", str(root / relative), *arguments],
                               cwd=root, env=env)
    try:
        code = process.wait()
    except BaseException:
        process.terminate()
        process.wait(timeout=15)
        raise
    if code != 0:
        raise RuntimeError(f"GPT-SoVITS 阶段 {relative} 失败，退出代码 {code}；请检查上方日志。")
