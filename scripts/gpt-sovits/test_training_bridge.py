"""Contract/error-path tests; these do not pretend to train an acoustic model."""
import contextlib
import io
import json
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch
import wave

import runtime_common
import train_voice


class TrainingDatasetTests(unittest.TestCase):
    def setUp(self):
        scratch = Path(__file__).resolve().parent / ".test-tmp"
        scratch.mkdir(exist_ok=True)
        self.temporary = tempfile.TemporaryDirectory(dir=scratch)
        self.root = Path(self.temporary.name)
        self.audio = self.root / "本人 样本.wav"
        self.audio.write_bytes(b"test fixture, not real audio")
        self.source = self.root / "voice.list"
        self.output = self.root / "normalized.list"

    def tearDown(self):
        self.temporary.cleanup()

    def test_unicode_relative_audio_is_normalized_without_altering_transcript(self):
        self.source.write_text("本人 样本.wav|本人|zh|你好，这是我的声音。\n", encoding="utf-8-sig")
        self.assertEqual([self.audio.name], train_voice.normalize_dataset(self.source, self.output))
        self.assertEqual(f"{self.audio}|本人|ZH|你好，这是我的声音。\n", self.output.read_text(encoding="utf-8"))

    def test_duplicate_basenames_do_not_silently_replace_features(self):
        directory = self.root / "another"
        directory.mkdir()
        (directory / self.audio.name).write_bytes(b"sample")
        self.source.write_text("本人 样本.wav|本人|ZH|第一段\nanother/本人 样本.wav|本人|ZH|第二段", encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "重复"):
            train_voice.normalize_dataset(self.source, self.output)

    def test_missing_audio_fails_before_starting_training(self):
        self.source.write_text("missing.wav|本人|ZH|你好", encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "缺少"):
            train_voice.normalize_dataset(self.source, self.output)

    def test_mixed_speakers_are_rejected_for_one_personal_voice(self):
        other = self.root / "other.wav"
        other.write_bytes(b"other")
        self.source.write_text("本人 样本.wav|本人|ZH|你好\nother.wav|他人|ZH|你好", encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "同一位"):
            train_voice.normalize_dataset(self.source, self.output)

    def test_upstream_silent_skip_is_detected(self):
        features = self.root / "2-name2text.txt"
        features.write_text("first.wav\tphonemes\n", encoding="utf-8")
        with self.assertRaisesRegex(RuntimeError, "遗漏"):
            train_voice.validate_rows(features, ["first.wav", "skipped.wav"])

    def test_invalid_epoch_does_not_start_process(self):
        for value in (0, 101, 2.5, True, "5"):
            with self.subTest(value=value), self.assertRaises(ValueError):
                train_voice.integer_option({"gpt_epochs": value}, "gpt_epochs", 5, 100)

    def test_progress_uses_zero_to_one_hundred_contract(self):
        output = io.StringIO()
        with contextlib.redirect_stdout(output):
            runtime_common.progress("train_gpt", .72, "训练 GPT")
        payload = json.loads(output.getvalue().removeprefix("LFX_PROGRESS "))
        self.assertEqual(72, payload["progress"])

    def test_subprocess_uses_argument_array_and_no_shell(self):
        with patch("runtime_common.subprocess.Popen") as popen:
            popen.return_value.wait.return_value = 0
            runtime_common.run_upstream(self.root, "script.py", ["--config", "space & quote.json"], {"X": "1"})
            self.assertEqual(["--config", "space & quote.json"], popen.call_args.args[0][-2:])
            self.assertNotIn("shell", popen.call_args.kwargs)

    def test_child_failure_does_not_report_success(self):
        with patch("runtime_common.subprocess.Popen") as popen:
            popen.return_value.wait.return_value = 7
            with self.assertRaisesRegex(RuntimeError, "退出代码 7"):
                runtime_common.run_upstream(self.root, "s2_train.py", [], {})

    def test_local_environment_disables_remote_model_fetch(self):
        env = runtime_common.local_environment(self.root, "cpu")
        self.assertEqual("1", env["HF_HUB_OFFLINE"])
        self.assertEqual("1", env["TRANSFORMERS_OFFLINE"])
        self.assertEqual("", env["CUDA_VISIBLE_DEVICES"])

    def run_pipeline_fixture(self, duration=1):
        """Exercise orchestration against tiny fake upstream outputs, never real training."""
        runtime = self.root / "runtime"
        configs = runtime / "GPT_SoVITS" / "configs"
        configs.mkdir(parents=True)
        (configs / "s2.json").write_text(json.dumps({"train": {}, "data": {}, "model": {}}))
        (configs / "s1longer-v2.yaml").write_text("unused fixture")
        self.source.write_text("本人 样本.wav|本人|ZH|你好", encoding="utf-8")
        output = self.root / "job"
        stages = []

        def fake_upstream(root, relative, arguments, env):
            stages.append(relative)
            dataset = Path(env["opt_dir"])
            dataset.mkdir(exist_ok=True)
            if relative.endswith("1-get-text.py"):
                (dataset / "2-name2text-0.txt").write_text(self.audio.name + "\tphones\n", encoding="utf-8")
            elif relative.endswith("2-get-hubert-wav32k.py"):
                (dataset / "4-cnhubert").mkdir()
                (dataset / "5-wav32k").mkdir()
                (dataset / "4-cnhubert" / (self.audio.name + ".pt")).write_bytes(b"fake feature")
                with wave.open(str(dataset / "5-wav32k" / self.audio.name), "wb") as wav:
                    wav.setnchannels(1)
                    wav.setsampwidth(2)
                    wav.setframerate(32000)
                    wav.writeframes(b"\0\0" * int(32000 * duration))
            elif relative.endswith("3-get-semantic.py"):
                (dataset / "6-name2semantic-0.tsv").write_text(self.audio.name + "\t1 2\n", encoding="utf-8")
            elif relative.endswith("s2_train.py"):
                # Real upstream does not create its checkpoint directory itself.
                self.assertTrue((dataset / "logs_s2_v2").is_dir())
                self.assertFalse((output / "result.json").exists())
                (output / "SoVITS_weights_v2" / "fixture.pth").write_bytes(b"fake test artifact")
            elif relative.endswith("s1_train.py"):
                self.assertFalse((output / "result.json").exists())
                (output / "GPT_weights_v2" / "fixture.ckpt").write_bytes(b"fake test artifact")

        yaml_stub = SimpleNamespace(safe_load=lambda text: {"train": {}, "data": {}},
                                    safe_dump=lambda data, **options: json.dumps(data))
        with patch("train_voice.validate_runtime"), patch("train_voice.choose_device", return_value=("cpu", False)), \
             patch("train_voice.run_upstream", side_effect=fake_upstream), patch.dict("sys.modules", {"yaml": yaml_stub}), \
             contextlib.redirect_stdout(io.StringIO()):
            result = train_voice.train({"runtime_root": str(runtime), "dataset_list": str(self.source),
                                        "output_directory": str(output)})
        return result, stages, output

    def test_checkpoint_directory_exists_and_result_requires_both_training_stages(self):
        result, stages, output = self.run_pipeline_fixture()
        self.assertEqual(5, len(stages))
        self.assertEqual("completed", result["status"])
        self.assertTrue(Path(result["gpt_weights_path"]).is_relative_to(output))
        self.assertTrue(Path(result["sovits_weights_path"]).is_relative_to(output))

    def test_audio_too_short_for_upstream_is_rejected_before_training(self):
        with self.assertRaisesRegex(ValueError, "大于 0.6"):
            self.run_pipeline_fixture(duration=0.5)
        self.assertFalse((self.root / "job" / "result.json").exists())

    def test_audio_too_long_for_upstream_is_rejected_before_training(self):
        with self.assertRaisesRegex(ValueError, "小于 54"):
            self.run_pipeline_fixture(duration=55)
        self.assertFalse((self.root / "job" / "result.json").exists())


if __name__ == "__main__":
    unittest.main()
