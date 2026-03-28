# HidBridge.EdgeProxy.Agent

Initial service-based replacement for the temporary PowerShell WebRTC peer adapter.

Runtime layering:

- `HidBridge.Edge.Abstractions` - edge execution contracts.
- `HidBridge.Edge.HidBridgeProtocol` - protocol adapters (`ControlWs` + `UART HID`).
- `HidBridge.EdgeProxy.Agent` - orchestration loop (API relay + heartbeat + peer lifecycle).

## Purpose

This worker:

1. registers one WebRTC relay peer online for a room session;
2. polls relay command envelopes from ControlPlane API;
3. forwards commands through configured executor (`ControlWs` or direct `UART HID`);
4. publishes ACK back to API;
5. emits periodic heartbeat signal packets.
6. publishes typed media-readiness metadata as part of peer online snapshots.

Lifecycle states emitted in peer metadata:

- `Starting`
- `Connecting`
- `Connected`
- `Degraded`
- `Reconnecting`
- `Offline`

ACK compatibility notes:
- the worker accepts peer ACK fields `ok`, `success`, or `status` (`ok|success|applied|accepted|done` treated as success);
- the websocket client closes with normal close handshake after ACK to reduce noisy remote `close handshake` errors.

## Environment variables

All settings are read with prefix `HIDBRIDGE_EDGE_PROXY_`:

- `BASEURL` (default: `http://127.0.0.1:18093`)
- `SESSIONID` (required)
- `PEERID` (required)
- `ENDPOINTID` (required)
- `CONTROLWSURL` (default: `ws://127.0.0.1:28092/ws/control`)
- `COMMANDEXECUTOR` (`uart` default, `controlws` only for legacy exp-022 compatibility)
- `TRANSPORTENGINE` (`relay` default; `dcd` preview first consumes direct signal commands (`WebRtcSignalKind.Command`) and can fallback to relay queue)
- `DCDALLOWRELAYFALLBACK` (`true` default; when `false`, `dcd` mode will not poll relay queue and processes only direct signal commands)
- `MEDIAENGINE` (`ffmpeg-dcd` default; `none` disables agent media runtime)
- `FFMPEGLATENCYPROFILE` (`balanced` default; `ultra`, `extreme`)
- `FFMPEGVIDEODEVICE` (optional DirectShow video device; when set runtime can auto-compose capture arguments without custom template)
- `FFMPEGAUDIODEVICE` (optional DirectShow audio device)
- `FFMPEGRESOLUTION` (default: `1280x720`)
- `FFMPEGFRAMERATE` (default: `30`)
- `FFMPEGVIDEOBITRATE` (default: `4500k`)
- `FFMPEGAUDIOBITRATE` (default: `128k`)
- `FFMPEGUSETESTSOURCE` (default: `false`; when `true` runtime uses lavfi synthetic source)
- `UARTPORT` (required for `COMMANDEXECUTOR=uart`)
- `UARTBAUD` (default: `3000000`)
- `UARTHMACKEY` (default: `changeme`)
- `UARTMASTERSECRET` (optional, enables per-device derived key)
- `UARTMOUSEINTERFACESELECTOR` (default: `255`)
- `UARTKEYBOARDINTERFACESELECTOR` (default: `254`)
- `UARTCOMMANDTIMEOUTMS` (default: `300`)
- `UARTINJECTTIMEOUTMS` (default: `200`)
- `UARTINJECTRETRIES` (default: `2`)
- `UARTRELEASEPORTAFTEREXECUTE` (default: `false`)
- `MEDIAHEALTHURL` (optional)
  - for `COMMANDEXECUTOR=controlws`: defaults to `http(s)://<control-ws-host>/health` when not set;
  - for `COMMANDEXECUTOR=uart`: not auto-derived from `CONTROLWSURL` (set explicitly only when real capture probe exists).
