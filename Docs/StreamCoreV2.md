# Stream Core v2 Plan

## Goal
Build a new low-latency, stable media path for USB capture without patching legacy runtime behavior.

## Non-Goals
- No incremental tuning of current mixed pipeline.
- No production switch until acceptance criteria pass.

## Legacy Baseline
- Keep current production path unchanged.
- New implementation runs behind `stream_core_v2` feature flag.
- Rollback path: disable flag, immediate return to legacy.

## Architecture (v2)
1. Single media graph per room.
2. Explicit clocking model:
- capture clock
- encoder output timestamps
- WebRTC sender pacing
3. Frame-oriented backpressure:
- drop whole frames only
- never drop random RTP packets inside a frame
4. Deterministic restart policy:
- bounded retries
- no hidden queue growth.

## Delivery Stages
1. Stage A: Skeleton
- Add feature flag plumbing and isolated `StreamCoreV2` entrypoint.
- Keep full compatibility with existing signaling/control.

2. Stage B: Video Path
- Implement video ingest/encode/send pipeline in `StreamCoreV2`.
- Add frame queue instrumentation (size, drops, latency).

3. Stage C: Audio Path + Sync
- Implement audio path with stable timestamps.
- Add A/V drift metrics and correction policy.

4. Stage D: Lifecycle + Recovery
- Start/restart/stop semantics.
- Cleanup guarantees for rooms/helpers/processes.

5. Stage E: Acceptance + Rollout
- Run full matrix.
- Enable flag for controlled validation.

## Acceptance Criteria (hard gate)
1. 30 minutes continuous run:
- no freeze > 2s.
2. Latency:
- p95 < 600ms.
3. Visual stability:
- no persistent corruption/artifact loops.
4. Audio sync:
- no growing A/V drift over session.
5. Recovery:
- reconnect/restart works without stale buffers.

## Test Matrix
1. Create room -> connect -> 30 min stream.
2. Restart room during active playback.
3. Delete room and recreate quickly.
4. Audio on/off toggles.
5. Profile switch under load.

## Observability
- Structured counters/logs per room:
- input fps, output fps
- queue depth
- frame drops
- A/V drift ms
- restart reason
- first-frame time

## Risks
1. Capture device driver instability under sustained load.
2. Encoder behavior differences by hardware.
3. Browser jitter-buffer variance.

## Mitigations
1. Keep legacy path as immediate fallback.
2. Feature-flag rollout.
3. Per-host profile overrides if required.
