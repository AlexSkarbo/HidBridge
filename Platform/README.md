# Platform

This folder is the greenfield implementation area for the new modular HidBridge platform.

Runtime baseline:
- `.NET 10`

Repository strategy:
- `Tools/` stays intact as the legacy/reference implementation during migration.
- `Platform/` is the new clean-architecture baseline.
- `WebRtcTests/exp-022-datachanneldotnet/` remains the integration lab.
- Only after functional parity is reached should legacy code be archived or relocated.

Current P0 skeleton:
- `Shared/HidBridge.Contracts`
- `Shared/HidBridge.Abstractions`
- `Shared/HidBridge.Hid`
- `Shared/HidBridge.Transport.Uart`
- `Core/HidBridge.Domain`
- `Core/HidBridge.Application`
- `Platform/HidBridge.ControlPlane.Api`
- `Platform/HidBridge.SessionOrchestrator`
- `Platform/HidBridge.ConnectorHost`
- `Platform/HidBridge.Persistence`
- `Platform/HidBridge.Persistence.Sql`
- `Platform/HidBridge.Persistence.Sql.Migrations`
- `Connectors/HidBridge.Connectors.HidBridgeUart`
- `Tests/HidBridge.Platform.Tests`

Current runtime flow:
- `HidBridge.ControlPlane.Api` bootstraps registered connectors on startup.
- `HidBridge.Connectors.HidBridgeUart` exposes HID mouse/keyboard execution over the UART transport.
- Sessions bind to a target agent, so per-command `args.agentId` is optional after session open.
- Collaboration baseline now includes explicit `session_participants`, `session_shares`, and invitation approval flows.
- `P4` baseline now adds policy-driven moderation and active controller arbitration through per-session control leases.
- Collaboration read models now expose session dashboards, share dashboards, participant activity views, and operator timelines.
- Operations dashboards now expose fleet inventory, audit diagnostics, and telemetry diagnostics read models.
- `HidBridge.Persistence` stores connector, endpoint, session, audit, and telemetry snapshots in JSON files under the configured data root.
- `HidBridge.Persistence.Sql` provides the SQL-backed persistence baseline for `agents`, `endpoints`, `sessions`, `audit`, `telemetry`, and `command_journal`.
- Command dispatch now writes a persisted `command_journal` entry with agent/session/collaboration context.
- Unified session timeline read models can now merge audit, telemetry, and command journal records.
- Tenant/org-aware enforcement now also runs inside projection/dashboard/diagnostics services, not only in endpoint handlers.
- `operator.admin` has a cross-tenant override for service/query reads; scoped callers remain limited to visible sessions.
- Structured authorization denial payloads now surface into `HidBridge.ControlPlane.Web`, including denial code, required roles, and required scope.
- Denial UX is now surfaced across fleet, audit, telemetry, and session web flows through a shared authorization-denied panel.
- The web shell now forwards the current OIDC access token to `HidBridge.ControlPlane.Api` and can still propagate explicit caller-scope headers for compatibility.
- Policy/config persistence is now in active baseline through `IPolicyStore` with file/sql implementations for policy scopes and policy assignments; SQL migration/runtime activation is wired.
- Shared operator policy resolution now runs in runtime:
  - persisted policy assignments can augment effective caller roles
  - persisted policy scopes can fill missing tenant/org scope
  - API requests are enriched before endpoint/query enforcement
- Policy bootstrap now writes:
  - persisted policy revisions through `IPolicyRevisionStore`
  - audit events with category `policy`
- Diagnostics now include:
  - `GET /api/v1/diagnostics/policies/revisions`
- Policy revision lifecycle baseline now exists:
  - configurable retention window
  - configurable maximum revisions per entity
  - background pruning with `policy.retention` audit events

Micro meet sales/demo baseline:
- Suggested public positioning:
  - shared control rooms for real endpoints, not just screen sharing
  - explicit operator roles and control handoff
  - room timeline for who did what
- Go-to-market drafts live in:
  - `Docs/GoToMarket/MicroMeet_GitHub_Package_UA.md`
  - `Docs/GoToMarket/MicroMeet_Reddit_Post_UA.md`
  - `Docs/GoToMarket/MicroMeet_LinkedIn_Post_UA.md`
  - `Docs/GoToMarket/MicroMeet_Demo_Runbook_UA.md`
- `Fleet Overview` can now start a room directly from an endpoint row and exposes a quick-launch card for the first idle endpoint.
- `Session Room` now acts as the primary operator surface for one live session.
- The room exposes:
  - session snapshot
  - participants
  - invitation moderation
  - invite/request actions
  - control handoff actions
  - operator timeline
  - room link for second-browser demo
  - a compact demo checklist
- Recommended manual demo path:
  1. Open `/`
  2. Click `Launch room` from the quick-launch card or `Start session` from a device row
  3. Land in `/sessions/{id}`
  4. Copy the room link or open it in a second browser profile
  5. Use `Grant invite` or `Request invite`
  6. Accept/reject/revoke invitations
  7. Request/release/force-takeover control
  8. Show the room timeline updating
- A compact sales/demo walkthrough is captured in:
  - `Docs/GoToMarket/MicroMeet_Demo_Runbook_UA.md`

