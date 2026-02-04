# WebRTC Signaling (Skeleton)

HidBridge includes a **minimal WebRTC signaling relay** to bootstrap WebRTC sessions between two browser clients.

This is intentionally a skeleton:
- In-memory rooms (no persistence)
- No auth beyond existing server token middleware
- No TURN/STUN configuration on the server (client controls ICE servers)
- Meant to validate that SDP/ICE exchange works before integrating real media pipelines

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
{ "ok": true, "type": "webrtc.joined", "room": "demo", "clientId": "...", "peers": 2 }
```

Other peers in the room receive:

```json
{ "ok": true, "type": "webrtc.peer_joined", "room": "demo", "peerId": "...", "peers": 2 }
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

