"""Explicitly prepare one requested retry; never submits or retries automatically."""
import argparse
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import uuid

parser = argparse.ArgumentParser()
parser.add_argument("root", type=Path)
parser.add_argument("source")
parser.add_argument("destination")
parser.add_argument("--reason", required=True)
args = parser.parse_args()
source = args.root / "requests" / (args.source + ".json")
destination = args.root / "requests" / (args.destination + ".json")
if destination.exists():
    raise RuntimeError("The retry is already captured")
previous = json.loads((args.root / "results" / (args.source + ".json")).read_text("utf-8-sig"))
if previous["status"] != "failed":
    raise RuntimeError("Use a new reviewed capture for a completed case")
capture = json.loads(source.read_text("utf-8-sig"))
frozen_request = json.dumps(capture["request"], sort_keys=True)
frozen_prompt = json.dumps(capture["workflow"]["prompt"], sort_keys=True)
capture["name"] = args.destination
capture["candidateId"] = capture["workflow"]["client_id"] = str(uuid.uuid4())
capture["retryOf"] = dict(case=args.source, sourceCaptureSha256=hashlib.sha256(source.read_bytes()).hexdigest(),
                          reason=args.reason, preparedUtc=datetime.now(timezone.utc).isoformat(),
                          submittedPromptSha256=hashlib.sha256(frozen_prompt.encode()).hexdigest())
assert json.dumps(capture["request"], sort_keys=True) == frozen_request
assert json.dumps(capture["workflow"]["prompt"], sort_keys=True) == frozen_prompt
destination.write_text(json.dumps(capture, indent=2))
print(destination)
