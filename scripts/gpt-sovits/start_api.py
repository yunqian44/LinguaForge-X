"""Start upstream api_v2.py on loopback with explicit v2 base models."""
from __future__ import annotations

import argparse
from pathlib import Path
import sys

from runtime_common import (BERT_BASE, GPT_BASE, HUBERT_BASE, MODEL_VERSION, SOVITS_BASE,
                            choose_device, local_environment, run_upstream, validate_runtime, write_json)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--runtime-root", required=True)
    parser.add_argument("--port", type=int, default=9880)
    parser.add_argument("--device", choices=("auto", "cpu", "cuda"), default="auto")
    args = parser.parse_args()
    try:
        root = Path(args.runtime_root).resolve()
        if not 1024 <= args.port <= 65535:
            raise ValueError("端口必须在 1024～65535 之间。")
        validate_runtime(root)
        device, half = choose_device(args.device)
        config_dir = root / ".linguaforge"
        config_dir.mkdir(exist_ok=True)
        config = config_dir / f"tts-{args.port}.json"
        # JSON is valid YAML and avoids interpreting user paths as YAML syntax.
        write_json(config, {"custom": {"device": device, "is_half": half, "version": MODEL_VERSION,
                                       "t2s_weights_path": str(root / GPT_BASE),
                                       "vits_weights_path": str(root / SOVITS_BASE),
                                       "bert_base_path": str(root / BERT_BASE),
                                       "cnhuhbert_base_path": str(root / HUBERT_BASE)}})
        print(f"本机 GPT-SoVITS： http://127.0.0.1:{args.port}，设备 {device}；正在载入模型。", flush=True)
        run_upstream(root, "api_v2.py", ["-a", "127.0.0.1", "-p", str(args.port), "-c", str(config)],
                     local_environment(root, device))
        return 0
    except Exception as error:
        print(str(error), file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