Default ControlPlane API:
- `http://localhost:18093`
- OpenAPI document: `http://localhost:18093/openapi/v1.json`
- Scalar UI: `http://localhost:18093/scalar`
- health: `GET /health`
- UART runtime config: `GET /api/v1/runtime/uart`
- agents: `GET /api/v1/agents`
- endpoints: `GET /api/v1/endpoints`
- sessions: `POST /api/v1/sessions`, `GET /api/v1/sessions`
- collaboration:
  - `GET /api/v1/sessions/{sessionId}/participants`
  - `POST /api/v1/sessions/{sessionId}/participants`
  - `DELETE /api/v1/sessions/{sessionId}/participants/{participantId}`
  - `GET /api/v1/sessions/{sessionId}/shares`
  - `POST /api/v1/sessions/{sessionId}/shares`
  - `POST /api/v1/sessions/{sessionId}/shares/{shareId}/accept`
  - `POST /api/v1/sessions/{sessionId}/shares/{shareId}/reject`
  - `POST /api/v1/sessions/{sessionId}/shares/{shareId}/revoke`
  - `GET /api/v1/sessions/{sessionId}/invitations`
  - `POST /api/v1/sessions/{sessionId}/invitations/requests`
  - `POST /api/v1/sessions/{sessionId}/invitations/{shareId}/approve`
  - `POST /api/v1/sessions/{sessionId}/invitations/{shareId}/decline`
  - `GET /api/v1/collaboration/sessions/{sessionId}/summary`
  - `GET /api/v1/collaboration/sessions/{sessionId}/lobby`
  - `GET /api/v1/collaboration/sessions/{sessionId}/dashboard`
  - `GET /api/v1/collaboration/sessions/{sessionId}/shares/dashboard`
  - `GET /api/v1/collaboration/sessions/{sessionId}/participants/activity`
  - `GET /api/v1/collaboration/sessions/{sessionId}/operators/timeline?principalId={principalId}&take={n}`
- control arbitration:
  - `GET /api/v1/sessions/{sessionId}/control`
  - `POST /api/v1/sessions/{sessionId}/control/request`
  - `POST /api/v1/sessions/{sessionId}/control/grant`
  - `POST /api/v1/sessions/{sessionId}/control/force-takeover`
  - `POST /api/v1/sessions/{sessionId}/control/release`
  - `GET /api/v1/collaboration/sessions/{sessionId}/control/dashboard`
- transport diagnostics/signaling:
  - `GET /api/v1/sessions/{sessionId}/transport/health?provider={uart|webrtc}`
    - response now includes typed relay peer readiness fields when available: `onlinePeerCount`, `lastPeerSeenAtUtc`, `lastPeerState`, `lastPeerFailureReason`, `lastPeerConsecutiveFailures`, `lastPeerReconnectBackoffMs`, `lastRelayAckAtUtc`
  - `POST /api/v1/sessions/{sessionId}/transport/webrtc/signals`
  - `GET /api/v1/sessions/{sessionId}/transport/webrtc/signals?recipientPeerId={peerId}&afterSequence={n}&limit={n}`
  - `POST /api/v1/sessions/{sessionId}/transport/webrtc/peers/{peerId}/online`
  - `POST /api/v1/sessions/{sessionId}/transport/webrtc/peers/{peerId}/offline`
  - `GET /api/v1/sessions/{sessionId}/transport/webrtc/peers`
  - `GET /api/v1/sessions/{sessionId}/transport/webrtc/commands?peerId={peerId}&afterSequence={n}&limit={n}`
  - `POST /api/v1/sessions/{sessionId}/transport/webrtc/commands/{commandId}/ack`
- command journal:
- `GET /api/v1/sessions/{sessionId}/commands/journal`
  - `GET /api/v1/commands?sessionId={sessionId}`
- events:
  - `GET /api/v1/events/audit`
  - `GET /api/v1/events/telemetry`
  - `GET /api/v1/events/timeline/{sessionId}?take={n}`
- dashboards:
  - `GET /api/v1/dashboards/inventory`
  - `GET /api/v1/dashboards/audit?take={n}`
  - `GET /api/v1/dashboards/telemetry?take={n}`
- optimized query projections:
  - `GET /api/v1/projections/sessions?state={state}&agentId={agentId}&endpointId={endpointId}&principalId={principalId}&skip={n}&take={n}`
  - `GET /api/v1/projections/endpoints?status={status}&agentId={agentId}&connectorType={connectorType}&activeOnly={bool}&skip={n}&take={n}`
  - `GET /api/v1/projections/audit?category={category}&sessionId={sessionId}&principalId={principalId}&sinceUtc={utc}&skip={n}&take={n}`
  - `GET /api/v1/projections/telemetry?scope={scope}&sessionId={sessionId}&metricName={metricName}&sinceUtc={utc}&skip={n}&take={n}`
- replay/archive diagnostics:
  - `GET /api/v1/diagnostics/replay/sessions/{sessionId}?take={n}`
  - `GET /api/v1/diagnostics/archive/summary?sessionId={sessionId}&sinceUtc={utc}`
  - `GET /api/v1/diagnostics/archive/audit?sessionId={sessionId}&category={category}&principalId={principalId}&sinceUtc={utc}&skip={n}&take={n}`
  - `GET /api/v1/diagnostics/archive/telemetry?sessionId={sessionId}&scope={scope}&metricName={metricName}&sinceUtc={utc}&skip={n}&take={n}`
  - `GET /api/v1/diagnostics/archive/commands?sessionId={sessionId}&status={status}&sinceUtc={utc}&skip={n}&take={n}`

Run example:
```bash
dotnet run --project Platform/Platform/HidBridge.ControlPlane.Api/HidBridge.ControlPlane.Api.csproj
```

Useful environment variables:
- `HIDBRIDGE_UART_PORT`
- `HIDBRIDGE_UART_BAUD`
- `HIDBRIDGE_UART_HMAC_KEY`
- `HIDBRIDGE_UART_MASTER_SECRET`
- `HIDBRIDGE_UART_MOUSE_SELECTOR`
- `HIDBRIDGE_UART_KEYBOARD_SELECTOR`
- `HIDBRIDGE_UART_COMMAND_TIMEOUT_MS`
- `HIDBRIDGE_UART_INJECT_TIMEOUT_MS`
- `HIDBRIDGE_UART_INJECT_RETRIES`
- `HIDBRIDGE_AGENT_ID`
- `HIDBRIDGE_ENDPOINT_ID`
- `HIDBRIDGE_DATA_ROOT`
- `HIDBRIDGE_PERSISTENCE_PROVIDER`
- `HIDBRIDGE_SQL_CONNECTION`
- `HIDBRIDGE_SQL_SCHEMA`
- `HIDBRIDGE_SQL_APPLY_MIGRATIONS`
- `HIDBRIDGE_CONTROL_LEASE_SECONDS`
- `HIDBRIDGE_AUTH_ENABLED`
- `HIDBRIDGE_AUTH_AUTHORITY`
- `HIDBRIDGE_AUTH_AUDIENCE`
- `HIDBRIDGE_AUTH_REQUIRE_HTTPS_METADATA`
- `HIDBRIDGE_POLICY_REVISION_MAINTENANCE_INTERVAL_SECONDS`
- `HIDBRIDGE_POLICY_REVISION_RETENTION_DAYS`
- `HIDBRIDGE_POLICY_REVISION_MAX_PER_ENTITY`
- `HIDBRIDGE_TRANSPORT_PROVIDER`
- `HIDBRIDGE_TRANSPORT_PROVIDER_OVERRIDES` (example: `endpoint_local_demo=uart;endpoint_remote_lab=webrtc`)
- `HIDBRIDGE_WEBRTC_REQUIRE_CAPABILITY`
- `HIDBRIDGE_WEBRTC_ENABLE_CONNECTOR_BRIDGE`
- `HIDBRIDGE_TRANSPORT_FALLBACK_TO_DEFAULT_ON_WEBRTC_ERROR` (default: `true`; retries once via default provider when WebRTC route returns transport error and no explicit `transportProvider` override was requested)

