# WebRTC Signaling Paths: Control vs Video

This document describes how HidBridge currently routes WebRTC signaling for two room families.

## Room Kinds

- `control` kind:
  - examples: `control`, `hb-<device>-<suffix>`, `hb-unknown-<suffix>`
- `video` kind:
  - examples: `video`, `video-*`, `hb-v-<device>-<suffix>`

Room kind is inferred by room id naming convention in signaling service.

## Signaling Transport

- WebSocket endpoint is shared:
  - `GET /ws/webrtc`
- Message types are shared:
  - client: `join`, `signal`, `leave`
  - server: `webrtc.hello`, `webrtc.joined`, `webrtc.peer_joined`, `webrtc.peer_left`, `webrtc.signal`, `webrtc.error`
- Server includes `kind` in room lifecycle events:
  - `webrtc.joined`, `webrtc.peer_joined`, `webrtc.peer_left`

## Room Lifecycle APIs

- Control rooms:
  - `GET /status/webrtc/rooms`
  - `POST /status/webrtc/rooms`
  - `DELETE /status/webrtc/rooms/{room}`
- Video rooms:
  - `GET /status/webrtc/video/rooms`
  - `POST /status/webrtc/video/rooms`
  - `DELETE /status/webrtc/video/rooms/{room}`

In `HidControl.Web` these are proxied as:

- `/api/webrtc/rooms*` for control
- `/api/webrtc/video/rooms*` for video

The UI now selects API prefix by room id, so `video` and `hb-v-*` rooms use the video endpoints.

## Current Limits (MVP)

- Per-room peer limit in signaling: `2` (controller + helper).
- Shared `/ws/webrtc` relay for both kinds.
- DataChannel control path is production-tested first; media/video path is still skeleton-level.
