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
- `COMMANDEXECUTOR` (`controlws` default, `uart` for direct HID bridge mode)
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
- `MEDIAHEALTHURL` (optional, defaults to `http(s)://<control-ws-host>/health` when `CONTROLWSURL` is set)
- `MEDIAHEALTHTIMEOUTSEC` (default: `3`)
- `MEDIASTREAMID` (default: `edge-main`)
- `MEDIASOURCE` (default: `edge-capture`)
- `REQUIREMEDIAREADY` (default: `false`, used by server-side readiness policy when enabled)
- `ASSUMEMEDIAREADYWITHOUTPROBE` (default: `false`)
- `PRINCIPALID` (default: `smoke-runner`)
- `TENANTID` / `ORGANIZATIONID`
- `ACCESSTOKEN` (optional)
- `KEYCLOAKBASEURL` / `KEYCLOAKREALM`
- `TOKENCLIENTID` / `TOKENUSERNAME` / `TOKENPASSWORD`
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
