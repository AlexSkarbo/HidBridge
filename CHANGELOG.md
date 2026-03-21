# Changelog

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
