#!/usr/bin/env bash
set -euo pipefail

# QuotaTray Antigravity status-line bridge.
# Reads the official agy statusLine JSON payload from stdin and writes only
# quota-related, non-conversation metadata to an atomic local snapshot.

CACHE_ROOT="${XDG_CACHE_HOME:-$HOME/.cache}/quotatray"
SNAPSHOT_PATH="${QUOTATRAY_ANTIGRAVITY_STATUS_PATH:-$CACHE_ROOT/antigravity-status.json}"
mkdir -p "$(dirname "$SNAPSHOT_PATH")"

payload="$(cat)"
QUOTATRAY_STATUS_PAYLOAD="$payload" python3 - "$SNAPSHOT_PATH" <<'PY'
import json
import os
import sys
import tempfile
from datetime import datetime, timezone

path = sys.argv[1]
raw = os.environ.get("QUOTATRAY_STATUS_PAYLOAD", "")
try:
    src = json.loads(raw)
except Exception:
    print("QT bridge: invalid status payload")
    raise SystemExit(0)

quota = src.get("quota")
if not isinstance(quota, dict):
    quota = {}

snapshot = {
    "schema_version": 1,
    "updated_at": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
    "source": "agy-statusline",
    "product": src.get("product"),
    "version": src.get("version"),
    "plan_tier": src.get("plan_tier"),
    "quota": quota,
}

parent = os.path.dirname(path) or "."
os.makedirs(parent, exist_ok=True)
fd, tmp = tempfile.mkstemp(prefix=".antigravity-status-", suffix=".json", dir=parent)
try:
    with os.fdopen(fd, "w", encoding="utf-8") as f:
        json.dump(snapshot, f, ensure_ascii=False, indent=2, sort_keys=True)
        f.write("\n")
    os.replace(tmp, path)
except Exception:
    try:
        os.unlink(tmp)
    except OSError:
        pass
    raise

# Keep the custom status line intentionally tiny. QuotaTray only needs the file.
values = []
for bucket in quota.values():
    if not isinstance(bucket, dict):
        continue
    remaining = bucket.get("remaining_fraction")
    if isinstance(remaining, (int, float)):
        values.append(float(remaining))
if values:
    print(f"QT {min(values) * 100:.0f}%")
else:
    print("QT ready")
PY
