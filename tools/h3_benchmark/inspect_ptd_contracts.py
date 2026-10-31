"""CPU-only node import/catalog inspection. Never loads model weights or samples."""
import importlib.util
import json
import os
from pathlib import Path
import sys
import tempfile

source, recorder, output = map(Path, sys.argv[1:4])
os.environ["CUDA_VISIBLE_DEVICES"] = ""
sys.argv = ["main.py", "--cpu"]
sys.path.insert(0, str(Path.cwd()))
import comfy.options
comfy.options.enable_args_parsing()
import folder_paths
import nodes


def package(name, path):
    spec = importlib.util.spec_from_file_location(name, path / "__init__.py", submodule_search_locations=[str(path)])
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    spec.loader.exec_module(module)
    return module


with tempfile.TemporaryDirectory(prefix="ptd-catalog-") as scratch:
    folder_paths.add_model_folder_path("ptd", scratch)
    Path(scratch, "ptd_h3_ref2va_refasymopd_6step_int8.safetensors").touch()
    module = package("lumibelle_ptd_contract", source)
    nodes.NODE_CLASS_MAPPINGS.update(module.NODE_CLASS_MAPPINGS)
    spec = importlib.util.spec_from_file_location("lumibelle_ptd_policy", recorder / "ptd.py")
    policy = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(policy)
    selected = {key: nodes.NODE_CLASS_MAPPINGS[key] for key in ("PTDH3CompactLoader", "PTDH3CompactSampler", "PTDH3CompactSigmas")}
    selected["LumibelleH3PTDLoader"] = policy.LumibelleH3PTDLoader
    catalog = {key: dict(input=cls.INPUT_TYPES(), output=cls.RETURN_TYPES, output_is_list=[False] * len(cls.RETURN_TYPES))
               for key, cls in selected.items()}
    output.write_text(json.dumps(catalog, indent=2), encoding="utf-8")
    print(json.dumps(dict(mode="cpu-only", nodes=list(catalog), modelWeightsLoaded=False, samplerCalls=0)))
