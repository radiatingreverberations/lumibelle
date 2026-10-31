"""One explicitly exported promotion case, on the isolated server only. No automatic retries."""
import argparse
import asyncio
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import time

import aiohttp


async def run(root, name, maximum_seconds):
    budget_path = root / "budget.json"
    if budget_path.exists() and json.loads(budget_path.read_text("utf-8-sig")).get("stoppedByUser"):
        raise RuntimeError("The user ended this benchmark. Further GPU work requires a new instruction to resume testing.")
    target = root / "results" / (name + ".json")
    if target.exists():
        raise RuntimeError("This case already has a result; never automatically repeat inference")
    request_path = root / "requests" / (name + ".json")
    request = json.loads(request_path.read_text("utf-8-sig"))
    candidate_experiment = request.get("experimental") or name.startswith(("10eros-", "smd-", "fasth3-"))
    if candidate_experiment:
        reviews = json.loads((root / "reviews.json").read_text("utf-8-sig"))
        if any(reviews[scene]["status"] != "accepted" for scene in ("dialogue", "movement")):
            raise RuntimeError("Both promoted finals must be accepted before testing new accelerators")
        if name.startswith("fasth3-") and not any(reviews.get(key, {}).get("status") == "accepted" for key in ("10eros", "smd")):
            raise RuntimeError("FastH3 requires an accepted initial candidate promotion")
    captured_files = [(root / "inputs" / item["fileName"], item) for item in request["request"]["inputs"]]
    captured_files += [(root / item["fileName"], item) for item in request.get("retainedLatents", [])]
    for path, item in captured_files:
        with path.open("rb") as stream:
            digest = hashlib.file_digest(stream, "sha256").hexdigest().upper()
        if path.stat().st_size != item["bytes"] or digest != item["sha256"]:
            raise RuntimeError("Captured media changed; export a new request instead of replacing its inputs: " + path.name)
    budget_file = root / "budget.json"
    budget = json.loads(budget_file.read_text()) if budget_file.exists() else dict(targetSeconds=7200, promotionReservedSeconds=3600, consumedSeconds=0, runs=[])
    stage_kind = name.split("-")[1]
    # Draft/promotion attempts stay in the reserved hour. Required baseline controls
    # can finish against the shared total, reducing later accelerator time.
    limit = budget["targetSeconds"] if candidate_experiment or stage_kind in ("control", "refine") else budget["promotionReservedSeconds"]
    if budget["consumedSeconds"] >= limit:
        raise RuntimeError("Case allocation is consumed; record remaining cases as unrun")
    remaining = limit - budget["consumedSeconds"]
    estimate = {"draft": 360, "control": 360, "final": 900, "refine": 450, "recorder": 90}.get(stage_kind, maximum_seconds)
    previous = [r["seconds"] for r in budget["runs"] if r["status"] == "complete" and r["name"].split("-")[1] == stage_kind]
    if previous:
        estimate = max(previous) * 1.2
    if remaining < estimate:
        raise RuntimeError("Not enough remaining time for this case; leave it unrun")
    if stage_kind == "final" and sum(r["name"].startswith(name.split("-")[0] + "-final-") for r in budget["runs"]) >= 2:
        raise RuntimeError("This scene has already used its two promotion attempts")
    timeout = aiohttp.ClientTimeout(total=30)
    async with aiohttp.ClientSession(timeout=timeout) as client:
        async def get(path, production=False):
            base = "http://host.docker.internal:8188" if production else "http://127.0.0.1:8188"
            async with client.get(base + path) as response:
                response.raise_for_status()
                return await response.json()
        production = await get("/queue", True)
        if production["queue_running"] or production["queue_pending"]:
            raise RuntimeError("Production has work; no benchmark was started")
        queue = await get("/queue")
        if queue["queue_running"] or queue["queue_pending"]:
            raise RuntimeError("The isolated server already has work")
        started = time.monotonic()
        result = dict(name=name, candidateId=request["candidateId"], seed=request["seed"], status="running",
                      startedUtc=datetime.now(timezone.utc).isoformat(),
                      controllerSha256=hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
                      requestSha256=hashlib.sha256(request_path.read_bytes()).hexdigest(),
                      memorySamples=[], events=[], attentionExecution="unobserved")
        budget["runs"].append(dict(name=name, status="running", seconds=0))
        budget_file.write_text(json.dumps(budget, indent=2))
        prompt_id = None
        try:
            async with client.ws_connect("http://127.0.0.1:8188/ws?clientId=" + request["candidateId"]) as ws:
                async with client.post("http://127.0.0.1:8188/prompt", json=request["workflow"]) as response:
                    body = await response.json()
                    if response.status != 200:
                        raise RuntimeError("Workflow validation failed: " + json.dumps(body))
                    prompt_id = body["prompt_id"]
                result["promptId"] = prompt_id
                stage = "preparation"
                while True:
                    elapsed = time.monotonic() - started
                    if elapsed > maximum_seconds:
                        raise RuntimeError("Case watchdog expired")
                    production = await get("/queue", True)
                    if production["queue_running"] or production["queue_pending"]:
                        raise RuntimeError("Production work arrived; isolated case interrupted")
                    stats = await get("/system_stats")
                    device = stats["devices"][0]
                    result["memorySamples"].append(dict(seconds=elapsed, stage=stage, vramUsed=device["vram_total"]-device["vram_free"],
                                                         torchReserved=device["torch_vram_total"], ramFree=stats["system"]["ram_free"],
                                                         ramTotal=stats["system"]["ram_total"]))
                    try:
                        message = await ws.receive(timeout=2)
                        if message.type == aiohttp.WSMsgType.TEXT:
                            event = json.loads(message.data)
                            if event.get("data", {}).get("prompt_id", prompt_id) == prompt_id:
                                if event["type"] in ("executing", "progress", "execution_cached", "execution_error"):
                                    result["events"].append(dict(seconds=elapsed, **event))
                                    live = root / "live"
                                    live.mkdir(exist_ok=True)
                                    (live / (name + ".json")).write_text(json.dumps(dict(
                                        status="running", seconds=elapsed, event=event["type"], data=event["data"])))
                                node = str(event.get("data", {}).get("node", ""))
                                if event["type"] == "executing":
                                    stage = "sampling" if node == "10" else "decode" if node in ("11", "12", "13", "14") else "preparation"
                    except asyncio.TimeoutError:
                        pass
                    history = await get("/history/" + prompt_id)
                    if prompt_id not in history:
                        continue
                    job = history[prompt_id]
                    result["history"] = job
                    if job.get("status", {}).get("status_str") != "success":
                        raise RuntimeError("ComfyUI execution failed; see recorded history")
                    outputs = job["outputs"]
                    video = (outputs["14"].get("images") or outputs["14"].get("videos"))[0]
                    result["video"] = "outputs/" + "/".join(filter(None, (video.get("subfolder"), video["filename"])))
                    for node, label in (("21", "videoLatent"), ("22", "audioLatent")):
                        if node in outputs:
                            latent = outputs[node]["latents"][0]
                            result[label] = "/".join(filter(None, (latent.get("subfolder"), latent["filename"]))) + " [output]"
                    result["executionReceipt"] = outputs.get("h3-execution-receipt", {}).get("lumibelle_h3_receipt", [None])[0]
                    result["status"] = "complete"
                    break
        except Exception as error:
            result["status"] = "failed"
            result["error"] = str(error)
            if prompt_id:
                # This is a dedicated experiment-only server. Never interrupt production.
                async with client.post("http://127.0.0.1:8188/interrupt", json={"prompt_id": prompt_id}) as response:
                    await response.read()
        finally:
            result["seconds"] = time.monotonic() - started
            result["completedUtc"] = datetime.now(timezone.utc).isoformat()
            result["vramPeakObserved"] = max((s["vramUsed"] for s in result["memorySamples"]), default=None)
            result["memoryPeaksByStage"] = {stage: dict(
                vramUsed=max(s["vramUsed"] for s in samples),
                systemRamUsed=max(s["ramTotal"] - s["ramFree"] for s in samples))
                for stage in ("preparation", "sampling", "decode")
                if (samples := [s for s in result["memorySamples"] if s["stage"] == stage])}
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_text(json.dumps(result, indent=2, allow_nan=False))
            # A user may authorize more time while a case is running. Preserve
            # that updated budget without changing the active case's watchdog.
            budget = json.loads(budget_file.read_text("utf-8-sig"))
            budget["consumedSeconds"] += result["seconds"]
            next(case for case in budget["runs"] if case["name"] == name).update(status=result["status"], seconds=result["seconds"])
            budget_file.write_text(json.dumps(budget, indent=2))
            live = root / "live"
            live.mkdir(exist_ok=True)
            (live / (name + ".json")).write_text(json.dumps({key: result.get(key) for key in ("status", "seconds", "error")}))
        print(json.dumps({k: result.get(k) for k in ("name", "status", "seconds", "video", "error", "vramPeakObserved")}), flush=True)
        if result["status"] != "complete":
            raise SystemExit(1)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("root", type=Path)
    parser.add_argument("name")
    parser.add_argument("--maximum-seconds", type=int, default=1200)
    args = parser.parse_args()
    asyncio.run(run(args.root, args.name, args.maximum_seconds))