WebRTC relay command path:
- peers publish online/offline presence via `/transport/webrtc/peers/*`;
- commands are queued by transport and consumed from `/transport/webrtc/commands`;
- peer-side ACK is posted to `/transport/webrtc/commands/{commandId}/ack`;
- if relay ACK times out and fallback is enabled, dispatch retries once via default provider.

UART key-mode notes:
- If `HIDBRIDGE_UART_HMAC_KEY` is not set but `HIDBRIDGE_UART_MASTER_SECRET` is set, bootstrap HMAC key defaults to `HIDBRIDGE_UART_MASTER_SECRET`.
- The connector tries derived key mode when `HIDBRIDGE_UART_MASTER_SECRET` is configured.
- If derived mode fails to receive ACK, transport automatically falls back to bootstrap key mode (same behavior as in `Tools/HidControlServer`).

Caller-scope integration headers:
- `X-HidBridge-UserId`
- `X-HidBridge-PrincipalId`
- `X-HidBridge-TenantId`
- `X-HidBridge-OrganizationId`
- `X-HidBridge-Role`

Authorization failures:
- `ControlPlane.Api` now returns structured `403` denial payloads with:
  - stable `code`
  - denial `message`
  - caller context
  - required roles / tenant / organization scope when known

Developer request examples:
- `Platform/Platform/HidBridge.ControlPlane.Api/HidBridge.ControlPlane.Api.http`

Test entrypoint:
- `Platform/run_all_tests.ps1`
- `Platform/run_checks.ps1`
- `Platform/run_smoke.ps1`
- `Platform/run_backend_smoke.ps1`
- `Platform/run_file_smoke.ps1`
- `Platform/run_sql_smoke.ps1`
- `Platform/run_demo_flow.ps1`
- `Platform/run_demo_gate.ps1`
- `Platform/run_uart_diagnostics.ps1`
- The runner restores the solution, builds it, and then runs `HidBridge.Platform.Tests`.
- `Platform/run_checks.ps1` runs the unit-test pipeline first and then runs the selected smoke profile.
- `Platform/run_smoke.ps1` is the single entrypoint wrapper that dispatches to file or SQL smoke validation.
- `Platform/run_backend_smoke.ps1` is the canonical smoke runner for both persistence providers.
- `Platform/run_file_smoke.ps1` wraps the generic smoke runner with `HIDBRIDGE_PERSISTENCE_PROVIDER=File` and an isolated data root.
- `Platform/run_sql_smoke.ps1` wraps the generic smoke runner with `HIDBRIDGE_PERSISTENCE_PROVIDER=Sql`, applies migrations, probes `/health`, opens one session, and reads back persisted snapshots.
- `Platform/run_demo_gate.ps1` runs the deterministic demo gate (`open session -> request control -> dispatch command -> verify journal -> close session`).
  - default mode is transport-agnostic (`action=noop`) and validates auth/session/control/journal pipeline deterministically.
  - use `-RequireDeviceAck` only when you explicitly want UART device-ack validation.
- `Platform/run_uart_diagnostics.ps1` runs a selector sweep (`itfSel`) across multiple command scenarios (`keyboard.shortcut`, `keyboard.text`, `mouse.move`, `keyboard.reset`) and prints a consolidated matrix.
- Collaboration tests validate:
  - participant upsert/remove
  - share grant/accept/revoke
  - participant materialization from accepted shares
- invitation request -> approval -> accept/decline lifecycle
- command journal persistence and timeline composition
- collaboration dashboard and participant activity read models
- control request / release / takeover flow with active controller lease enforcement
- operations dashboard read models:
  - inventory dashboard
  - audit dashboard
  - telemetry dashboard
- optimized query projections:
  - session projections
  - endpoint projections
  - audit projections
  - telemetry projections
- replay/archive diagnostics:
  - session replay bundle
  - archive summary
  - archive audit slices
  - archive telemetry slices
  - archive command slices
- The SQL smoke script requires a valid PostgreSQL connection string; the bundled default `postgres/postgres` credentials are only a local placeholder.

Persistence provider selection:
- `HIDBRIDGE_PERSISTENCE_PROVIDER=File` uses JSON snapshot stores.
- `HIDBRIDGE_PERSISTENCE_PROVIDER=Sql` uses the PostgreSQL-backed stores from `HidBridge.Persistence.Sql`.
- `HIDBRIDGE_SQL_APPLY_MIGRATIONS=true` applies pending EF Core migrations automatically on API startup.
- SQL migrations live in `Platform/Platform/HidBridge.Persistence.Sql.Migrations`.

Local PostgreSQL for Platform:
- `docker-compose.postgis.yml` remains a legacy/older infrastructure file and should not be reused as the default Platform SQL backend.
- Use `docker-compose.platform-sql.yml` for the new Platform baseline.
- Start:
  - `docker compose -f docker-compose.platform-sql.yml up -d`
- Default connection string for that container:
  - `Host=127.0.0.1;Port=5434;Database=hidbridge;Username=hidbridge;Password=hidbridge`
