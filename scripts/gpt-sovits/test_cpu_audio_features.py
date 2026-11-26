"""CPU CNHuBERT input and workbench routing, without loading the large checkpoint."""
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import cpu_audio_features as cpu


class CpuAudioTests(unittest.TestCase):
    def test_workbench_custom_model_uses_original_entry(self):
        with patch.dict(os.environ, {"cnhubert_base_dir": str(Path("custom") / "model")}), \
             patch.object(cpu.runpy, "run_path") as run, patch.object(cpu, "load_hubert") as load:
            cpu.main(webui=True)
            self.assertEqual(Path(run.call_args.args[0]).name, "2-get-hubert-wav32k.py")
            load.assert_not_called()

    def test_silence_rejected_and_odd_length_is_rounded_up(self):
        try:
            import numpy as np
            import soxr
        except ImportError:
            self.skipTest("This test uses the installed CPU audio dependencies.")
        # Exercise the post-decode contract, including librosa's ceil-length convention.
        path = Path("含空格 ' 的录音.wav")
        samples = np.linspace(-.5, .5, 64001, dtype="float32")
        with patch.object(cpu.subprocess, "run") as run:
            run.return_value.stdout = samples.tobytes()
            pcm, floats = cpu.prepare_wave(path)
            self.assertIn(str(path), run.call_args.args[0])
            self.assertEqual(samples.shape, pcm.shape)
            self.assertEqual((32001,), floats.shape)
            self.assertEqual("<i2", pcm.dtype.str)
            self.assertTrue(np.isfinite(floats).all())
            run.return_value.stdout = np.zeros(64001, dtype="float32").tobytes()
            with self.assertRaisesRegex(ValueError, "静音"):
                cpu.prepare_wave(path)

    def test_bad_config_is_rejected_before_checkpoint_load(self):
        try:
            import torchaudio
        except ImportError:
            self.skipTest("This test uses the installed TorchAudio runtime.")
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory)
            (path / "config.json").write_text('{"model_type":"wavlm"}', encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "模型配置"):
                cpu.load_hubert(path)

    def test_interrupted_or_stale_output_is_recomputed(self):
        try:
            import torch
            import numpy as np
        except ImportError:
            self.skipTest("This test uses the installed CPU audio dependencies.")
        with tempfile.TemporaryDirectory() as directory:
            wav = Path(directory, "test.wav")
            pt = Path(directory, "test.wav.pt")
            cpu.save_features(wav, pt, np.ones(32000, dtype="<i2"), torch.ones(1, 768, 49))
            self.assertTrue(cpu.cached_pair_is_valid(wav, pt, 0))
            self.assertFalse(cpu.cached_pair_is_valid(wav, pt, pt.stat().st_mtime_ns + 1))
            pt.write_bytes(b"bad")
            self.assertFalse(cpu.cached_pair_is_valid(wav, pt, 0))
