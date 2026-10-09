"""Verify retained source hashes and held latent context in a live motion smoke archive.

Uses only Python's standard library; it never contacts ComfyUI or uses the GPU.
"""
import argparse
import array
import hashlib
import json
import math
from pathlib import Path
import struct


def tensor(path, key):
    data = path.read_bytes()
    size = struct.unpack("<Q", data[:8])[0]
    entry = json.loads(data[8:8 + size])[key]
    first, last = entry["data_offsets"]
    raw = data[8 + size + first:8 + size + last]
    if entry["dtype"] == "F32":
        values = array.array("f")
        values.frombytes(raw)
    elif entry["dtype"] == "F16":
        values = [v[0] for v in struct.iter_unpack("<e", raw)]
    elif entry["dtype"] == "BF16":
        values = [struct.unpack("<f", struct.pack("<I", v[0] << 16))[0] for v in struct.iter_unpack("<H", raw)]
    else:
        raise ValueError("Unsupported tensor dtype: " + entry["dtype"])
    return entry["shape"], values


def compare(source, source_key, output, output_key, axis, held):
    shape, before = tensor(source, source_key)
    after_shape, after = tensor(output, output_key)
    assert shape == after_shape, (shape, after_shape)
    stride = math.prod(shape[axis + 1:])
    planes = math.prod(shape[:axis])
    maximum = total = 0.0
    count = planes * held * stride
    for plane in range(planes):
        start = plane * shape[axis] * stride
        for i in range(start, start + held * stride):
            difference = abs(before[i] - after[i])
            assert math.isfinite(difference)
            maximum = max(maximum, difference)
            total += difference
    # Sampling's normalization/un-normalization can introduce float32 rounding.
    assert maximum <= 1e-6, (source, output, maximum)
    return {"shape": shape, "heldSteps": held, "maxAbsoluteDifference": maximum, "meanAbsoluteDifference": total / count}


def verify(directory):
    results = []
    for case in sorted(directory.iterdir()):
        if not (case / "take.json").is_file():
            continue
        take = json.loads((case / "take.json").read_text(encoding="utf-8-sig"))
        folder = case / "take"
        request = json.loads((case / "request.json").read_text(encoding="utf-8-sig"))
        manifest = (take.get("extension") or {}).get("sourceFiles", [])
        for file in manifest:
            data = (folder / "extension-source" / file["fileName"]).read_bytes()
            assert len(data) == file["bytes"]
            assert hashlib.sha256(data).hexdigest().upper() == file["sha256"].upper()
        result = {"case": case.name, "retainedFilesVerified": len(manifest)}
        motion = take["snapshot"].get("motion")
        if motion and motion["route"] == "SavedLatents":
            refinement = request.get("refinement")
            for name, axis, held in [("video", 2, 12), ("audio", 3, 65)]:
                source = folder / "refinement-inputs" / ("motion-" + name + ".latent")
                key = "latent_tensor"
                if refinement:
                    source = folder / "refinement-inputs" / "refinement-source.safetensors"
                    if not source.is_file():
                        source = folder / "extension-source" / "refinement.safetensors"
                    key = name
                result[name] = compare(source, key, folder / "refinement.safetensors", name, axis, held)
        results.append(result)
    assert results, "No completed live cases were found."
    return results


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path)
    options = parser.parse_args()
    results = verify(options.directory)
    output = options.directory / "latent-verification.json"
    output.write_text(json.dumps(results, indent=2), encoding="utf-8")
    print(json.dumps(results, indent=2))
