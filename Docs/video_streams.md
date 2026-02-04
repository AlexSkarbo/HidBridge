# Video streams mapping (draft)

This document explains how `video/sources` map to stream endpoints.

## Sources

`GET /video/sources` returns a list of configured sources:

- `id`: logical stream id
- `kind`: `uvc` | `rtsp` | `http` | `hls`
- `url`: device path or network url
- `name`: optional friendly name
- `enabled`: if false, not used

## Streams

`GET /video/streams` returns stream endpoints:

- `rtspUrl`: `rtsp://HOST:8554/{id}` (FFmpeg publishes RTSP directly)
- `srtUrl`: `srt://HOST:9000` (per-source port, see `srtPort`)
- `srtPort`: numeric port for this source
- `rtmpUrl`: `rtmp://HOST:1935/live/{id}` (FFmpeg publishes RTMP directly)
- `hlsUrl`: `http://HOST:8080/video/hls/{id}/index.m3u8`
- `mjpegUrl`: `http://HOST:8080/video/mjpeg/{id}`
- `snapshotUrl`: `http://HOST:8080/video/snapshot/{id}`
- `ffmpegInput`: suggested input string

These are direct endpoints served by the server and FFmpeg (no external media server).

## Profiles

`GET /video/profiles` returns the active profile and args used by ffmpeg launcher.

## Example ffmpeg (Linux UVC, RTSP listener)

```bash
ffmpeg -f v4l2 -i /dev/video0 \
  -c:v libx264 -preset ultrafast -tune zerolatency \
  -f rtsp -rtsp_flags listen -rtsp_transport tcp rtsp://0.0.0.0:8554/cap-1
```

## Example ffmpeg (Windows UVC, RTSP listener)

```bash
ffmpeg -f dshow -i video="USB3.0 Video" \
  -c:v libx264 -preset ultrafast -tune zerolatency \
  -f rtsp -rtsp_flags listen -rtsp_transport tcp rtsp://0.0.0.0:8554/cap-1
```
