# WebRTC Video Peer (Skeleton)

Goal: move towards real-time video via WebRTC. For now we only ship the **skeleton** required to manage video rooms and run a helper peer process.

## Components

- `HidControlServer`: signaling relay (`/ws/webrtc`) + room/ICE/config endpoints under `/status/*`
- `WebRtcVideoPeer` (helper): `Tools/WebRtcVideoPeer` (Go + Pion)
- `HidControl.Web`: demo UI (rooms browser + DataChannel tester)
- Signaling path split details: `Docs/webrtc_signaling_paths.md`

## Rooms

Video rooms are a separate namespace (recommended):

- Default video room: `video`
- Generated video rooms: `hb-v-<deviceIdShort>-<rand>`

Where:
- `<deviceIdShort>` is derived from `HidUartClient.GetDeviceIdHex()` (first 8 hex chars) and is currently tied to the UART-connected HID bridge MCU (typically `B_host`).
- If the device id is not known yet, the generator uses `unknown`.
- `<rand>` is a short random base36 suffix to avoid collisions.

## Endpoints

- List video rooms: `GET /status/webrtc/video/rooms`
- Create a video room (and start helper): `POST /status/webrtc/video/rooms` (optional body: `{ "room": "..." }`)
- Delete a video room helper: `DELETE /status/webrtc/video/rooms/{room}`
  - Note: deleting the default room `video` is blocked (`cannot_delete_video`).

## Helper Auto-Start

`HidControlServer` can auto-start the video helper on boot:

```json
{
  "webRtcVideoPeerAutoStart": true,
  "webRtcVideoPeerRoom": "video",
  "webRtcVideoPeerStun": "stun:stun.l.google.com:19302"
}
```

## Current Limitations

- The helper does **not** publish any media tracks yet.
- For now, it only accepts offers and opens a DataChannel (echo mode) to prove the path end-to-end.
