# Changelog

## 2026-03-26 (cli-first operational docs/ui defaults)

Summary:

- Switched operator-facing runbook guidance from `Platform/run.ps1` to direct `HidBridge.RuntimeCtl` usage for daily operations.
- Updated Ops Status UI operational playbook commands to CLI-first (`dotnet run --project ... HidBridge.RuntimeCtl`).
- Kept `run.ps1` explicitly documented as compatibility shim (not primary orchestration path).

Detailed notes:

- `Platform/README.md`
- `Platform/Clients/HidBridge.ControlPlane.Web/Components/Pages/OpsStatus.razor`

## 2026-03-26 (runtimectl native webrtc-stack + demo-flow, run.ps1 minimized)

Summary:

- Completed CLI-first migration for WebRTC orchestration:
  - `webrtc-stack` is now native in `HidBridge.RuntimeCtl`.
  - `demo-flow` is now native in `HidBridge.RuntimeCtl`.
- `run_webrtc_stack.ps1` and `run_demo_flow.ps1` are reduced to thin compatibility wrappers that only delegate to RuntimeCtl.
- `Platform/run.ps1` is reduced to a minimal compatibility shim with direct routing to native RuntimeCtl commands (`demo-flow`, `webrtc-stack`, plus previously migrated native commands).
- Legacy `controlws/exp-022` mode remains explicitly gated (`HIDBRIDGE_ENABLE_LEGACY_EXP022`) without reintroducing script-side policy logic.

Detailed notes:

- `Platform/Tools/HidBridge.RuntimeCtl/Program.cs`
- `Platform/Scripts/run_webrtc_stack.ps1`
- `Platform/Scripts/run_demo_flow.ps1`
- `Platform/run.ps1`

## 2026-03-25 (pr-b.2 regression tests: session media panel)

Summary:

- Added component-level regression coverage for session media playback panel runtime behavior.
- Added bUnit Web test dependency for Blazor component rendering in `HidBridge.Platform.Tests`.
- Locked key UI expectations to prevent future regressions in playback controls and runtime status rendering.

Detailed notes:

- `Platform/Tests/HidBridge.Platform.Tests/HidBridge.Platform.Tests.csproj` (`bunit.web`)
- `Platform/Tests/HidBridge.Platform.Tests/SessionMediaPanelTests.cs` (new)

## 2026-03-25 (pr-b.2 ui playback parity: live media runtime + reconnect states)

Summary:

- Completed Session Details media playback panel as a real runtime surface (not static diagnostics):
  - live connection/ICE state
  - track count
  - last playback event
  - reconnect attempt counter.
- Added explicit operator controls for media runtime handling:
  - `Reconnect now`
  - `Auto reconnect` toggle
  - existing `Start stream`/`Stop stream` kept.
- Added JS media runtime event bridge (`JS -> .NET`) so panel state updates from actual `video`/`RTCPeerConnection` lifecycle.
- Added bounded auto-reconnect loop for playback failures/stalls/disconnects with configurable delay/attempt guardrails.

Detailed notes:

- `Platform/Clients/HidBridge.ControlPlane.Web/Components/Pages/SessionMediaPanel.razor`
- `Platform/Clients/HidBridge.ControlPlane.Web/Components/Pages/SessionMediaPanel.razor.js`
- `Platform/Clients/HidBridge.ControlPlane.Web/Localization/OperatorText.cs`

## 2026-03-25 (pr-next promotion plan + media e2e gate)

Summary:

- Added explicit transport-engine promotion plan (`relay` -> `dcd`, `none` -> `ffmpeg-dcd`) with shadow/canary/default switch criteria and rollback rules.
- Extended `.NET` acceptance runner with media E2E gate options:
  - `--require-media-ready`
  - `--require-media-playback-url`
  - bounded retries via `--media-health-attempts` / `--media-health-delay-ms`.
