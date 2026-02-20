# Media Architecture Decision

## Decision
Current in-process WebRTC media path is **frozen** and moved to legacy status.
No further tuning/optimization work is allowed on the legacy path except critical break/fix.

## Reason
Observed behavior under real workload:
- periodic video stalls/freezes,
- high end-to-end lag (up to ~2s),
- unstable quality in long sessions,
- non-deterministic behavior across source profiles.

This does not meet product quality requirements for 1080p interactive use.

## Scope Frozen
- `Tools/WebRtcVideoPeer` legacy media pipeline behavior.
- parameter-level tuning loops for ffmpeg/pion queue behavior.

## What Continues
- control plane: mouse/keyboard/datachannel handling,
- telemetry/eventing infrastructure,
- room lifecycle APIs.

## Replacement Strategy
Adopt a new media stack only via strict acceptance gate.

## Platform Requirement
Cross-platform is mandatory.
- Host side: Windows, Linux, macOS.
- Client side: Web desktop + Android (required), iOS (target).

## Acceptance Gate (hard)
A candidate media stack is accepted only if all items pass:
1. 1920x1080 stable session for 30 minutes.
2. No freeze > 2 seconds.
3. End-to-end interactive lag within target (product-defined, measured p95).
4. Stable A/V sync (no drift growth during session).
5. Mouse and keyboard control remain responsive and correct under load.
6. Client compatibility:
- Android client works in target scenario.
- iOS client support is validated or has a documented compliant fallback path.

## Go/No-Go Rule
- If candidate fails any hard gate item: **No-Go** (do not integrate).
- If candidate passes all hard gate items: **Go** to integration planning.

## Immediate Next Step
Prepare shortlist of replacement media stacks and run acceptance gate evaluation on host hardware.
