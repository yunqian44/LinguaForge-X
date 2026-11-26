"""Run the pinned upstream CPU trainer with a synchronous PyTorch DataLoader."""
from __future__ import annotations

import argparse
from functools import partial
import importlib
import os
from pathlib import Path
import sys


def make_cpu_loader(loader_type, dataset, **kwargs):
    # Worker prefetch/persistence are invalid when num_workers is zero, and
    # Windows spawn otherwise loads another copy of the ML runtime per worker.
    kwargs.pop("prefetch_factor", None)
    kwargs.update(num_workers=0, persistent_workers=False, pin_memory=False)
    return loader_type(dataset, **kwargs)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("stage", choices=("s1_train.py", "s2_train.py"))
    parser.add_argument("--config", "--config_file", "-c", dest="config", required=True)
    args = parser.parse_args()
    root = Path.cwd()
    sys.path[:0] = [str(root / "GPT_SoVITS"), str(root)]
    os.environ["CUDA_VISIBLE_DEVICES"] = os.environ["_CUDA_VISIBLE_DEVICES"] = ""
    from torch.utils.data import DataLoader

    factory = partial(make_cpu_loader, DataLoader)
    if args.stage == "s1_train.py":
        from AR.data import data_module
        import s1_train

        data_module.DataLoader = factory
        s1_train.main(argparse.Namespace(config_file=args.config))
    else:
        sys.argv = [str(root / "GPT_SoVITS" / args.stage), "--config", args.config]
        trainer = importlib.import_module("s2_train")  # Parses --config at import.
        os.environ["CUDA_VISIBLE_DEVICES"] = ""
        trainer.hps.train.fp16_run = False

        trainer.DataLoader = factory
        trainer.main()


if __name__ == "__main__":
    main()