- Acceptance summary now reports media-gate pass/fail and observed media diagnostics.
- Integrated media gate into CI orchestration (`ci-local`/`full`) as required-by-default lane with emergency skip flag.

Detailed notes:

- `Platform/Tools/HidBridge.Acceptance.Runner/Program.cs`
- `Platform/Scripts/run_webrtc_edge_agent_acceptance.ps1`
- `Platform/Scripts/run_ci_local.ps1`
- `Platform/Scripts/run_full.ps1`
- `Platform/run.ps1`
- `Platform/README.md`

## 2026-03-23 (pr-2 integration gate finalization: hosted full-stack lease coverage)

Summary:

- Added hosted full-stack integration coverage for lease orchestration + transport readiness projection:
  - happy path (`ensure lease` + peer online + media registry -> readiness `ready`)
  - deterministic lease conflict propagation (`E_CONTROL_LEASE_HELD_BY_OTHER`).
- Kept `ci-local`/`full` gating model unchanged (mandatory acceptance + ops verify) while extending default test suite depth through hosted scenarios.
- Updated platform runbook to explicitly mention hosted lease-orchestration coverage in the regular test lane.

Detailed notes:

- `Platform/Tests/HidBridge.Platform.Tests/HostedWebRtcFullStackIntegrationTests.cs` (new)
- `Platform/README.md`

## 2026-03-23 (pr-a live input batch dispatch parity)

Summary:

- Added server-side batch command dispatch contract and endpoint:
  - `POST /api/v1/sessions/{sessionId}/commands/batch`
  - aggregated response with per-command statuses and counters (`applied/rejected/timeout`).
- Added application use case for ordered batch dispatch (`DispatchCommandBatchUseCase`) and caller-context enrichment support for batch scope fields.
- Added Web client batch models + API client method and integrated live input pipeline in `SessionDetails`:
  - JS capture for `keydown/keyup/mousedown/mouseup/mousemove/wheel/blur/visibilitychange/contextmenu/pointerlockchange`
  - UI controls: `Live Control ON/OFF`, `Pointer Lock ON/OFF`, `Panic Reset`
- Added regression tests for batch route client wiring and batch use-case aggregation.

Detailed notes:

- `Platform/Shared/HidBridge.Contracts/Messages.cs`
- `Platform/Core/HidBridge.Application/DispatchCommandBatchUseCase.cs` (new)
- `Platform/Platform/HidBridge.ControlPlane.Api/ApiCallerContext.cs`
- `Platform/Platform/HidBridge.ControlPlane.Api/Endpoints/SessionEndpoints.cs`
- `Platform/Platform/HidBridge.ControlPlane.Api/Program.cs`
- `Platform/Clients/HidBridge.ControlPlane.Web/Models/OperatorUiReadModels.cs`
- `Platform/Clients/HidBridge.ControlPlane.Web/Services/ControlPlaneApiClient.cs`
- `Platform/Clients/HidBridge.ControlPlane.Web/Components/Pages/SessionDetails.razor`
- `Platform/Clients/HidBridge.ControlPlane.Web/Components/Pages/SessionDetails.razor.js` (new)
- `Platform/Tests/HidBridge.Platform.Tests/ControlPlaneApiClientTests.cs`
- `Platform/Tests/HidBridge.Platform.Tests/DispatchCommandBatchUseCaseTests.cs` (new)

## 2026-03-23 (pr-3 thin wrappers completed for webrtc-stack scripts)

Summary:

- Reworked `run_webrtc_stack.ps1` into a thin bootstrap wrapper focused only on runtime bootstrap + edge-agent/legacy process start + summary output.
- Removed script-side policy/fallback/readiness/keycloak-discovery orchestration from `webrtc-stack`; policy decisions remain API-owned (`/control/ensure`, `/transport/readiness`) and are exercised by smoke/acceptance.
- Kept `webrtc-stack-terminal-b` as thin compatibility proxy over canonical smoke flow.
- Updated platform runbook to document explicit runtime reuse via `-SkipRuntimeBootstrap` and clarify acceptance wrapper behavior.

