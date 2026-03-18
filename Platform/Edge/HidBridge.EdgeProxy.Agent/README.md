# HidBridge.EdgeProxy.Agent

Initial service-based replacement for the temporary PowerShell WebRTC peer adapter.

## Purpose

This worker:

1. registers one WebRTC relay peer online for a room session;
2. polls relay command envelopes from ControlPlane API;
3. forwards commands to local control websocket endpoint (exp-022 compatible);
4. publishes ACK back to API;
5. emits periodic heartbeat signal packets.

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
