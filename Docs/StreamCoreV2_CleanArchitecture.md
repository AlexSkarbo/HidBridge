# Stream Core v2 - Clean Architecture

## Goal
Create a cross-platform media subsystem that is replaceable, testable, and stable under interactive 1080p load.

## Architectural Principles
1. Dependency Rule:
- Inner layers must not depend on outer layers.
2. Framework Isolation:
- WebRTC/capture/encoder specifics belong only to Infrastructure.
3. Replaceability:
- Swap media backend without changing Domain/Application logic.
4. Deterministic Operations:
- Explicit queues, clocks, and backpressure policies.

## Layers

### 1) Domain
Responsibilities:
- Session state machine.
- Invariants: latency budget, drift budget, freeze thresholds.
- Domain events: `SessionStarted`, `FrameDropped`, `DriftExceeded`, `SessionRecovered`.

No dependencies on:
- ffmpeg/pion/obs/platform APIs.

### 2) Application
Responsibilities:
- Use-cases:
  - `StartStreamSession`
  - `StopStreamSession`
  - `RestartStreamSession`
  - `ApplyProfile`
  - `HandleControlInput`
  - `PublishRuntimeTelemetry`
- Orchestration of ports.

Depends on:
- Domain + Port interfaces only.

### 3) Ports (Interfaces)
Core contracts:
- `IMediaPipelinePort`
- `IControlChannelPort`
- `ITelemetryPort`
- `ISessionStorePort`
- `IClockPort`
- `IHealthProbePort`

All ports must be technology-agnostic.

### 4) Infrastructure
Responsibilities:
- Implement ports for concrete stacks (legacy/v2/candidate backends).
- Capture/encode/transmit adapter code.
- Process supervision, platform-specific IO.

Examples:
- `LegacyMediaPipelineAdapter`
- `V2MediaPipelineAdapter`
- `WebRtcControlChannelAdapter`
- `ServerTelemetryAdapter`

### 5) Composition Root
Responsibilities:
- DI wiring.
- Feature flag selection (`HIDBRIDGE_STREAM_CORE=legacy|v2`).
- Config loading and validation.

## Server and Clients / Сервер і клієнти

### Server Side (authoritative control plane)
- `Tools/Server/HidControlServer`
  - API, room lifecycle, profile/runtime config, orchestration.
- `Tools/Infrastructure/HidControl.Infrastructure`
  - concrete adapters for session store, telemetry sinks, runtime supervision.
- `Tools/WebRtcVideoPeer`
  - runtime media host process (executes selected media adapter via ports).

### Client Side
- Web client: `Tools/Clients/Web/HidControl.Web`
  - signaling/control UI, telemetry visualization, profile selection.
- Desktop clients (required target):
  - Windows desktop client
  - Linux desktop client
  - macOS desktop client
  - all must support stream playback + mouse/keyboard control + telemetry view.
- Android client (required target)
  - WebRTC playback + control channel compatibility.
- iOS client (target)
  - WebRTC playback + control channel compatibility or documented fallback.

### Responsibility Split
- Server: source of truth for rooms/sessions/profiles and lifecycle decisions.
- Clients: consume stream, send input (mouse/keyboard), display telemetry.
- Media backend: interchangeable infrastructure component, hidden behind ports.

## Suggested Project/Folder Structure
- `Tools/Core/HidControl.Domain/StreamCore/*`
- `Tools/Core/HidControl.Application/StreamCore/*`
- `Tools/Core/HidControl.Application/Abstractions/StreamCore/*` (ports)
- `Tools/Infrastructure/HidControl.Infrastructure/StreamCore/*` (adapters)
- `Tools/WebRtcVideoPeer` can remain runtime host, but delegates to Application via ports.

## Minimal Port Contracts (conceptual)
1. `IMediaPipelinePort`
- `StartAsync(StreamSessionSpec, CancellationToken)`
- `StopAsync(SessionId, CancellationToken)`
- `GetStats(SessionId)`

2. `IControlChannelPort`
- `SendInput(SessionId, ControlMessage, CancellationToken)`
- `GetRtt(SessionId)`

3. `ITelemetryPort`
- `Publish(SessionId, StreamTelemetry)`

4. `ISessionStorePort`
- `Load(SessionId)`
- `Save(StreamSessionState)`

5. `IClockPort`
- `NowUtc` and monotonic tick helpers.

## Runtime Policies (v2)
1. Frame-level backpressure only.
2. No random packet drops within a frame.
3. Bounded queue depths with explicit watermarks.
4. Controlled restart policy (bounded retries + reason codes).
5. Telemetry must be non-blocking for media/control paths.

## Testing Strategy
1. Domain tests:
- state transitions, invariants, policy decisions.
2. Application tests:
- use-case orchestration with mocked ports.
3. Contract tests:
- each Infrastructure adapter must satisfy port contracts.
4. End-to-end acceptance:
- 1080p/30m, lag p95, no freeze >2s, A/V drift, input correctness.

## Migration Strategy
1. Keep legacy as default.
2. Introduce v2 behind feature flag.
3. Run side-by-side validation.
4. Switch default only after acceptance gate pass.
5. Keep one-release rollback window.

## Definition of Done for Stream Core v2
- Passes all hard gate criteria on supported host/client matrix.
- No direct infra dependencies from Domain/Application.
- Feature-flag rollback verified.