Detailed notes:

- `Platform/Scripts/run_webrtc_stack.ps1`
- `Platform/Scripts/run_webrtc_stack_terminal_b.ps1`
- `Platform/README.md`

## 2026-03-22 (pr-next-acceptance runner in solution + .net acceptance orchestration)

Summary:

- Added `Platform/Tools/HidBridge.Acceptance.Runner` into both platform solution files so acceptance tooling is a first-class project in IDE/CLI workflows.
- Moved `webrtc-edge-agent-acceptance` orchestration from script-composed stack/smoke flow to dedicated `.NET` runner logic.
- Kept PowerShell task entrypoint as a thin wrapper that forwards arguments to the `.NET` runner and preserves `run.ps1` task compatibility.
- Updated platform docs to describe `.NET` acceptance orchestration path and current wrapper behavior.

Detailed notes:

- `Platform/HidBridge.Platform.sln`
- `Platform/HidBridge.Platform.slnx`
- `Platform/Tools/HidBridge.Acceptance.Runner/HidBridge.Acceptance.Runner.csproj`
- `Platform/Tools/HidBridge.Acceptance.Runner/Program.cs`
- `Platform/Scripts/run_webrtc_edge_agent_acceptance.ps1`
- `Platform/README.md`

## 2026-03-21 (pr-b media runtime scaffold: ffmpeg+dcd preview engine, default path unchanged)

Summary:

- Added pluggable edge media runtime engine abstraction to decouple media-process orchestration from command transport path.
- Added default no-op media runtime engine (`MediaEngine=none`) to preserve existing production behavior.
- Added preview ffmpeg+dcd runtime scaffold (`MediaEngine=ffmpeg-dcd`) with bounded process lifecycle handling and diagnostics snapshots.
- Wired media runtime startup/shutdown into edge worker lifecycle and surfaced runtime telemetry in peer metadata + media stream snapshot payloads.
- Added regression coverage to guarantee Applied ACK relay path remains unchanged when preview media engine is enabled without runtime binaries configured.

Detailed notes:

- `Platform/Edge/HidBridge.EdgeProxy.Agent/Media/IEdgeMediaRuntimeEngine.cs` (new)
- `Platform/Edge/HidBridge.EdgeProxy.Agent/Media/NoOpMediaRuntimeEngine.cs` (new)
- `Platform/Edge/HidBridge.EdgeProxy.Agent/Media/FfmpegDataChannelDotNetMediaRuntimeEngine.cs` (new)
- `Platform/Edge/HidBridge.EdgeProxy.Agent/EdgeProxyWorker.cs`
- `Platform/Tests/HidBridge.Platform.Tests/EdgeProxyOptionsTests.cs`
- `Platform/Tests/HidBridge.Platform.Tests/EdgeProxyWorkerLifecycleTests.cs`

## 2026-03-21 (pr-next-3 dcd direct signal command path with optional relay fallback)

Summary:

- Implemented preview `TransportEngine=dcd` direct command processing from signaling inbox (`WebRtcSignalKind.Command`).
- Added direct ACK signaling path (`WebRtcSignalKind.Ack`) with DCD marker metric `transportEngineDcdDirect=1`.
- Added option `DcdAllowRelayFallback` (default `true`) so DCD mode can fallback to relay queue when no direct signal commands are available.
- Extended worker transport loop to use pluggable control transport engines (`relay` default, `dcd` preview).
- Added regression coverage for relay-compat mode, dcd preview mode, and direct-signal ACK publish flow.

Detailed notes:

- `Platform/Edge/HidBridge.EdgeProxy.Agent/Transport/IEdgeControlTransportEngine.cs` (new)
- `Platform/Edge/HidBridge.EdgeProxy.Agent/Transport/RelayCompatControlEngine.cs` (new)
- `Platform/Edge/HidBridge.EdgeProxy.Agent/Transport/DataChannelDotNetControlEngine.cs` (new)
- `Platform/Edge/HidBridge.EdgeProxy.Agent/EdgeProxyTransportEngineKind.cs` (new)
- `Platform/Edge/HidBridge.EdgeProxy.Agent/EdgeProxyOptions.cs`
- `Platform/Edge/HidBridge.EdgeProxy.Agent/EdgeProxyWorker.cs`
- `Platform/Edge/HidBridge.EdgeProxy.Agent/HidBridge.EdgeProxy.Agent.csproj`
- `Platform/Shared/HidBridge.Contracts/Messages.cs`
- `Platform/Tests/HidBridge.Platform.Tests/EdgeProxyOptionsTests.cs`
- `Platform/Tests/HidBridge.Platform.Tests/EdgeProxyWorkerLifecycleTests.cs`
- `Platform/Edge/HidBridge.EdgeProxy.Agent/README.md`
- `Platform/README.md`

## 2026-03-21 (pr-next-2.2 api dcd control bridge + diagnostics)

Summary:

- Added optional API-side DCD bridge (`IWebRtcDcdControlBridge`) that can dispatch HID commands through DataChannelDotnet before existing signal/relay paths.
- Added runtime flag `EnableDcdControlBridge` with deterministic diagnostics (`dcdControlBridgeEnabled`, `bridgeMode=dcd-control-bridge` when applicable).
- Registered/configured DCD bridge in API runtime via environment settings.
- Added regression tests for bridge-success and bridge-fallback flows in `WebRtcDataChannelRealtimeTransport`.
- Pinned `Microsoft.EntityFrameworkCore.Relational` in SQL persistence project to stabilize EF runtime assembly resolution after package updates.

Detailed notes:

- `Platform/Core/HidBridge.Application/WebRtcDcdControlBridge.cs` (new)
- `Platform/Core/HidBridge.Application/RealtimeTransportFactory.cs`
- `Platform/Core/HidBridge.Application/HidBridge.Application.csproj`
- `Platform/Platform/HidBridge.ControlPlane.Api/Program.cs`
- `Platform/Platform/HidBridge.ControlPlane.Api/ApiRuntimeSettings.cs`
- `Platform/Platform/HidBridge.ControlPlane.Api/Endpoints/SystemEndpoints.cs`
- `Platform/Platform/HidBridge.Persistence.Sql/HidBridge.Persistence.Sql.csproj`
- `Platform/Tests/HidBridge.Platform.Tests/WebRtcDataChannelRealtimeTransportTests.cs`

## 2026-03-21 (pr-1 deterministic uart protocol tests + retry seam)

Summary:

- Extracted UART frame codec into dedicated reusable seam (`UartFrameCodec`) with deterministic CRC/HMAC parsing path.
- Extracted deterministic retry orchestration seam (`UartCommandRetryExecutor`) and wired `HidBridgeUartClient` through it without changing runtime contract.
- Added deterministic unit coverage for frame roundtrip, invalid CRC, invalid HMAC, alternate HMAC key fallback, retry exhaustion, and retry short-circuit on success.
- Expanded UART action normalization coverage for keyboard/mouse aliases.

Detailed notes:

- `Platform/Shared/HidBridge.Transport.Uart/UartFrameCodec.cs` (new)
- `Platform/Shared/HidBridge.Transport.Uart/UartCommandRetryExecutor.cs` (new)
- `Platform/Shared/HidBridge.Transport.Uart/HidBridgeUartClient.cs`
- `Platform/Shared/HidBridge.Transport.Uart/HidBridge.Transport.Uart.csproj`
- `Platform/Tests/HidBridge.Platform.Tests/UartFrameCodecTests.cs` (new)
- `Platform/Tests/HidBridge.Platform.Tests/UartRetryTimeoutTests.cs` (new)
- `Platform/Tests/HidBridge.Platform.Tests/HidBridgeUartCommandDispatcherTests.cs`