- `MEDIAHEALTHTIMEOUTSEC` (default: `3`)
- `MEDIASTREAMID` (default: `edge-main`)
- `MEDIASOURCE` (default: `edge-capture`)
- `MEDIAPLAYBACKURL` (optional absolute URL exposed by transport diagnostics/UI as live preview link)
- `MEDIABACKENDAUTOSTART` (default: `false`; when `true`, agent starts local media backend process itself before ffmpeg publish)
- `MEDIABACKENDEXECUTABLEPATH` (required when `MEDIABACKENDAUTOSTART=true`; path or command name of local backend binary)
- `MEDIABACKENDARGUMENTSTEMPLATE` (optional; supports placeholders `{sessionId}`, `{peerId}`, `{endpointId}`, `{streamId}`, `{source}`, `{baseUrl}`, `{whipUrl}`, `{whepUrl}`)
- `MEDIABACKENDWORKINGDIRECTORY` (optional)
- `MEDIABACKENDSTARTUPTIMEOUTSEC` (default: `20`)
- `MEDIABACKENDPROBEDELAYMS` (default: `500`)
- `MEDIABACKENDPROBETIMEOUTMS` (default: `1200`)
- `MEDIABACKENDSTOPTIMEOUTMS` (default: `3000`)
- `REQUIREMEDIAREADY` (default: `true`, used by server-side readiness policy)
- `ASSUMEMEDIAREADYWITHOUTPROBE` (default: `false`)
- `PRINCIPALID` (default: `smoke-runner`)
- `TENANTID` / `ORGANIZATIONID`
- `OPERATORROLESCSV` (default: `operator.edge`, least-privilege caller role for relay agent paths)
- `ACCESSTOKEN` (optional)
- `KEYCLOAKBASEURL` / `KEYCLOAKREALM`
- `TOKENCLIENTID` / `TOKENCLIENTSECRET` / `TOKENUSERNAME` / `TOKENPASSWORD`
- `TOKENREFRESHTOKEN` (optional bootstrap refresh token)
- `TOKENSCOPE` (optional OIDC scope for password/refresh grants)
- `TOKENREFRESHSKEWSEC` (default: `60`, proactive refresh window before token expiry)
- `ALLOWPASSWORDGRANTFALLBACK` (default: `true`; set `false` for refresh-token-only hardening)
- `POLLINTERVALMS`
- `BATCHLIMIT`
- `HEARTBEATINTERVALSEC`
- `COMMANDTIMEOUTMS`
- `RECONNECTBACKOFFMINMS` (default: `500`)
- `RECONNECTBACKOFFMAXMS` (default: `5000`)
- `RECONNECTBACKOFFJITTERMS` (default: `250`)
- `TRANSIENTFAILURETHRESHOLDFOROFFLINE` (default: `2`)

## Local run

```bash
dotnet run --project Platform/Edge/HidBridge.EdgeProxy.Agent/HidBridge.EdgeProxy.Agent.csproj
```

## Token lifecycle

- if `ACCESSTOKEN` is absent, the agent acquires a password-grant token;
- if a refresh token exists, the agent refreshes proactively (`TOKENREFRESHSKEWSEC`) and on HTTP `401`;
- if refresh grant fails, the agent falls back to password grant only when `ALLOWPASSWORDGRANTFALLBACK=true`;
- default caller role header is `operator.edge` (override via `OPERATORROLESCSV` only when stricter API scope requires it).

## UART + media readiness notes

- If API readiness policy requires media (`HIDBRIDGE_WEBRTC_REQUIRE_MEDIA_READY=true`) and you run UART-only control path without capture probe, set:
  - `HIDBRIDGE_EDGE_PROXY_ASSUMEMEDIAREADYWITHOUTPROBE=true`
- In this mode the agent reports media state as `Ready` (not `NoProbeConfigured`) so server-side readiness can pass for control-only acceptance scenarios.

## Agent-managed media backend

For host installs without Docker/RuntimeCtl orchestration, enable local backend bootstrap directly in the agent:

- set `HIDBRIDGE_EDGE_PROXY_MEDIABACKENDAUTOSTART=true`
- provide `HIDBRIDGE_EDGE_PROXY_MEDIABACKENDEXECUTABLEPATH` (for example local `srs.exe` wrapper/service launcher)
- set `HIDBRIDGE_EDGE_PROXY_MEDIAWHIPURL` and `HIDBRIDGE_EDGE_PROXY_MEDIAWHEPURL` to backend endpoints

With this mode the agent itself owns backend process lifecycle and waits for endpoint reachability before starting ffmpeg publish.
