"""Check actual imports and local model files without loading user voice recordings."""
import argparse
import importlib
import json
import os
from pathlib import Path
import shutil
import sys

from runtime_common import choose_device, validate_runtime


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--runtime-root", required=True)
    args = parser.parse_args()
    root = Path(args.runtime_root).resolve()
    os.chdir(root)
    sys.path.insert(0, str(root))
    os.environ["NLTK_DATA"] = str(root / "runtime" / "nltk_data")
    problems = []
    for module in ("torch", "torchaudio", "pytorch_lightning", "transformers", "librosa", "soundfile",
                   "yaml", "fastapi", "uvicorn", "pyopenjtalk", "jieba_fast", "opencc", "peft", "x_transformers",
                   "matplotlib", "onnxruntime", "resampy"):
        print("检查依赖：" + module, flush=True)
        try:
            importlib.import_module(module)
        except Exception as error:
            problems.append(f"{module}: {error}")
    try:
        validate_runtime(root, training=True)
    except Exception as error:
        problems.append(str(error))
    try:
        import nltk
        for resource in ("taggers/averaged_perceptron_tagger", "taggers/averaged_perceptron_tagger_eng", "corpora/cmudict"):
            nltk.data.find(resource, paths=[str(root / "runtime" / "nltk_data")])
    except Exception as error:
        problems.append("缺少本地 NLTK 文本资源，请运行 Setup-GptSoVits.ps1 -DownloadModels：" + str(error))
    if not (root / "ffmpeg.exe").is_file() and not shutil.which("ffmpeg"):
        problems.append("缺少 ffmpeg：在安装时传 -FfmpegPath，或将 ffmpeg.exe 放入 RuntimeRoot。")
    device = None
    try:
        device, _ = choose_device("auto")
    except Exception as error:
        problems.append(str(error))
    print(json.dumps({"ready": not problems, "python": sys.executable, "device": device,
                      "runtime_root": str(root), "problems": problems}, ensure_ascii=False, indent=2))
    return 1 if problems else 0


if __name__ == "__main__":
    raise SystemExit(main())
