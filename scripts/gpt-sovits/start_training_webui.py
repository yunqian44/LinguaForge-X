"""Launch the official GPT-SoVITS training UI with local desktop boundaries.

The v2 workbench shares the desktop trainer's CPU feature and loader adapters.
"""
from __future__ import annotations

import argparse
import ast
import importlib
import os
from pathlib import Path
import socket
import sys

from runtime_common import choose_device, local_environment, required_file, validate_runtime


def prepare_source(path: Path, cpu: bool = False):
    tree = ast.parse(path.read_text(encoding="utf-8-sig"), filename=str(path))
    matches = 0
    for node in tree.body:
        if isinstance(node, ast.Assign) and any(isinstance(t, ast.Name) and t.id == "version" for t in node.targets):
            if not isinstance(node.value, ast.Constant) or not isinstance(node.value.value, str):
                raise ValueError("上游 WebUI 版本初始化方式发生变化，请更新应用适配器。")
            node.value = ast.copy_location(ast.Constant("v2"), node.value)
            matches += 1
    if matches != 1:
        raise ValueError("无法确认官方 WebUI 的模型版本入口，请使用本应用安装的 GPT-SoVITS 版本。")
    if cpu:
        # Replace only the known Python commands in the pinned WebUI. Using -m
        # keeps the integration directory out of shell-formatted command strings.
        commands = {
            "GPT_SoVITS/s1_train.py": ("-m cpu_train s1_train.py", 1),
            "GPT_SoVITS/s2_train.py": ("-m cpu_train s2_train.py", 1),
            "GPT_SoVITS/prepare_datasets/2-get-hubert-wav32k.py": ("-m cpu_audio_features --webui", 2),
        }
        counts = dict.fromkeys(commands, 0)
        for node in ast.walk(tree):
            if not isinstance(node, ast.Constant) or not isinstance(node.value, str):
                continue
            for script, (replacement, _) in commands.items():
                prefix = '"%s" -s ' + script
                if node.value == prefix or node.value.startswith(prefix + ' '):
                    node.value = '"%s" -s ' + replacement + node.value[len(prefix):]
                    counts[script] += 1
        if any(counts[k] != expected for k, (_, expected) in commands.items()):
            raise ValueError("无法确认此版 GPT-SoVITS 的 CPU 训练命令，请检查运行目录版本。")
    return compile(ast.fix_missing_locations(tree), str(path), "exec")


def validate_port(port: int):
    if not 1024 <= port <= 65535:
        raise ValueError("训练界面端口必须在 1024～65535 之间。")
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as listener:
        if hasattr(socket, "SO_EXCLUSIVEADDRUSE"):
            listener.setsockopt(socket.SOL_SOCKET, socket.SO_EXCLUSIVEADDRUSE, 1)
        listener.bind(("127.0.0.1", port))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--runtime-root", required=True)
    parser.add_argument("--port", type=int, default=9874)
    parser.add_argument("--device", choices=("auto", "cpu", "cuda"), default="auto")
    args = parser.parse_args()
    root = Path(args.runtime_root).resolve()
    entry = required_file(root / "webui.py")
    validate_runtime(root, training=True)
    validate_port(args.port)
    device, _ = choose_device(args.device)
    source = prepare_source(entry, cpu=device == "cpu")
    os.environ.update(local_environment(root, device))
    bootstrap = Path(__file__).resolve().parent / "training_webui_bootstrap"
    required_file(bootstrap / "sitecustomize.py")
    os.environ["PYTHONPATH"] = os.pathsep.join([str(bootstrap), str(bootstrap.parent), os.environ["PYTHONPATH"]])
    os.environ["LINGUAFORGE_TRAINING_WEBUI"] = "1"
    os.environ["is_share"] = "False"
    os.environ["GRADIO_SERVER_NAME"] = "127.0.0.1"
    os.environ["NO_PROXY"] = "127.0.0.1,localhost,::1"
    os.chdir(root)
    sys.path[:0] = [str(bootstrap), str(root), str(root / "GPT_SoVITS"), str(root / "tools")]
    try:
        guard = importlib.import_module("sitecustomize")
        import gradio.blocks
        guard.secure_blocks(gradio.blocks)
        import gradio
        if gradio.__version__ != "4.44.1":
            raise ValueError("训练界面需要已验证的 gradio==4.44.1，请重新运行 Setup-GptSoVits.ps1 -InstallDependencies。")
        import config
    except ImportError as error:
        raise ValueError("缺少训练界面依赖，请运行 Setup-GptSoVits.ps1 -InstallDependencies：" + str(error)) from error
    config.is_share = False
    config.webui_port_main = args.port
    config.python_exec = sys.executable
    sys.argv = [str(entry), "zh_CN"]
    print(f"LFX_WEBUI_STARTING http://127.0.0.1:{args.port}/ device={device} version=v2", flush=True)
    exec(source, {"__name__": "__main__", "__file__": str(entry)})


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        print("LFX_WEBUI_ERROR " + str(error), file=sys.stderr, flush=True)
        raise
