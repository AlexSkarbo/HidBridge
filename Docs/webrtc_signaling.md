# WebRTC Signaling (Skeleton)

HidBridge includes a **minimal WebRTC signaling relay** to bootstrap WebRTC sessions between two browser clients.

This is intentionally a skeleton:
- Room registry can be persisted to disk (optional)
- No auth beyond existing server token middleware
- No TURN/STUN configuration on the server (client controls ICE servers)
- Meant to validate that SDP/ICE exchange works before integrating real media pipelines

See also:
- `Docs/webrtc_signaling_paths.md` for control/video room path split and endpoint mapping.

## Server Endpoint

- WebSocket: `GET /ws/webrtc`

The server assigns a `clientId` and replies:

```json
{ "ok": true, "type": "webrtc.hello", "clientId": "..." }
```

## Client Messages

### Join a Room

```json
{ "type": "join", "room": "demo" }
```

Server replies:

```json
{ "ok": true, "type": "webrtc.joined", "room": "demo", "kind": "control", "clientId": "...", "peers": 2 }
```

Other peers in the room receive:

```json
{ "ok": true, "type": "webrtc.peer_joined", "room": "demo", "kind": "control", "peerId": "...", "peers": 2 }
```

### Relay Signaling Data

```json
{
  "type": "signal",
  "room": "demo",
  "data": { "kind": "offer", "sdp": { "type": "offer", "sdp": "..." } }
}
```

The server broadcasts to other peers:

```json
{
  "ok": true,
  "type": "webrtc.signal",
  "room": "demo",
  "from": "senderClientId",
  "data": { "kind": "offer", "sdp": { "type": "offer", "sdp": "..." } }
}
```

Use the same format for:
- `kind: "answer"` with `sdp`
- `kind: "candidate"` with `candidate` (the `RTCIceCandidate` object)

### Leave

```json
{ "type": "leave" }
```

## Web Client

`Tools/Clients/Web/HidControl.Web` includes a demo UI:
- open the page in **two tabs**
- use the same room id
- click **Call** in one tab to establish a **DataChannel**

## Optional Room Persistence

To keep generated rooms across server restarts, enable room registry persistence in server config:

```json
{
  "webRtcRoomsPersistEnabled": true,
  "webRtcRoomsStorePath": "webrtc_rooms.json",
  "webRtcRoomsPersistTtlSeconds": 86400
}
```

Behavior:
- persisted rooms are restored on first room-list/connect flow
- helper autostart is retried for persisted rooms
- stale persisted rooms are removed when no peers/helpers are present and TTL expires
