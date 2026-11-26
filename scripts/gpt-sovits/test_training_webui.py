"""Boundary tests for the official training UI adapter; no models or training needed."""
import importlib.util
from pathlib import Path
import socket
import tempfile
import types
import unittest

from start_training_webui import prepare_source, validate_port


class TrainingUiTests(unittest.TestCase):
    def test_cpu_commands_for_individual_and_combined_dataset_preparation(self):
        audio = '"%s" -s GPT_SoVITS/prepare_datasets/2-get-hubert-wav32k.py'
        gpt = '"%s" -s GPT_SoVITS/s1_train.py --config_file "%s"'
        sovits = '"%s" -s GPT_SoVITS/s2_train.py --config "%s"'
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "webui.py"
            path.write_text("version='v2Pro'\ncommands=" + repr([audio, audio, gpt, sovits]), encoding="utf-8")
            cpu, gpu = {}, {}
            exec(prepare_source(path, cpu=True), cpu)
            exec(prepare_source(path), gpu)
            self.assertEqual(cpu["commands"], [
                '"%s" -s -m cpu_audio_features --webui', '"%s" -s -m cpu_audio_features --webui',
                '"%s" -s -m cpu_train s1_train.py --config_file "%s"',
                '"%s" -s -m cpu_train s2_train.py --config "%s"'])
            self.assertEqual(gpu["commands"], [audio, audio, gpt, sovits])

    def test_changes_only_default_version_in_memory(self):
        with tempfile.TemporaryDirectory() as folder:
            source = Path(folder) / "webui.py"
            original = 'import os\nos.environ["version"] = version = "v2Pro"\nresult = (version, 40 + 2)\n'
            source.write_text(original, encoding="utf-8")
            namespace = {}
            exec(prepare_source(source), namespace)
            self.assertEqual(namespace["result"], ("v2", 42))
            self.assertEqual(source.read_text(encoding="utf-8"), original)

    def test_unknown_entry_rejected(self):
        for content in ('version = obtain_version()', 'different_version = "v2"', 'version="v1"\nversion="v2"'):
            with self.subTest(content=content), tempfile.TemporaryDirectory() as folder:
                source = Path(folder) / "webui.py"
                source.write_text(content, encoding="utf-8")
                with self.assertRaises(ValueError):
                    prepare_source(source)

    def test_invalid_and_occupied_ports_rejected(self):
        for port in (0, 80, -1, 65536):
            with self.assertRaises(ValueError):
                validate_port(port)
        with socket.socket() as listener:
            listener.bind(("127.0.0.1", 0))
            listener.listen()
            with self.assertRaises(OSError):
                validate_port(listener.getsockname()[1])

    def test_guard_overrides_positional_and_keyword_remote_launch(self):
        path = Path(__file__).parent / "training_webui_bootstrap" / "sitecustomize.py"
        spec = importlib.util.spec_from_file_location("test_guard", path)
        guard = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(guard)

        class Blocks:
            def launch(self, server_name=None, share=None, inbrowser=None, server_port=None):
                return server_name, share, inbrowser, server_port

        module = types.SimpleNamespace(Blocks=Blocks)
        guard.secure_blocks(module)
        first = Blocks.launch
        guard.secure_blocks(module)
        self.assertIs(first, Blocks.launch)
        self.assertEqual(Blocks().launch("0.0.0.0", True, True, 9874), ("127.0.0.1", False, False, 9874))
        self.assertEqual(Blocks().launch(server_name="::", share=True, inbrowser=True), ("127.0.0.1", False, False, None))


if __name__ == "__main__":
    unittest.main()
