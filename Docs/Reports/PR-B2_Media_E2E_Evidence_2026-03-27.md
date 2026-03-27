# PR-B.2 Media E2E Evidence (2026-03-27)

## Scope

Цей документ фіксує evidence для PR-B.2 (`real UI playback` + media gate), щоб закрити `done`-критерії в одному місці.

## Evidence Inputs

- RuntimeCtl gate logs:
  - `Platform/.logs/ci-local/20260327-000214`
  - `Platform/.logs/full/20260327-000311`
- Acceptance smoke summaries:
  - `Platform/.logs/webrtc-edge-agent-acceptance/20260327-000256/webrtc-edge-agent-smoke.result.json`
  - `Platform/.logs/webrtc-edge-agent-acceptance/20260327-000355/webrtc-edge-agent-smoke.result.json`
- Integration coverage:
  - `Platform/Tests/HidBridge.Platform.Tests/WebRtcEdgeAgentIntegrationTests.cs`
  - `dotnet test Platform/Tests/HidBridge.Platform.Tests/HidBridge.Platform.Tests.csproj -c Debug --filter FullyQualifiedName~WebRtcEdgeAgentIntegrationTests`

## Results

| Check | Run #1 (000256) | Run #2 (000355) | Status |
|---|---:|---:|---|
| `MediaGateRequired` | `true` | `true` | PASS |
| `MediaGatePass` | `true` | `true` | PASS |
| `mediaReady` | `true` | `true` | PASS |
| `mediaPlaybackUrl` | `http://127.0.0.1:18110/media/edge-main` | `http://127.0.0.1:18110/media/edge-main` | PASS |
| `mediaStreamId` | `edge-main` | `edge-main` | PASS |
| `mediaSource` | `edge-capture` | `edge-capture` | PASS |
| media-ready latency (`firstPeerSeenAtUtc -> mediaReportedAtUtc`) | `55 ms` | `3141 ms` | PASS (`~3.14 s` local bound) |
| command dispatch status | `Applied` | `Applied` | PASS |

## Reconnect Evidence

- Runtime evidence (these two runs): `reconnectTransitionCount = 0` and `connectedTransitionCount = 1` (stable no-reconnect path).
- Reconnect behavior validated by integration tests in:
  - `Platform/Tests/HidBridge.Platform.Tests/WebRtcEdgeAgentIntegrationTests.cs` (reconnect scenario, worker returns online and continues command processing).
- Filtered run result: `Passed: 3, Failed: 0` for `WebRtcEdgeAgentIntegrationTests` on `2026-03-27`.

## Gate Outcome

- `ci-local -StopOnFailure`: PASS (`Platform/.logs/ci-local/20260327-000214`)
- `full -StopOnFailure`: PASS (`Platform/.logs/full/20260327-000311`)

## Conclusion

PR-B.2 acceptance evidence is complete for:
- playback URL publication and media-ready gating,
- command path viability with media gate enabled,
- reconnect safety covered by integration lane.
