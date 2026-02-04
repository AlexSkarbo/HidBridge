#!/usr/bin/env bash
set -euo pipefail

BASE_URL="${BASE_URL:-http://127.0.0.1:8080}"
TOKEN="${TOKEN:-}"
SOURCE_ID="${SOURCE_ID:-}"
TIMEOUT_SEC="${TIMEOUT_SEC:-6}"
SCAN_APPLY="${SCAN_APPLY:-0}"
START_FFMPEG="${START_FFMPEG:-0}"

headers=()
if [[ -n "$TOKEN" ]]; then
  headers=(-H "X-HID-Token: $TOKEN")
fi

pass=0
fail=0

section() {
  echo ""
  echo "=== $1 ==="
}

check_ok() {
  local name="$1"
  local ok="$2"
  local details="${3:-}"
  if [[ "$ok" == "1" ]]; then
    echo "[PASS] $name"
    pass=$((pass+1))
  else
    if [[ -n "$details" ]]; then
      echo "[FAIL] $name :: $details"
    else
      echo "[FAIL] $name"
    fi
    fail=$((fail+1))
  fi
}

echo "=== HidControlServer video full test ==="
echo "BaseUrl: $BASE_URL"

if curl -s "${headers[@]}" "$BASE_URL/health" | grep -q '"ok":true'; then
  check_ok "health" 1
else
  check_ok "health" 0
fi

if [[ "$SCAN_APPLY" == "1" ]]; then
  section "scan sources (apply)"
  if curl -s "${headers[@]}" -X POST "$BASE_URL/video/sources/scan?apply=true" | grep -q '"ok":true'; then
    check_ok "scan/apply" 1
  else
    check_ok "scan/apply" 0
  fi
fi

section "sources"
sources_json="$(curl -s "${headers[@]}" "$BASE_URL/video/sources")" || true
echo "$sources_json" | grep -q '"ok":true' && check_ok "sources/list" 1 || check_ok "sources/list" 0

section "streams"
streams_json="$(curl -s "${headers[@]}" "$BASE_URL/video/streams")" || true
echo "$streams_json" | grep -q '"ok":true' && check_ok "streams/list" 1 || check_ok "streams/list" 0

section "profiles"
profiles_json="$(curl -s "${headers[@]}" "$BASE_URL/video/profiles")" || true
echo "$profiles_json" | grep -q '"ok":true' && check_ok "profiles/list" 1 || check_ok "profiles/list" 0

if [[ "$START_FFMPEG" == "1" ]]; then
  section "ffmpeg start"
  ff="$(curl -s "${headers[@]}" -X POST -H "Content-Type: application/json" -d '{}' "$BASE_URL/video/ffmpeg/start")" || true
  echo "$ff" | grep -q '"ok":true' && check_ok "ffmpeg/start" 1 || check_ok "ffmpeg/start" 0
fi

if command -v python3 >/dev/null 2>&1; then
  target_id="$SOURCE_ID"
  if [[ -z "$target_id" ]]; then
    target_id="$(python3 - <<'PY'
import json,sys
data=json.loads(sys.stdin.read() or "{}")
sources=data.get("sources") or []
for s in sources:
    if s.get("enabled"):
        print(s.get("id",""))
        break
PY
<<<"$sources_json")"
  fi
else
  target_id="$SOURCE_ID"
fi

if [[ -z "$target_id" ]]; then
  check_ok "select source" 0 "no enabled sources"
else
  echo "using source: $target_id"

  section "test-capture"
  tc="$(curl -s "${headers[@]}" -X POST "$BASE_URL/video/test-capture?id=$target_id&timeoutSec=$TIMEOUT_SEC")" || true
  echo "$tc" | grep -q '"ok":true' && check_ok "test-capture" 1 || check_ok "test-capture" 0 "$tc"

  section "snapshot"
  snap_file="$(mktemp)"
  if curl -s "${headers[@]}" --max-time "$TIMEOUT_SEC" "$BASE_URL/video/snapshot/$target_id" -o "$snap_file"; then
    if command -v xxd >/dev/null 2>&1; then
      sig="$(xxd -p -l 2 "$snap_file")"
      [[ "$sig" == "ffd8" ]] && check_ok "snapshot" 1 || check_ok "snapshot" 0 "sig=$sig"
    else
      size="$(wc -c <"$snap_file")"
      [[ "$size" -gt 0 ]] && check_ok "snapshot" 1 || check_ok "snapshot" 0
    fi
  else
    check_ok "snapshot" 0
  fi
  rm -f "$snap_file"

  section "mjpeg"
  mjpeg_file="$(mktemp)"
  if curl -s "${headers[@]}" --max-time "$TIMEOUT_SEC" "$BASE_URL/video/mjpeg/$target_id?fps=5" -o "$mjpeg_file"; then
    if grep -q -- "--frame" "$mjpeg_file"; then
      check_ok "mjpeg" 1
    else
      check_ok "mjpeg" 0
    fi
  else
    check_ok "mjpeg" 0
  fi
  rm -f "$mjpeg_file"

  section "hls"
  hls_file="$(mktemp)"
  if curl -s "${headers[@]}" --max-time "$TIMEOUT_SEC" "$BASE_URL/video/hls/$target_id/index.m3u8" -o "$hls_file"; then
    if grep -q "#EXTM3U" "$hls_file"; then
      check_ok "hls" 1
    else
      check_ok "hls" 0
    fi
  else
    check_ok "hls" 0
  fi
  rm -f "$hls_file"
fi

echo ""
echo "passed: $pass"
echo "failed: $fail"

if [[ "$fail" -gt 0 ]]; then
  exit 1
fi