- Example unified smoke-run:
  - `powershell -ExecutionPolicy Bypass -File Platform/run_smoke.ps1 -Provider File`
  - `powershell -ExecutionPolicy Bypass -File Platform/run_smoke.ps1 -Provider Sql -ConnectionString "Host=127.0.0.1;Port=5434;Database=hidbridge;Username=hidbridge;Password=hidbridge"`
- Example full checks run:
  - `powershell -ExecutionPolicy Bypass -File Platform/run_checks.ps1 -Provider File`
  - `powershell -ExecutionPolicy Bypass -File Platform/run_checks.ps1 -Provider Sql -ConnectionString "Host=127.0.0.1;Port=5434;Database=hidbridge;Username=hidbridge;Password=hidbridge"`
- Example file smoke-run:
  - `powershell -ExecutionPolicy Bypass -File Platform/run_file_smoke.ps1`
- Example smoke-run:
  - `powershell -ExecutionPolicy Bypass -File Platform/run_sql_smoke.ps1 -ConnectionString "Host=127.0.0.1;Port=5434;Database=hidbridge;Username=hidbridge;Password=hidbridge"`

One-command demo flow:
- `powershell -ExecutionPolicy Bypass -File Platform/run.ps1 -Task demo-flow`
- Step-by-step runbooks:
  - `Docs/GoToMarket/MicroMeet_Demo_Runbook_UA.md`
  - `Docs/GoToMarket/MicroMeet_Demo_Runbook_EN.md`
- Default sequence:
  - realm reset (`identity-reset`)
  - runtime checks (`doctor`)
  - CI-local checks (`ci-local`)
  - demo seed (`demo-seed`) closes active sessions in scope and ensures at least one idle endpoint for quick-start
  - demo runtime gate (`demo-gate`) verifies live room flow end-to-end before UI startup
  - starts API (`http://127.0.0.1:18093`) and Web (`http://127.0.0.1:18110`)
- Useful switches:
  - `-SkipIdentityReset`
    - use this when you already have a working realm and do not want to reset IdP users/settings
  - `-SkipServiceStartup`
  - `-SkipDemoGate`
    - use this only when you intentionally want startup without command-path verification
  - `-IncludeWebRtcEdgeAgentSmoke`
    - runs WebRTC transport gate path (`demo-gate` in `webrtc-datachannel` mode) and WebRTC edge-agent smoke
    - demo-flow now auto-adjusts runtime behavior for this mode:
      - forces service reuse only when API health is already `ok` (to preserve live relay session)
      - if API is down, starts runtime services normally
      - skips CI-local to avoid recycling API runtime while relay peer is active
      - auto-runs WebRTC stack bootstrap (exp-022 + edge proxy agent) when API was restarted in this run or when control health is not ready
      - applies passive UART API runtime settings in this mode to avoid COM lock against edge stack
      - runs Doctor without API-required gate (API is validated by Start API step in this flow)
      - when Demo Gate is enabled, standalone `WebRTC Edge Agent Smoke` step is skipped (Demo Gate already validates WebRTC command path)
    - useful when `webrtc-stack` is already running with active control websocket health
  - `-WebRtcControlHealthUrl` (default `http://127.0.0.1:28092/health`)
  - `-WebRtcRequestTimeoutSec` (default `15`)
  - `-WebRtcControlHealthAttempts` (default `20`)
  - `-ReuseRunningServices`
    - by default `demo-flow` restarts API/Web to apply deterministic demo env
    - when used, `Start API` now requires `/health` to be reachable (not just open TCP port)
  - `-IncludeFull`
  - `-NoBuild`
  - `-ShowServiceWindows` (default is hidden service windows)

Manual demo seed only:
- `powershell -ExecutionPolicy Bypass -File Platform/run.ps1 -Task demo-seed -BaseUrl http://127.0.0.1:18093`
- by default `demo-seed` gets bearer token via `controlplane-smoke / operator.smoke.admin`

Manual demo gate only:
- `powershell -ExecutionPolicy Bypass -File Platform/run.ps1 -Task demo-gate -BaseUrl http://127.0.0.1:18093`
- WebRTC relay gate mode (requires active `webrtc-stack` session env + control health):
  - `powershell -ExecutionPolicy Bypass -File Platform/run.ps1 -Task demo-gate -BaseUrl http://127.0.0.1:18093 -TransportProvider webrtc-datachannel -ControlHealthUrl http://127.0.0.1:28092/health -OutputJsonPath Platform/.logs/demo-gate-webrtc.result.json`
- optional report output:
  - `powershell -ExecutionPolicy Bypass -File Platform/run.ps1 -Task demo-gate -BaseUrl http://127.0.0.1:18093 -OutputJsonPath Platform/.logs/demo-gate.result.json`
- strict UART ack mode:
  - `powershell -ExecutionPolicy Bypass -File Platform/run.ps1 -Task demo-gate -BaseUrl http://127.0.0.1:18093 -RequireDeviceAck -KeyboardInterfaceSelector 1`
  - equivalent forward-args form:
    - `powershell -ExecutionPolicy Bypass -File Platform/run.ps1 -Task demo-gate -ForwardArgs @('-RequireDeviceAck','-KeyboardInterfaceSelector','1')`
  - note: do not use `--` argument separator with `run.ps1`; use direct args or `-ForwardArgs`.

Manual WebRTC relay smoke (peer queue + ack + transport health):
- `powershell -ExecutionPolicy Bypass -File Platform/run.ps1 -Task webrtc-relay-smoke -BaseUrl http://127.0.0.1:18093`
- optional JSON summary output:
  - `powershell -ExecutionPolicy Bypass -File Platform/run.ps1 -Task webrtc-relay-smoke -BaseUrl http://127.0.0.1:18093 -OutputJsonPath Platform/.logs/webrtc-relay-smoke.result.json`
