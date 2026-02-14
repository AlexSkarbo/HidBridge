#!/usr/bin/env bash
set -euo pipefail

BASE_URL="${BASE_URL:-http://127.0.0.1:8080}"
TOKEN="${TOKEN:-}"
MOUSE_ITF="${MOUSE_ITF:-255}"
KEYBOARD_ITF="${KEYBOARD_ITF:-254}"
USE_BOOTSTRAP="${USE_BOOTSTRAP:-0}"
ALLOW_STALE="${ALLOW_STALE:-0}"
SKIP_INJECT="${SKIP_INJECT:-0}"

headers=()
if [[ -n "$TOKEN" ]]; then
  headers=(-H "X-HID-Token: $TOKEN")
fi

pass=0
fail=0

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

echo "=== HidControlServer HID full test ==="
echo "BaseUrl: $BASE_URL"

if curl -s "${headers[@]}" "$BASE_URL/health" | grep -q '"ok":true'; then
  check_ok "health" 1
else
  check_ok "health" 0
fi

cfg="$(curl -s "${headers[@]}" "$BASE_URL/config" || true)"
echo "$cfg" | grep -q '"ok":true' && check_ok "config" 1 || check_ok "config" 0

qs=()
[[ "$USE_BOOTSTRAP" == "1" ]] && qs+=("useBootstrapKey=true")
[[ "$ALLOW_STALE" == "1" ]] && qs+=("allowStale=true")
qs+=("includeReportDesc=true")
query=""
if [[ "${#qs[@]}" -gt 0 ]]; then
  query="?"$(IFS="&"; echo "${qs[*]}")
fi

devices="$(curl -s "${headers[@]}" "$BASE_URL/devices$query" || true)"
if echo "$devices" | grep -q '"ok":true'; then
  check_ok "devices" 1
else
  check_ok "devices" 0 "$devices"
  devices="$(curl -s "${headers[@]}" "$BASE_URL/devices/last?includeReportDesc=true" || true)"
  echo "$devices" | grep -q '"ok":true' && check_ok "devices/last" 1 || check_ok "devices/last" 0
fi

if command -v python3 >/dev/null 2>&1; then
  itf_mouse="$(python3 - <<'PY'
import json,sys
data=json.loads(sys.stdin.read() or "{}")
lst=data.get("list",{})
interfaces=lst.get("interfaces") or []
for itf in interfaces:
    if itf.get("typeName")=="mouse":
        print(itf.get("itf", ""))
        break
PY
<<<"$devices")"
  itf_kb="$(python3 - <<'PY'
import json,sys
data=json.loads(sys.stdin.read() or "{}")
lst=data.get("list",{})
interfaces=lst.get("interfaces") or []
for itf in interfaces:
    if itf.get("typeName")=="keyboard":
        print(itf.get("itf", ""))
        break
PY
<<<"$devices")"
  [[ -n "$itf_mouse" ]] && MOUSE_ITF="$itf_mouse"
  [[ -n "$itf_kb" ]] && KEYBOARD_ITF="$itf_kb"
fi

echo "mouse itf: $MOUSE_ITF"
echo "keyboard itf: $KEYBOARD_ITF"

layout="$(curl -s "${headers[@]}" "$BASE_URL/keyboard/layout?itf=$KEYBOARD_ITF" || true)"
echo "$layout" | grep -q '"ok":true' && check_ok "keyboard/layout" 1 || check_ok "keyboard/layout" 0

if [[ "$SKIP_INJECT" == "1" ]]; then
  echo "[SKIP] inject tests"
else
  move="$(curl -s "${headers[@]}" -X POST -H "Content-Type: application/json" \
    -d "{\"dx\":1,\"dy\":0,\"itfSel\":$MOUSE_ITF}" \
    "$BASE_URL/mouse/move" || true)"
  echo "$move" | grep -q '"ok":true' && check_ok "mouse/move" 1 || check_ok "mouse/move" 0 "$move"

  wheel="$(curl -s "${headers[@]}" -X POST -H "Content-Type: application/json" \
    -d "{\"delta\":1,\"itfSel\":$MOUSE_ITF}" \
    "$BASE_URL/mouse/wheel" || true)"
  echo "$wheel" | grep -q '"ok":true' && check_ok "mouse/wheel" 1 || check_ok "mouse/wheel" 0 "$wheel"

  buttons1="$(curl -s "${headers[@]}" -X POST -H "Content-Type: application/json" \
    -d "{\"buttonsMask\":1,\"itfSel\":$MOUSE_ITF}" \
    "$BASE_URL/mouse/buttons" || true)"
  buttons2="$(curl -s "${headers[@]}" -X POST -H "Content-Type: application/json" \
    -d "{\"buttonsMask\":0,\"itfSel\":$MOUSE_ITF}" \
    "$BASE_URL/mouse/buttons" || true)"
  if echo "$buttons1" | grep -q '"ok":true' && echo "$buttons2" | grep -q '"ok":true'; then
    check_ok "mouse/buttons" 1
  else
    check_ok "mouse/buttons" 0
  fi

  kreport="$(curl -s "${headers[@]}" -X POST -H "Content-Type: application/json" \
    -d "{\"modifiers\":0,\"keys\":[4],\"itfSel\":$KEYBOARD_ITF,\"applyMapping\":false}" \
    "$BASE_URL/keyboard/report" || true)"
  echo "$kreport" | grep -q '"ok":true' && check_ok "keyboard/report" 1 || check_ok "keyboard/report" 0 "$kreport"

  kreset="$(curl -s "${headers[@]}" -X POST -H "Content-Type: application/json" \
    -d "{\"itfSel\":$KEYBOARD_ITF}" \
    "$BASE_URL/keyboard/reset" || true)"
  echo "$kreset" | grep -q '"ok":true' && check_ok "keyboard/reset" 1 || check_ok "keyboard/reset" 0 "$kreset"
fi

state="$(curl -s "${headers[@]}" "$BASE_URL/keyboard/state" || true)"
echo "$state" | grep -q '"ok":true' && check_ok "keyboard/state" 1 || check_ok "keyboard/state" 0

echo ""
echo "passed: $pass"
echo "failed: $fail"

if [[ "$fail" -gt 0 ]]; then
  exit 1
fi
