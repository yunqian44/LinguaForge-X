"""Inherited only by LinguaForge's training WebUI children; keep Gradio local."""
import functools
import importlib.abc
import importlib.machinery
import inspect
import os
import sys


def secure_blocks(module):
    original = module.Blocks.launch
    if getattr(original, "_linguaforge_local", False):
        return
    signature = inspect.signature(original)

    @functools.wraps(original)
    def launch(*args, **kwargs):
        bound = signature.bind_partial(*args, **kwargs)
        bound.arguments.update(server_name="127.0.0.1", share=False, inbrowser=False)
        return original(*bound.args, **bound.kwargs)

    launch._linguaforge_local = True
    module.Blocks.launch = launch


class _GradioLoader(importlib.abc.Loader):
    def __init__(self, original):
        self.original = original

    def create_module(self, spec):
        return self.original.create_module(spec)

    def exec_module(self, module):
        self.original.exec_module(module)
        secure_blocks(module)


class _GradioFinder(importlib.abc.MetaPathFinder):
    def find_spec(self, fullname, path=None, target=None):
        if fullname != "gradio.blocks":
            return None
        spec = importlib.machinery.PathFinder.find_spec(fullname, path)
        if spec is not None and spec.loader is not None:
            spec.loader = _GradioLoader(spec.loader)
        return spec


if os.environ.get("LINGUAFORGE_TRAINING_WEBUI") == "1":
    os.environ["GRADIO_ANALYTICS_ENABLED"] = "False"
    os.environ["GRADIO_SERVER_NAME"] = "127.0.0.1"
    os.environ["is_share"] = "False"
    sys.meta_path.insert(0, _GradioFinder())