- this scenario force-routes command dispatch through `transportProvider=webrtc`, simulates peer polling (`/transport/webrtc/commands`), publishes ack (`/transport/webrtc/commands/{commandId}/ack`), checks transport health, then cleans up session + peer presence.
- if relay smoke reports `endpointSupportsWebRtc=false`, restart API with endpoint capability override:
  - `setx HIDBRIDGE_ENDPOINT_EXTRA_CAPABILITIES "transport.webrtc.datachannel.v1@1.0"` (new terminal required), or
  - in current shell: `$env:HIDBRIDGE_ENDPOINT_EXTRA_CAPABILITIES="transport.webrtc.datachannel.v1@1.0"` then restart API.
- when running external WebRTC peer adapter (`exp-022`) against the same UART port, enable passive UART health mode to avoid API-side COM lock during registration/heartbeat:
  - `$env:HIDBRIDGE_UART_PASSIVE_HEALTH_MODE="true"` (then restart API)
  - `$env:HIDBRIDGE_UART_RELEASE_PORT_AFTER_EXECUTE="true"` (recommended together with passive mode)

Real WebRTC peer adapter (exp-022 `dc-hid-poc` bridge):
- preferred stack launcher (starts API/Web + exp-022 + service-based edge proxy agent):
  - `powershell -ExecutionPolicy Bypass -File Platform/run.ps1 -Task webrtc-stack -ForwardArgs @('-StopExisting','-ControlWsUrl','ws://127.0.0.1:28092/ws/control','-UartPort','COM6','-UartHmacKey','your-master-secret')`
  - stack bootstrap now waits until relay peer is reported online in API before returning summary (default timeout: `30s`, override with `-PeerReadyTimeoutSec`).
- one-command smoke for the running stack (expects `Applied`):
  - `powershell -ExecutionPolicy Bypass -File Platform/run.ps1 -Task webrtc-edge-agent-smoke -ForwardArgs @('-ControlHealthUrl','http://127.0.0.1:28092/health','-OutputJsonPath','Platform/.logs/webrtc-edge-agent-smoke.result.json')`
  - smoke now waits for WebRTC transport readiness by typed `/transport/health` fields before dispatching command; disable this check only for compatibility troubleshooting via `-SkipTransportHealthCheck`.
  - readiness polling knobs: `-TransportHealthAttempts`, `-TransportHealthDelayMs`.
- include the WebRTC smoke as part of `demo-flow`:
  - `powershell -ExecutionPolicy Bypass -File Platform/run.ps1 -Task demo-flow -SkipIdentityReset -IncludeWebRtcEdgeAgentSmoke -WebRtcControlHealthUrl "http://127.0.0.1:28092/health"`
  - optional relay-readiness tuning for `demo-flow`/`demo-gate`/`webrtc-edge-agent-smoke`: `-TransportHealthAttempts`, `-TransportHealthDelayMs`, `-SkipTransportHealthCheck`.
- the stack now launches `Platform/Edge/HidBridge.EdgeProxy.Agent` (dotnet worker) instead of the legacy PowerShell adapter runtime.
- manual direct run of service-based edge proxy agent:
  - `dotnet run --project Platform/Edge/HidBridge.EdgeProxy.Agent/HidBridge.EdgeProxy.Agent.csproj`
  - required env vars:
    - `HIDBRIDGE_EDGE_PROXY_BASEURL`
    - `HIDBRIDGE_EDGE_PROXY_SESSIONID`
    - `HIDBRIDGE_EDGE_PROXY_PEERID`
    - `HIDBRIDGE_EDGE_PROXY_ENDPOINTID`
    - `HIDBRIDGE_EDGE_PROXY_CONTROLWSURL`
- legacy script adapter is kept only as compatibility harness during migration.
- task entry:
  - `powershell -ExecutionPolicy Bypass -File Platform/run.ps1 -Task webrtc-peer-adapter -ForwardArgs @('-SessionId','room-...','-PeerId','peer-local-exp022','-StartExp022')`
- direct wrapper:
  - `powershell -ExecutionPolicy Bypass -File Platform/run_webrtc_peer_adapter.ps1 -SessionId room-... -PeerId peer-local-exp022 -StartExp022`
- behavior:
  - auto-creates session when `-SessionId` is missing (reuses ready endpoint; provider=`webrtc-datachannel`)
  - marks peer online via `/transport/webrtc/peers/{peerId}/online`
  - sends heartbeat signals via `/transport/webrtc/signals`
  - polls relay queue via `/transport/webrtc/commands`
  - executes command payloads through exp-022 websocket (`/ws/control`)
  - publishes ack via `/transport/webrtc/commands/{commandId}/ack`
  - marks peer offline on exit
- useful options:
  - `-ControlWsUrl ws://127.0.0.1:18092/ws/control`
  - `-DurationSec 120` (run loop for bounded duration)
  - `-SingleRun` (single poll/ack pass)
  - `-EnsureSession $false` (fail fast instead of auto-creating session)
  - `-OutputJsonPath Platform/.logs/webrtc-peer-adapter.result.json`

Manual UART diagnostics (selector sweep):
- `powershell -ExecutionPolicy Bypass -File Platform/run.ps1 -Task uart-diagnostics -BaseUrl http://127.0.0.1:18093`
- custom selector set + json report:
  - `powershell -ExecutionPolicy Bypass -File Platform/run.ps1 -Task uart-diagnostics -BaseUrl http://127.0.0.1:18093 -OutputJsonPath Platform/.logs/uart-diagnostics.result.json -InterfaceSelectorsCsv "0,1,2,3"`
- notes:
  - diagnostics preflight automatically attempts to close non-terminal visible sessions before endpoint selection.
  - if no successful probe exists, script exits with code `1` and keeps a full matrix in console/JSON output.
  - if command journal/UI shows `Applied` but the target PC has no visible effect, prefer auto selectors:
    - `HIDBRIDGE_UART_MOUSE_SELECTOR=255`
    - `HIDBRIDGE_UART_KEYBOARD_SELECTOR=254`
    - then rerun `uart-diagnostics` to confirm command path behavior.

Bulk room cleanup actions:
- close failed rooms:
  - `powershell -ExecutionPolicy Bypass -File Platform/run.ps1 -Task close-failed-rooms -BaseUrl http://127.0.0.1:18093`
  - dry run:
    - `powershell -ExecutionPolicy Bypass -File Platform/run.ps1 -Task close-failed-rooms -BaseUrl http://127.0.0.1:18093 -ForwardArgs @('-DryRun')`
