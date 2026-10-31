"""Freeze isolated candidate graphs before enabling application discovery.

These prototype requests are explicitly experimental, not production job imports.
They retain the actual graph, full checkpoint/adapter distinction, input hashes,
runtime identities and all sampler/attention overrides.
"""
import copy
import hashlib
import json
from pathlib import Path
import sys
import time
from datetime import datetime, timezone
import urllib.request
import uuid

root, candidate = Path(sys.argv[1]), sys.argv[2]
if candidate not in ("10eros", "smd", "fasth3"):
    raise ValueError("Choose 10eros, smd or fasth3")
name = candidate + "-draft"
target = root / "requests" / (name + ".json")
if target.exists():
    raise RuntimeError("This candidate is already captured; do not overwrite its recipe")
reviews = json.loads((root / "reviews.json").read_text("utf-8-sig"))
if any(reviews[scene]["status"] != "accepted" for scene in ("dialogue", "movement")):
    raise RuntimeError("The promotion gate is unresolved")
if candidate == "fasth3" and not any(reviews.get(key, {}).get("status") == "accepted" for key in ("10eros", "smd")):
    raise RuntimeError("FastH3 requires an accepted promoted final from an initial candidate")
started_utc, started = datetime.now(timezone.utc).isoformat(), time.monotonic()
source = json.loads((root / "requests/dialogue-control.json").read_text("utf-8-sig"))
request, workflow = copy.deepcopy(source["request"]), copy.deepcopy(source["workflow"])
identity, seed = str(uuid.uuid4()), 20261030
request["batchId"] = identity
snapshot = request["snapshot"]
snapshot["shot"]["generationPreset"] = candidate + "-prototype"
snapshot["shot"]["saveLatents"] = False
snapshot["captureRefinementData"] = False
snapshot["settings"]["model"] = {
    "10eros": "10Eros_Max_h3_8stepTURBO_beta7_activation_learned_compact_w4w6int8_14.65gb.safetensors",
    "smd": "minimax_h3_ref2va_pruned_int8_convrot.safetensors",
    "fasth3": "fasth3_omniref_8step_pruned_int8_convrot.safetensors"
}[candidate]
adapter = "SMD-MiniMax-H3-Ref2VA-4step.safetensors" if candidate == "smd" else None
steps = 4 if adapter else 8
sampling = dict(profile=candidate + "-isolated-v1", steps=steps, sampler="euler", scheduler="manual" if adapter else "simple",
                videoShift=12, audioShift=3, lora=adapter, loraStrength=0 if adapter is None else 1)
snapshot["sampling"] = sampling
details = dict(kind="complete-checkpoint" if adapter is None else "adapter", model=snapshot["settings"]["model"],
               adapter=adapter, sampling=sampling, guider="BasicGuider", additionalAccelerationAdapters=False)
graph = workflow["prompt"]
for node in ("21", "22", "20", "15"):
    graph.pop(node, None)
graph["1"]["inputs"]["unet_name"] = snapshot["settings"]["model"]
model = "1"
if adapter:
    graph["candidate-adapter"] = dict(class_type="LoraLoaderModelOnly", inputs=dict(model=[model, 0], lora_name=adapter, strength_model=1))
    model = "candidate-adapter"
graph["candidate-shift"] = dict(class_type="MiniMaxH3SigmaShift", inputs=dict(model=[model, 0], shift_video=12, shift_audio=3))
model = "candidate-shift"
if candidate in ("10eros", "fasth3"):
    sparse = {"model": [model, 0], "selection": "vsa", "selection.keep_percent": 10.0, "start_percent": 0.0,
              "end_percent": 1.0, "dense_blocks": "0,1" if candidate == "10eros" else "", "min_tokens": 12288, "extra_tokens": 256,
              "sink_conditioning": "exact_kv_and_rows", "verbose": False}
    graph["candidate-vsa"] = dict(class_type="BlockSparseAttention", inputs=sparse)
    graph["candidate-attention"] = dict(class_type="ModelAttentionBackend", inputs=dict(model=["candidate-vsa", 0], attention="comfy kitchen attention"))
    model = "candidate-attention"
    details["attention"] = {**sparse, "backend": "comfy kitchen attention"}
    graph["9"] = dict(class_type="BasicScheduler", inputs=dict(model=["candidate-shift", 0], scheduler="simple", steps=8, denoise=1.0))
else:
    grid = "1.0000000000, 0.9677419355, 0.9000000000, 0.6666666667, 0.0000000000"
    details["sigmas"] = grid
    details["encoder"] = "Existing W4A8 encoder retained; differs from the publisher's NVFP4-AWQ compact encoder"
    graph["9"] = dict(class_type="ManualSigmas", inputs=dict(sigmas=grid))
graph["6"]["inputs"]["model"] = [model, 0]
graph["8"] = dict(class_type="KSamplerSelect", inputs=dict(sampler_name="euler"))
graph["7"]["inputs"]["noise_seed"] = seed
graph["14"]["inputs"]["filename_prefix"] = "lumibelle/" + identity + "/video"
graph["h3-recipe-probe"]["inputs"].update(seed=seed, attempt_id=identity)
snapshot["preset"] = dict(version="isolated-acceleration-prototype-v1", key=candidate, checkpoint=adapter, inputs=details)
workflow["client_id"] = identity
models = [dict(category=category, fileName=snapshot["settings"][key]) for category, key in
          (("diffusion_models", "model"), ("text_encoders", "encoder"), ("vae", "videoVae"), ("vae", "audioVae"))]
if adapter:
    models.append(dict(category="loras", fileName=adapter))
body = json.dumps(dict(models=models)).encode()
http_request = urllib.request.Request("http://127.0.0.1:18188/lumibelle/h3/identities", body, {"Content-Type": "application/json"})
with urllib.request.urlopen(http_request, timeout=600) as response:
    evidence = json.load(response)
expected_model = {
    "10eros": "64bf10e2ef650c2484c60a0eb3bc495bba707aadc50281361ad0dd8bba7c7b1e",
    "smd": "9255f52b6677845ad238f20dfaafa94727053694127ab7f255c048f0f9365779",
    "fasth3": "851b2dc9670ed864b7f1e951ac9da047d83b1c04ded9bfcc1a9e5bba0ee33f80"
}[candidate]
if evidence["models"][0]["sha256"].lower() != expected_model:
    raise RuntimeError("The installed transformer is not the pinned candidate checkpoint")
parameters = dict(kind="isolated-candidate-validation-v1", snapshot=snapshot, inputs=request["inputs"], effectiveRecipe=details,
                  seed=seed, dimensions=[snapshot["width"], snapshot["height"]], frames=snapshot["frameCount"])
recipe = dict(version="h3-generation-recipe-v1", parameters=parameters, **evidence, probeAvailable=True)
recipe["fingerprint"] = hashlib.sha256(json.dumps(recipe, sort_keys=True, separators=(",", ":")).encode()).hexdigest().upper()
request["recipe"] = recipe
graph["h3-recipe-probe"]["inputs"]["recipe_json"] = json.dumps(recipe, separators=(",", ":"))
captured = dict(name=name, seed=seed, candidateId=identity, request=request, workflow=workflow, experimental=True,
                preparationStartedUtc=started_utc, capturedUtc=datetime.now(timezone.utc).isoformat(), preparationSeconds=time.monotonic()-started,
                compatibility="Prototype graph; production discovery remains disabled until actual loading and generation are validated",
                expected=source["expected"])
target.write_text(json.dumps(captured, indent=2))
print(target)