## 2026-03-21 (pr-2 media playback-url propagation across edge/api/ui)

Summary:

- Added typed media playback URL field across edge contracts, API transport snapshots/readiness, and Web read models.
- Extended edge media probe to parse playback URL from health payloads (`playbackUrl`/`playbackUri`/`whepUrl`/`streamUrl`) with options fallback.
- Surfaced media playback link in Session Details transport diagnostics panel.
- Added regression assertions for playback URL parsing, registry persistence, relay metrics projection, and readiness projection.

Detailed notes:

- `Platform/Shared/HidBridge.Contracts/Messages.cs`
- `Platform/Edge/HidBridge.Edge.Abstractions/EdgeRuntimeContracts.cs`
- `Platform/Edge/HidBridge.EdgeProxy.Agent/EdgeProxyOptions.cs`
- `Platform/Edge/HidBridge.EdgeProxy.Agent/EdgeProxyMediaReadinessProbe.cs`
- `Platform/Edge/HidBridge.EdgeProxy.Agent/EdgeProxyWorker.cs`
- `Platform/Core/HidBridge.Application/SessionMediaRegistryService.cs`
- `Platform/Core/HidBridge.Application/WebRtcCommandRelayService.cs`
- `Platform/Core/HidBridge.Application/WebRtcRelayReadinessService.cs`
- `Platform/Platform/HidBridge.ControlPlane.Api/Endpoints/TransportEndpoints.cs`
- `Platform/Clients/HidBridge.ControlPlane.Web/Models/OperatorUiReadModels.cs`
- `Platform/Clients/HidBridge.ControlPlane.Web/Components/Pages/SessionDetails.razor`
- `Platform/Clients/HidBridge.ControlPlane.Web/Localization/OperatorText.cs`
- `Platform/Tests/HidBridge.Platform.Tests/EdgeProxyMediaReadinessProbeTests.cs`
- `Platform/Tests/HidBridge.Platform.Tests/SessionMediaRegistryServiceTests.cs`
- `Platform/Tests/HidBridge.Platform.Tests/WebRtcCommandRelayServiceTests.cs`
- `Platform/Tests/HidBridge.Platform.Tests/WebRtcRelayReadinessServiceTests.cs`

## 2026-03-21 (pr-b relay stale-peer guard + smoke retry stabilization)

Summary:

- Added WebRTC relay stale-peer guard so expired heartbeat snapshots no longer count as online peers.
- Added configurable relay staleness threshold (`HIDBRIDGE_WEBRTC_PEER_STALE_AFTER_SEC`, default `15s`) and exposed it via runtime diagnostics.
- Hardened edge-agent smoke by adding bounded retry for transient transport command outcomes (`Timeout` / `E_TRANSPORT_*`) with readiness re-check between attempts.
- Added regression tests covering stale-peer behavior and bridge fallback when relay peer presence is stale.

Detailed notes:

- `Platform/Core/HidBridge.Application/WebRtcCommandRelayOptions.cs`
- `Platform/Core/HidBridge.Application/WebRtcCommandRelayService.cs`
- `Platform/Platform/HidBridge.ControlPlane.Api/Program.cs`
- `Platform/Platform/HidBridge.ControlPlane.Api/ApiRuntimeSettings.cs`
- `Platform/Platform/HidBridge.ControlPlane.Api/Endpoints/SystemEndpoints.cs`
- `Platform/Scripts/run_webrtc_edge_agent_smoke.ps1`
- `Platform/Tests/HidBridge.Platform.Tests/WebRtcCommandRelayServiceTests.cs`
- `Platform/Tests/HidBridge.Platform.Tests/WebRtcDataChannelRealtimeTransportTests.cs`

## 2026-03-21 (pr-a typed media contract + edge/api/ui propagation)

Summary:

