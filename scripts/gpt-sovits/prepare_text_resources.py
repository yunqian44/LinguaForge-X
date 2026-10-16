"""Explicit setup-time downloads only. Never invoked by training or synthesis."""
import argparse
import os
from pathlib import Path
import sys

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--runtime-root", required=True)
    args = parser.parse_args()
    root = Path(args.runtime_root).resolve()
    # Upstream portable_runtime.activate explicitly selects this path for api_v2.
    nltk_path = root / "runtime" / "nltk_data"
    nltk_path.mkdir(parents=True, exist_ok=True)
    os.environ["NLTK_DATA"] = str(nltk_path)
    import nltk
    for package in ("averaged_perceptron_tagger", "averaged_perceptron_tagger_eng", "cmudict"):
        if not nltk.download(package, download_dir=str(nltk_path), quiet=True, raise_on_error=True):
            raise RuntimeError("无法准备 NLTK 资源：" + package)
    import fast_langdetect
    cache = root / "GPT_SoVITS" / "pretrained_models" / "fast_langdetect"
    cache.mkdir(parents=True, exist_ok=True)
    detector = fast_langdetect.infer.LangDetector(fast_langdetect.infer.LangDetectConfig(cache_dir=cache))
    fast_langdetect.infer._default_detector = detector
    fast_langdetect.detect("你好，这是中文语音合成。", model="full")
    print("文本资源已准备到运行环境目录。", flush=True)


if __name__ == "__main__":
    main()
