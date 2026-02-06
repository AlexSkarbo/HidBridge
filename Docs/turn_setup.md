# TURN Setup (coturn) for WebRTC Control

Some Chromium-based browsers (notably Microsoft Edge / Opera) may produce **0 ICE candidates** in hardened environments. In that case, a direct WebRTC connection cannot be established via STUN only, and you need a **TURN relay**.

This project supports **TURN REST (ephemeral credentials)** which works well with `coturn`.

## Quick Notes

- For LAN-only testing, you can run coturn on your local network (RPi/PC).
- For Internet access, TURN must be reachable from clients:
  - Typically requires a public IP + port forwarding (UDP/TCP).
  - Cloudflare Tunnel does **not** proxy arbitrary UDP/TCP TURN traffic by default (HTTP-only). For TURN behind Cloudflare, you generally need a product that supports raw TCP/UDP proxying.
- If you run coturn in Docker, remember TURN allocates **relay ports** (a UDP port range). You must publish that range and configure `min-port/max-port`.

## coturn (TURN REST) Example

1) Install `coturn` on Linux (RPi or server).

2) Start with a minimal config enabling TURN REST:

```
listening-port=3478
fingerprint
realm=hidbridge
use-auth-secret
static-auth-secret=REPLACE_WITH_STRONG_SECRET
no-cli
```

3) Ensure firewall allows inbound:

- UDP `3478`
- TCP `3478`

Optional (recommended for restrictive networks):

- TURN over TLS on `5349` (or `443`) via `turns:` URLs (requires certs).

## Docker (Windows) Quick Start

If you run TURN on Windows via Docker Desktop, you can start coturn like this (PowerShell):

Important:

- Do **not** use `$env:TURN_REALM` / `$env:TURN_SECRET` in the arguments *unless you set them in your PowerShell session*.
  - The `-e TURN_SECRET=...` environment variables are inside the container.
  - `$env:...` is expanded on the Windows host before Docker starts the container.
- With Docker Desktop, coturn will usually discover only the container bridge IP (e.g. `172.x.x.x`) as a relay address.
  - For TURN to work for LAN clients, you typically need `--external-ip=<LAN_IP>/<CONTAINER_IP>`.
  - The easiest way to make this stable is to run coturn on a dedicated Docker network with a fixed container IP.

```powershell
$TURN_SECRET = "REPLACE_WITH_STRONG_SECRET"
$TURN_LAN_IP = "192.168.0.141"
$TURN_CONTAINER_IP = "172.30.0.10"

docker network create --subnet 172.30.0.0/16 turnnet 2>$null

docker run -d --name coturn --restart unless-stopped `
  --network turnnet --ip $TURN_CONTAINER_IP `
  -p 3478:3478/udp -p 3478:3478/tcp `
  -p 49160-49200:49160-49200/udp `
  coturn/coturn:latest `
  -n --log-file=stdout `
  --realm="hidbridge" `
  --use-auth-secret --static-auth-secret="$TURN_SECRET" `
  --listening-port=3478 --external-ip="$TURN_LAN_IP/$TURN_CONTAINER_IP" `
  --min-port=49160 --max-port=49200 `
  --no-tls --no-dtls `
  --fingerprint --no-cli
```

Notes:

- The `49160-49200/udp` range is for TURN relay ports (Docker must publish them).
- Set `$TURN_LAN_IP` to the LAN IP of the Windows host.
- If you need Internet access, you must also port-forward `3478` (UDP/TCP) and the relay UDP port range on your router/firewall.

Check logs:

```powershell
docker logs -f coturn
```

## HidControlServer Configuration

In your server config (example keys; values depend on your environment):

```json
{
  "webRtcTurnUrls": [
    "turn:192.168.0.10:3478?transport=udp",
    "turn:192.168.0.10:3478?transport=tcp"
  ],
  "webRtcTurnSharedSecret": "REPLACE_WITH_STRONG_SECRET",
  "webRtcTurnTtlSeconds": 3600,
  "webRtcTurnUsername": "hidbridge"
}
```

The endpoint `GET /status/webrtc/ice` will then return:

- STUN (if configured)
- TURN entry with an **ephemeral** `username` and `credential`

## Web Client

`HidControl.Web` calls `GET /api/webrtc/ice` (proxy to `HidControlServer`) and auto-fills the ICE JSON input when TURN is configured.