- close stale rooms (non-active sessions older than threshold):
  - `powershell -ExecutionPolicy Bypass -File Platform/run.ps1 -Task close-stale-rooms -BaseUrl http://127.0.0.1:18093 -StaleAfterMinutes 30`
  - dry run:
    - `powershell -ExecutionPolicy Bypass -File Platform/run.ps1 -Task close-stale-rooms -BaseUrl http://127.0.0.1:18093 -StaleAfterMinutes 30 -ForwardArgs @('-DryRun')`

Thin operator UI shell:
- open design backlog:
  - `Docs/SystemArchitecture/HidBridge_ControlPlane_Web_Design_Questions_UA.md`
- identity / SSO baseline:
  - `Docs/SystemArchitecture/HidBridge_Identity_SSO_Baseline_UA.md`
- `Clients/HidBridge.ControlPlane.Web`
- stack:
  - `Blazor Web App`
  - `Tailwind CSS` build pipeline (`package.json` + `tailwind.config.cjs`)
  - design baseline inherited from `landing/index.html`
- Tailwind commands (run in `Platform/Clients/HidBridge.ControlPlane.Web`):
  - `npm install`
  - `npm run tailwind:build`
  - `npm run tailwind:watch`
  - output: `wwwroot/tailwind.generated.css`
- current baseline screens:
  - `Fleet Overview`
  - `Ops Status`
  - `Session Details`
  - `Audit Dashboard`
  - `Telemetry Dashboard`
  - `Settings`
- operator actions already wired into `Session Details`:
  - `request control`
  - `release control`
  - `force takeover`
  - `approve invitation`
  - `decline invitation`
- localization and theme baseline:
  - `en` default
  - `uk` secondary
  - browser locale auto-detection
  - manual culture override in `Settings`
  - manual theme override in `Settings` is currently tracked as an open stabilization issue
  - responsive layout aligned with `landing/index.html`
  - core layout styling no longer depends on a delayed Tailwind browser refresh
- identity/access UX baseline:
  - current operator identity is surfaced in shell header and session details
  - roles / tenant / organization are projected from claims into the shell
  - moderation affordances now explain why actions are allowed or denied
  - auth provider mode is surfaced in the shell (`Development` vs `OIDC`)
- default web shell config points to:
  - `http://127.0.0.1:18093`
- run example:
```bash
dotnet run --project Platform/Clients/HidBridge.ControlPlane.Web/HidBridge.ControlPlane.Web.csproj
```

Security direction for the web shell:
- `HidBridge.ControlPlane.Web` is an OIDC client, not the platform identity server
- baseline implementation now exists in `Clients/HidBridge.ControlPlane.Web`:
  - cookie-backed web session
  - optional `OIDC` challenge flow
  - development operator fallback identity
  - authorization policies: `OperatorViewer`, `OperatorModerator`, `OperatorAdmin`
  - tenant/org seams projected from claims into the web shell
  - auth inspection endpoint:
    - `GET /auth/status`
- use server-side web sessions, not long-lived browser bearer tokens
- target model:
  - `OIDC + cookie session` for human operators
  - `service principals` for machine-to-machine automation
  - `RBAC + ABAC` for session/control authorization
- full tenant/org enforcement should be added after the web shell baseline is stabilized
- centralized dev IdP baseline:
  - `docker-compose.identity.yml`
  - `Platform/Identity/Keycloak/README.md`
  - `Platform/Identity/Keycloak/README_EN.md`
  - `Platform/Identity/Keycloak/realm-import/hidbridge-dev-realm.json`
  - local username/password + external IdP federation through Keycloak


Оновлення web shell / Identity baseline:
- коли `Identity:Enabled=false`, development fallback identity тепер автентифікується автоматично без ручного login flow;
- коли `Identity:Enabled=true` і `Identity:Onboarding:Enabled=true`, web shell виконує auto-onboarding OIDC користувача на `OnTokenValidated`:
  - гарантує групу `hidbridge-operators` + роль `operator.viewer`;
  - нормалізує user attributes (`tenant_id`, `org_id`, `principal_id`);
  - додає користувача в операторську групу idempotent-режимом;
- ручний theme switch лишається окремим відкритим дефектом web shell і винесений у дизайн-backlog;
- `html, body` background зроблено напівпрозорим, щоб grid background із `landing/index.html` читався стабільно.


Оновлення стану `2026-03-03`:
- login/logout між `HidBridge.ControlPlane.Web` і `Keycloak` перевірено локально;
- централізований SSO baseline підтверджено практично;
- наступний practical step: `Google` як перший external IdP у `Keycloak`, далі той самий onboarding pattern для інших провайдерів.

Оновлення стану `2026-03-11`:
- додано recovery/fix baseline для випадку втрати external IdP під час realm reset;
- `Reset-HidBridgeDevRealm.ps1` тепер має guardrail проти випадкової втрати `identityProviders`;
- `Sync-HidBridgeDevRealm.ps1` підтримує external provider config (`google-oidc.local.json`) і idempotent conflict handling для mapper-ів;
- у web shell додано claim mapping для `createdTimestamp/created_at/user_created_at`, тому `Settings -> User added at` працює після повторного login.


Практичний runbook для Google через Keycloak:
- `Docs/SystemArchitecture/HidBridge_Google_Keycloak_Runbook_UA.md`
- EN mirror:
  - `Docs/SystemArchitecture/HidBridge_Google_Keycloak_Runbook_EN.md`


Практичний runbook для claim mapping у Keycloak:
- `Docs/SystemArchitecture/HidBridge_Keycloak_Claim_Mapping_Runbook_UA.md`
- EN mirror:
  - `Docs/SystemArchitecture/HidBridge_Keycloak_Claim_Mapping_Runbook_EN.md`


Click-by-click runbook для Keycloak UI claim mapping:
- `Docs/SystemArchitecture/HidBridge_Keycloak_UI_Claim_Mapping_Clickpath_UA.md`
- EN mirror:
  - `Docs/SystemArchitecture/HidBridge_Keycloak_UI_Claim_Mapping_Clickpath_EN.md`

