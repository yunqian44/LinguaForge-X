"""Train a real v2 personal voice using the upstream preparation/training CLIs.

No fake model/result is emitted. The .list is a reviewed local transcript dataset.
The caller must use a unique output directory and kill this process tree on cancel.
"""
from __future__ import annotations

import argparse
from datetime import datetime, timezone
import json
from pathlib import Path
import re
import shutil
import sys
import wave

from runtime_common import (BERT_BASE, GPT_BASE, HUBERT_BASE, MODEL_VERSION, SOVITS_BASE,
                            SOVITS_DISCRIMINATOR, UPSTREAM_COMMIT, choose_device,
                            local_environment, progress, required_file, run_upstream,
                            validate_runtime, write_json)


def normalize_dataset(source: Path, destination: Path) -> list[str]:
    rows, names = [], set()
    languages = {"zh": "ZH", "en": "EN", "ja": "JA", "jp": "JA", "ko": "KO", "yue": "YUE"}
    for number, line in enumerate(source.read_text(encoding="utf-8-sig").splitlines(), 1):
        if not line.strip():
            continue
        fields = line.split("|")
        if len(fields) != 4:
            raise ValueError(f"标注第 {number} 行应为 音频路径|说话人|语言|准确转写，不能包含额外的 |。")
        audio_text, speaker, language, text = (part.strip() for part in fields)
        audio = Path(audio_text.strip('"'))
        if not audio.is_absolute():
            audio = source.parent / audio
        audio = required_file(audio.resolve())
        if audio.name.casefold() in names:
            raise ValueError(f"音频文件名重复：{audio.name}；官方预处理按文件名关联，请先重命名。")
        if not speaker or not text or language.lower() not in languages:
            raise ValueError(f"标注第 {number} 行缺少说话人/转写，或语言不是 ZH/EN/JA/KO/YUE。")
        if any(character in text for character in ("\t", "\x00")):
            raise ValueError(f"标注第 {number} 行不能包含制表符或空字符。")
        names.add(audio.name.casefold())
        rows.append((audio, speaker, languages[language.lower()], text))
    if not rows:
        raise ValueError("标注文件为空；请导入本人录音并逐片校对转写。")
    if len({row[1] for row in rows}) != 1:
        raise ValueError("一次个人音色训练只能包含同一位说话人。")
    destination.write_text("\n".join("|".join(map(str, row)) for row in rows) + "\n", encoding="utf-8")
    return [row[0].name for row in rows]


def validate_rows(path: Path, names: list[str]) -> None:
    found = {line.split("\t", 1)[0] for line in required_file(path).read_text(encoding="utf-8").splitlines() if line.strip()}
    missing = set(names) - found
    if missing:
        raise RuntimeError(f"预处理遗漏 {len(missing)} 段录音（例如 {sorted(missing)[0]}）；请检查录音和日志，不会用不完整数据继续训练。")


def integer_option(data: dict, name: str, default: int, maximum: int) -> int:
    value = data.get(name, default)
    if isinstance(value, bool) or not isinstance(value, int) or not 1 <= value <= maximum:
        raise ValueError(f"{name} 必须为 1～{maximum} 的整数。")
    return value


