# EXP-022 System Conclusions (AV + HID, Ultra-Low-Latency Goal)

## Objective

The real objective of EXP-022 is not only HID injection.  
It is a complete remote-control pipeline with:

- **Maximum practical video quality**
- **Stable audio**
- **Mouse/keyboard control**
- **Minimum end-to-end latency**

Target topology:

`Browser client <-> Server <-> Capture card (video/audio) + RP2040 HID bridge <-> Target PC`

## What Was Proven

1. **WebRTC playback path works** (WHEP/WHIP through SRS).
2. **Audio can be delivered** when Opus profile is used correctly.
3. **Clock-sync method works** and gives measurable visual latency checks.
4. **HID control path works at transport level**:
   - Browser -> WS control -> UART -> RP2040
   - ACK flow is stable.
5. **HID semantic layer is partially working**:
   - Some mouse/keyboard actions work.
   - Some reports/layout variants still need normalization.

## Key Findings (Root Causes)

1. **Latency is multi-factor**, not only bitrate:
   - Capture device buffering
   - FFmpeg encoder settings (GOP, lookahead, B-frames, profile)
   - Browser jitter buffer behavior
   - SRS queueing/retransmit behavior
   - Local machine CPU/GPU scheduling and thermal spikes

2. **Freeze/stutter events** were often related to:
   - Aggressive ultra-low-latency profile under unstable source cadence
   - High packet loss/retransmit bursts (NACK storms)
   - Over-constrained encoder settings for the host load

3. **Quality vs latency tradeoff is real**:
   - Extremely low latency profiles can degrade quality and increase instability.
   - A balanced low-latency profile can produce better real usability than “minimum possible” latency.

4. **HID ACK != semantic success**:
   - UART ACK confirms frame acceptance, not necessarily correct target HID behavior.
   - Correct report formatting (layout/descriptor/report-id) is mandatory.

## Updated Program Goal (Production-Oriented)

Build a stable remote-control system that prioritizes:

1. **Control responsiveness first** (predictable latency, no input drops)
2. **AV continuity second** (no long freezes, audio continuity)
3. **Visual quality third** (maximize quality inside latency/stability budget)

## Recommended System Split

1. **AV Service**
   - Capture + encode + publish profiles
   - Runtime adaptive profile switching (quality/latency/stability modes)
   - AV telemetry (FPS, keyframe interval, packet loss, RTT, NACK/PLI)

2. **HID Service**
   - WS/DataChannel control ingress
   - UART protocol adapter
   - Report layout + descriptor fallback logic
   - HID telemetry and semantic validation

3. **Experiment/Operator UI**
   - Stream monitor + clock overlay
   - Control arming/pointer lock
   - Live diagnostics for AV and HID

## Success Criteria (System-Level)

1. **Latency**
   - Define target envelope (example: visual end-to-end median <= 200 ms on local LAN).

2. **Stability**
   - No hard freeze > 1 sec in a 30-minute run.
   - Audio continuity without repeated dropouts.

3. **Control**
   - 99%+ successful semantic HID actions in scripted scenarios.

4. **Quality**
   - 1080p baseline with acceptable artifact level under target latency envelope.

## Next Execution Plan

1. Freeze current experimental settings and record baseline profile.
2. Add unified telemetry log for AV + HID in one timeline.
3. Tune in this order:
   - stability -> latency -> quality
4. Finalize HID descriptor/layout compatibility layer.
5. Promote to two-service architecture (AV + HID), keep EXP-022 as integration harness.