Keycloak dev realm sync:
- `Platform/Identity/Keycloak/Sync-HidBridgeDevRealm.ps1`
- скрипт робить backup і приводить `hidbridge-dev` до актуального dev baseline без ручного проклікування.


Protected non-web API smoke:
- `Platform/run_api_bearer_smoke.ps1`
- if `-AccessToken "<token>"`, `-AccessToken "auto"` or empty token mode is used, the script acquires dev JWTs from Keycloak automatically
- if `-AccessToken` contains a real JWT, the script uses that token as-is
- auto mode uses Keycloak dev client:
  - `controlplane-smoke`
- auto mode uses Keycloak dev users:
  - `operator.smoke.admin`
  - `operator.smoke.viewer`
  - `operator.smoke.foreign`
- smoke scripts now write detailed logs to files and print only:
  - summary
  - data root
  - log/result file paths
- `Platform/run_token_debug.ps1` acquires or inspects a JWT and writes:
  - access token header/payload
  - raw token response
  - optional `id_token` header/payload
  - `userinfo` response
  under `Platform/.logs/token-debug/`

Additional API auth settings:
- `HIDBRIDGE_AUTH_ALLOW_HEADER_FALLBACK`
- set to `false` for bearer-first validation of non-web clients

Policy governance diagnostics:
- `GET /api/v1/diagnostics/policies/summary`
- `POST /api/v1/diagnostics/policies/prune`
- `GET /api/v1/diagnostics/policies/scopes`
- `GET /api/v1/diagnostics/policies/assignments`
- `POST /api/v1/diagnostics/policies/scopes`
- `POST /api/v1/diagnostics/policies/assignments`
- `POST /api/v1/diagnostics/policies/assignments/{assignmentId}/activate`
- `POST /api/v1/diagnostics/policies/assignments/{assignmentId}/deactivate`
- `DELETE /api/v1/diagnostics/policies/assignments/{assignmentId}`


Останні доповнення:
- у `HidBridge.ControlPlane.Web` додано окремий екран `Policy Governance`;
- додано фільтри й JSON export для policy revisions через web endpoint `/exports/policies/revisions`;
- додано edit flows для policy scopes та policy assignments через `Policy Governance` screen і API management endpoints;
- `Policy Governance` UI тепер підтримує load-into-form / clear-form UX для scopes та assignments;
- додано deactivate/delete semantics для policy assignments через `Policy Governance` screen та diagnostics API endpoints;
- додано activate/deactivate semantics для policy scopes через `Policy Governance` screen та diagnostics API endpoints;
- додано safe delete semantics для policy scopes: scope можна видалити лише після видалення всіх прив’язаних assignments;
- UI resilience pass для `HidBridge.ControlPlane.Web` виконано:
  `Fleet Overview`, `Session Details`, `Audit`, `Telemetry`, `Policy Governance`, `Ops Status`
  повинні показувати `Backend unavailable`, коли `ControlPlane.Api` недоступний;
- `Ops Status` тепер показує останні operational artifacts і дає прямі посилання на `doctor`, `checks`, `full`, `token-debug`, `identity-reset` і `bearer-rollout` логи;
- `ControlPlane.Api` підтримує gradual bearer-only rollout через `HIDBRIDGE_AUTH_BEARER_ONLY_PREFIXES`;
- за замовчуванням bearer-only baseline тепер застосовано до:
  - `/api/v1/dashboards`
  - `/api/v1/projections`
  - `/api/v1/diagnostics`
  - `/api/v1/sessions`
- `ControlPlane.Api` додатково підтримує `HIDBRIDGE_AUTH_CALLER_CONTEXT_REQUIRED_PREFIXES` для unsafe mutation paths;
- за замовчуванням caller-context-required baseline тепер застосовано до:
  - `/api/v1/sessions`
- `ControlPlane.Api` також підтримує `HIDBRIDGE_AUTH_HEADER_FALLBACK_DISABLED_PATTERNS` для path-level staged fallback removal (`*` = один path segment wildcard);
- для `Keycloak` dev realm додано окремий scope `hidbridge-caller-context-v2`, який проєктує `preferred_username`, `email`, `tenant_id`, `org_id`, `principal_id` і `role` у bearer token для `controlplane-web` та `controlplane-smoke`;
- `/api/v1/sessions` тепер переведено в strict bearer-only baseline; caller-context mutation rules лишаються окремо видимими через `HIDBRIDGE_AUTH_CALLER_CONTEXT_REQUIRED_PREFIXES`.
- `HIDBRIDGE_AUTH_HEADER_FALLBACK_DISABLED_PATTERNS` тепер за замовчуванням відповідає verified `Phase 4` state:
  - `control`
  - `invitations`
  - `shares / participants`
  - `commands`
- smoke/direct-grant token scripts тепер запитують `openid profile email`, щоб bearer-first контур отримував richer OIDC claims без `invalid_scope` на dev Keycloak baseline.
- `run_doctor.ps1` тепер показує окремий сигнал `smoke-claims`, щоб було видно, чи bearer token вже містить достатній caller context.
- safe staged profile для `HIDBRIDGE_AUTH_HEADER_FALLBACK_DISABLED_PATTERNS` описано в:
  - `Docs/SystemArchitecture/HidBridge_Bearer_Rollout_Profile_UA.md`
- для контрольованого reset/reimport dev realm додано:
  - `powershell -ExecutionPolicy Bypass -File Platform/run.ps1 -Task identity-reset`
- для idempotent onboarding нового OIDC оператора додано:
  - `& .\Platform\run.ps1 -Task identity-onboard -ForwardArgs @('-Email','<user@email>')`
- готовий runtime preset для першої safe rollout фази:
  - `Platform/Profiles/BearerRollout/Phase1-Control.ps1`
  - `Platform/Profiles/BearerRollout/Phase1-Control.env.example`

Активація `Phase 1`:
```powershell
powershell -ExecutionPolicy Bypass -File Platform/Profiles/BearerRollout/Phase1-Control.ps1
```