def train(manifest: dict) -> dict:
    if manifest.get("schema_version", 1) != 1:
        raise ValueError("不支持此训练清单版本。")
    root = Path(manifest["runtime_root"]).resolve()
    source = required_file(Path(manifest["dataset_list"]).resolve())
    output = Path(manifest["output_directory"]).resolve()
    if output == root or output in root.parents or output == source.parent:
        raise ValueError("训练输出必须使用独立的任务子目录。")
    gpt_epochs = integer_option(manifest, "gpt_epochs", 5, 100)
    sovits_epochs = integer_option(manifest, "sovits_epochs", 8, 100)
    batch = integer_option(manifest, "batch_size", 2, 32)
    validate_runtime(root, training=True)
    output.mkdir(parents=True, exist_ok=True)
    # A new run is required: never silently reuse old features or report stale weights.
    dataset = output / "dataset"
    if dataset.exists() or (output / "result.json").exists():
        raise ValueError("输出目录已有训练结果或中间数据，请创建新的训练任务。")
    normalized = output / "training.list"
    names = normalize_dataset(source, normalized)
    device, half = choose_device(manifest.get("device", "auto"))
    progress("validate", 0.03, f"已校验 {len(names)} 段标注；使用 {device}，CPU 训练耗时取决于硬件与数据量。")
    env = local_environment(root, device)
    experiment = "lfx_" + re.sub(r"[^a-zA-Z0-9_]", "_", output.name)[-48:]
    env.update({"inp_text": str(normalized), "inp_wav_dir": "", "exp_name": experiment,
                "opt_dir": str(dataset), "i_part": "0", "all_parts": "1",
                "bert_pretrained_dir": str(root / BERT_BASE),
                "cnhubert_base_dir": str(root / HUBERT_BASE),
                "pretrained_s2G": str(root / SOVITS_BASE),
                "s2config_path": str(root / "GPT_SoVITS/configs/s2.json"), "hz": "25hz"})
    if manifest.get("ffmpeg_path"):
        import os
        executable = required_file(Path(manifest["ffmpeg_path"]).resolve())
        env["PATH"] = str(executable.parent) + os.pathsep + env["PATH"]
    progress("text_features", 0.08, "提取文本和中文 BERT 特征。")
    run_upstream(root, "GPT_SoVITS/prepare_datasets/1-get-text.py", [], env)
    text_parts = dataset / "2-name2text-0.txt"
    validate_rows(text_parts, names)
    shutil.copyfile(text_parts, dataset / "2-name2text.txt")
    progress("audio_features", 0.20, "提取录音特征并转换为 32 kHz 音频。")
    run_upstream(root, "GPT_SoVITS/prepare_datasets/2-get-hubert-wav32k.py", [], env)
    for name in names:
        required_file(dataset / "4-cnhubert" / (name + ".pt"))
        wav_path = required_file(dataset / "5-wav32k" / name)
        with wave.open(str(wav_path), "rb") as audio:
            duration = audio.getnframes() / audio.getframerate()
        if not 0.6 < duration < 54:
            raise ValueError(f"录音 {name} 为 {duration:.2f} 秒；官方训练样本必须大于 0.6 秒且小于 54 秒，请按句切片并校对标注。")
    progress("semantic_features", 0.35, "提取语义标记。")
    run_upstream(root, "GPT_SoVITS/prepare_datasets/3-get-semantic.py", [], env)
    semantic_parts = dataset / "6-name2semantic-0.tsv"
    validate_rows(semantic_parts, names)
    (dataset / "6-name2semantic.tsv").write_text(
        "item_name\tsemantic_audio\n" + semantic_parts.read_text(encoding="utf-8").rstrip() + "\n", encoding="utf-8")
    import yaml
    sovits_dir, gpt_dir = output / "SoVITS_weights_v2", output / "GPT_weights_v2"
    sovits_dir.mkdir()
    gpt_dir.mkdir()
    # Upstream save_checkpoint expects this directory, just as webui.open1Ba creates it.
    (dataset / "logs_s2_v2").mkdir(exist_ok=True)
    s2 = json.loads((root / "GPT_SoVITS/configs/s2.json").read_text(encoding="utf-8"))
    s2["train"].update({"batch_size": batch, "epochs": sovits_epochs, "fp16_run": half,
                         "text_low_lr_rate": 0.4, "pretrained_s2G": str(root / SOVITS_BASE),
                         "pretrained_s2D": str(root / SOVITS_DISCRIMINATOR), "if_save_latest": True,
                         "if_save_every_weights": True, "save_every_epoch": 1,
                         "gpu_numbers": "0" if device == "cuda" else "", "grad_ckpt": False, "lora_rank": 32})
    s2["model"]["version"] = MODEL_VERSION
    s2["data"]["exp_dir"] = str(dataset)
    s2.update({"s2_ckpt_dir": str(dataset), "save_weight_dir": str(sovits_dir),
               "name": experiment, "version": MODEL_VERSION})
    s2_path = output / "s2.json"
    write_json(s2_path, s2)
    s1 = yaml.safe_load((root / "GPT_SoVITS/configs/s1longer-v2.yaml").read_text(encoding="utf-8"))
    s1["train"].update({"batch_size": batch, "epochs": gpt_epochs, "precision": "32",
                         "save_every_n_epoch": 1, "if_save_every_weights": True, "if_save_latest": True,
                         "if_dpo": False, "half_weights_save_dir": str(gpt_dir), "exp_name": experiment})
    s1["data"]["num_workers"] = 0 if device == "cpu" else 2
    s1.update({"pretrained_s1": str(root / GPT_BASE), "train_semantic_path": str(dataset / "6-name2semantic.tsv"),
               "train_phoneme_path": str(dataset / "2-name2text.txt"),
               "output_dir": str(dataset / "logs_s1_v2")})
    s1_path = output / "s1.yaml"
    s1_path.write_text(yaml.safe_dump(s1, allow_unicode=True), encoding="utf-8")
    progress("train_sovits", 0.45, f"开始 SoVITS 微调，共 {sovits_epochs} 轮；具体轮次见训练日志。")
    run_upstream(root, "GPT_SoVITS/s2_train.py", ["--config", str(s2_path)], env)
    progress("train_gpt", 0.72, f"开始 GPT 微调，共 {gpt_epochs} 轮；具体轮次见训练日志。")
    run_upstream(root, "GPT_SoVITS/s1_train.py", ["--config_file", str(s1_path)], env)
    sovits_models = sorted(sovits_dir.glob("*.pth"), key=lambda item: item.stat().st_mtime_ns)
    gpt_models = sorted(gpt_dir.glob("*.ckpt"), key=lambda item: item.stat().st_mtime_ns)
    if not sovits_models or not gpt_models:
        raise RuntimeError("训练进程结束，但未生成两种可用权重；请检查训练日志。")
    result = {"schema_version": 1, "status": "completed", "model_version": MODEL_VERSION,
              "gpt_weights_path": str(required_file(gpt_models[-1])),
              "sovits_weights_path": str(required_file(sovits_models[-1])),
              "sample_count": len(names), "device": device,
              "upstream_compatibility_commit": UPSTREAM_COMMIT,
              "completed_at": datetime.now(timezone.utc).isoformat()}
    write_json(output / "result.json", result)
    progress("completed", 1.0, "GPT 和 SoVITS 训练完成，权重已保存到本地。")
    return result


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", required=True)
    args = parser.parse_args()
    manifest = None
    try:
        manifest = json.loads(Path(args.manifest).read_text(encoding="utf-8-sig"))
        train(manifest)
        return 0
    except Exception as error:
        print(f"LFX_ERROR {error}", file=sys.stderr, flush=True)
        if isinstance(manifest, dict) and manifest.get("output_directory"):
            output = Path(manifest["output_directory"])
            if output.is_dir():
                write_json(output / "error.json", {"status": "failed", "message": str(error)})
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