- Extended media contracts with typed stream descriptors (`streamKind`, `video`, `audio`) for readiness/health/media-stream payloads.
- Propagated typed media fields through edge agent publish path, API readiness/health projection, and Web operator read models.
- Extended transport diagnostics UI with media kind/video/audio cards.
- Added regression tests for typed media parsing, registry persistence, relay metrics, and readiness projection.

Detailed notes:

- `Platform/Shared/HidBridge.Contracts/Messages.cs`
- `Platform/Edge/HidBridge.Edge.Abstractions/EdgeRuntimeContracts.cs`
- `Platform/Edge/HidBridge.EdgeProxy.Agent/EdgeProxyMediaReadinessProbe.cs`
- `Platform/Edge/HidBridge.EdgeProxy.Agent/EdgeProxyWorker.cs`
- `Platform/Core/HidBridge.Application/SessionMediaRegistryService.cs`
- `Platform/Core/HidBridge.Application/WebRtcRelayReadinessService.cs`
- `Platform/Core/HidBridge.Application/WebRtcCommandRelayService.cs`
- `Platform/Platform/HidBridge.ControlPlane.Api/Endpoints/TransportEndpoints.cs`
- `Platform/Clients/HidBridge.ControlPlane.Web/Models/OperatorUiReadModels.cs`
- `Platform/Clients/HidBridge.ControlPlane.Web/Components/Pages/SessionDetails.razor`
- `Platform/Clients/HidBridge.ControlPlane.Web/Localization/OperatorText.cs`
- `Platform/Tests/HidBridge.Platform.Tests/EdgeProxyMediaReadinessProbeTests.cs`
- `Platform/Tests/HidBridge.Platform.Tests/SessionMediaRegistryServiceTests.cs`
- `Platform/Tests/HidBridge.Platform.Tests/WebRtcCommandRelayServiceTests.cs`
- `Platform/Tests/HidBridge.Platform.Tests/WebRtcRelayReadinessServiceTests.cs`

## 2026-03-21 (webrtc command-deck regression coverage)

Summary:

- Added regression coverage for edge-agent startup route ensure behavior when session route is missing.
- Added regression coverage for full Web command-deck HID scenario set to protect payload mapping and ACK stability.
- Updated integration test relay backplane handlers for current API startup contract (`control/ensure` + explicit session existence fallback).

Detailed notes:

- `Platform/Tests/HidBridge.Platform.Tests/EdgeProxyWorkerLifecycleTests.cs`
- `Platform/Tests/HidBridge.Platform.Tests/WebRtcEdgeAgentIntegrationTests.cs`

## 2026-03-21

Summary:

- Added strict ops verification lane: `ops-slo-security-verify` with JSON output and strict fail flags.
- Integrated SLO/security verification into `ci-local` and `full` orchestration flows.
- Switched platform runtime defaults to strict security posture in Docker (`auth enabled`, `header fallback disabled`, strict bearer/caller-context patterns).
- Switched Web runtime login to OIDC-only strict mode for platform runtime profile.
- Added robust token-authority fallback in ops verify script and fixed PowerShell `$Host` collision issue.
- Updated platform and architecture docs for strict runtime + SLO/security verification flow.

Detailed notes:

- `Platform/README.md`
- `Docs/SystemArchitecture/HidBridge_Runtime_Flow_2026-03-19_UA.md`
- `Docs/SystemArchitecture/README.md`

## 2026-02-19

Summary:

- Unified mutable runtime state under `hidcontrol.state.json` (profiles, active profile, room-profile bindings).
- Added storage observability and authority diagnostics (`/status/storage`, storage events).
- Hardened WebRTC room lifecycle (rotation/binding cleanup regressions).
- Finalized watchdog skip policy (`watchdog_skip_reason`) and suppression tests.
- Stabilized audio health/probe UX edge-cases.
- Formalized legacy config/storage deprecations in API responses and docs.

Detailed notes:

- `Docs/RELEASE_NOTES_2026-02-19.md`