Тільки друк значення без застосування:
```powershell
powershell -ExecutionPolicy Bypass -File Platform/Profiles/BearerRollout/Phase1-Control.ps1 -PrintOnly
```

Активація `Phase 2`:
```powershell
powershell -ExecutionPolicy Bypass -File Platform/Profiles/BearerRollout/Phase2-Invitations.ps1
```

Тільки друк значення без застосування:
```powershell
powershell -ExecutionPolicy Bypass -File Platform/Profiles/BearerRollout/Phase2-Invitations.ps1 -PrintOnly
```

Активація `Phase 3`:
```powershell
powershell -ExecutionPolicy Bypass -File Platform/Profiles/BearerRollout/Phase3-SharesParticipants.ps1
```

Тільки друк значення без застосування:
```powershell
powershell -ExecutionPolicy Bypass -File Platform/Profiles/BearerRollout/Phase3-SharesParticipants.ps1 -PrintOnly
```

Активація `Phase 4`:
```powershell
powershell -ExecutionPolicy Bypass -File Platform/Profiles/BearerRollout/Phase4-Commands.ps1
```

Тільки друк значення без застосування:
```powershell
powershell -ExecutionPolicy Bypass -File Platform/Profiles/BearerRollout/Phase4-Commands.ps1 -PrintOnly
```

Поточний verified state:
- після `identity-reset` і нормалізації claims strict rollback runner підтверджує `Phase 4`
- `Phase 4` (`commands`) стала поточним effective rollout state
- для вирівнювання dev identity contour використовуй:
```powershell
powershell -ExecutionPolicy Bypass -File Platform/run.ps1 -Task identity-reset
```


**Script Layout**
- `Platform/Scripts/` contains the real operational scripts.
- `Platform/run.ps1` is the unified launcher:
  - `powershell -ExecutionPolicy Bypass -File Platform/run.ps1 -Task checks`
  - `powershell -ExecutionPolicy Bypass -File Platform/run.ps1 -Task smoke-sql -- -ConnectionString "Host=127.0.0.1;Port=5434;Database=hidbridge;Username=hidbridge;Password=hidbridge"`
  - `powershell -ExecutionPolicy Bypass -File Platform/run.ps1 -Task smoke-bearer`
  - `powershell -ExecutionPolicy Bypass -File Platform/run.ps1 -Task identity-reset`
  - `powershell -ExecutionPolicy Bypass -File Platform/run.ps1 -Task bearer-rollout -Phase 4`
- top-level `Platform/run_*.ps1` files are compatibility wrappers that forward to `Platform/Scripts/`.


**Operational Helpers**
- `Platform/run_doctor.ps1` checks local prerequisites: `.NET`, Keycloak port, PostgreSQL port, API port, realm artifacts, and smoke wrapper presence.
- `Ops Status` in `HidBridge.ControlPlane.Web` now surfaces latest local operational artifacts via `/ops-artifacts/file?...`.
- `Platform/run_clean_logs.ps1` prunes old `Platform/.logs` and, optionally, `Platform/.smoke-data`.
- `Platform/Scripts/Common/ScriptCommon.ps1` contains shared logging and operational helper functions used by the script suite.
- unified launcher examples:
  - `powershell -ExecutionPolicy Bypass -File Platform/run.ps1 -Task doctor`
  - `powershell -ExecutionPolicy Bypass -File Platform/run.ps1 -Task clean-logs -- -KeepDays 5 -IncludeSmokeData`

- `Platform/run_doctor.ps1` now supports:
  - `-RequireApi` to fail if API is not running
  - `-StartApiProbe` to start `ControlPlane.Api`, probe `/health`, and stop it
  - live `Keycloak` checks for realm, `controlplane-smoke`, and smoke users

- `Platform/run_ci_local.ps1` runs a practical local CI sequence:
  - `doctor -StartApiProbe -RequireApi`
  - `run_checks.ps1 -Provider Sql`
  - `run_api_bearer_smoke.ps1`
  - automatically exports artifacts on failure into `Platform/Artifacts/ci-local-*`
- `Platform/run_export_artifacts.ps1` exports `.logs` and, optionally, `.smoke-data` and `Keycloak` backups into one artifact folder.
- additional unified launcher examples:
  - `powershell -ExecutionPolicy Bypass -File Platform/run.ps1 -Task ci-local`
  - `powershell -ExecutionPolicy Bypass -File Platform/run.ps1 -Task export-artifacts -IncludeSmokeData -IncludeBackups`
  - `powershell -ExecutionPolicy Bypass -File Platform/run.ps1 -Task token-debug`
Unified full run:
```powershell
powershell -ExecutionPolicy Bypass -File Platform/run.ps1 -Task full
```

Direct wrapper:
```powershell
powershell -ExecutionPolicy Bypass -File Platform/run_full.ps1
```

What it does:
- realm sync with backup
- local CI (`doctor`, `checks`, `bearer smoke`)
- artifact export on failure

Recommended operator/dev workflow:
```powershell
powershell -ExecutionPolicy Bypass -File Platform/run.ps1 -Task full
```

Fast token inspection:
```powershell
powershell -ExecutionPolicy Bypass -File Platform/run.ps1 -Task token-debug
```
Bearer rollout:
```powershell
powershell -ExecutionPolicy Bypass -File Platform/run.ps1 -Task bearer-rollout -Phase 4
```

If the requested phase is not yet bearer-safe, the runner automatically rolls back through lower phases down to `Phase 0` (current baseline) and reports the effective state that stayed green.

Print the exact PowerShell env assignment without applying it:
```powershell
powershell -ExecutionPolicy Bypass -File Platform/run.ps1 -Task bearer-rollout -Phase 4 -PrintOnly
```

Apply only in the current shell:
```powershell
. .\Platform\Scripts\run_bearer_rollout_phase.ps1 -Phase 4 -ApplyOnly
```

Disable auto-rollback and fail hard:
```powershell
powershell -ExecutionPolicy Bypass -File Platform/run.ps1 -Task bearer-rollout -Phase 4 -NoAutoRollback
```
