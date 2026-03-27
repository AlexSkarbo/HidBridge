# MediaLocal Profile (agent-managed backend, no Docker dependency)

This profile lets `HidBridge.EdgeProxy.Agent` auto-start a local media backend process and
publish explicit backend runtime health (`mediaBackend*`) into transport diagnostics.

## 1) Apply environment for edge-agent launched by RuntimeCtl

```powershell
$env:HIDBRIDGE_EDGE_PROXY_MEDIAENGINE = "ffmpeg-dcd"
$env:HIDBRIDGE_EDGE_PROXY_FFMPEGEXECUTABLEPATH = "ffmpeg"
$env:HIDBRIDGE_EDGE_PROXY_MEDIAHEALTHURL = "http://127.0.0.1:28092/health"
$env:HIDBRIDGE_EDGE_PROXY_MEDIAWHIPURL = "http://127.0.0.1:19851/rtc/v1/whip/?app=live&stream=cam21"
$env:HIDBRIDGE_EDGE_PROXY_MEDIAWHEPURL = "http://127.0.0.1:19851/rtc/v1/whep/?app=live&stream=cam21"
$env:HIDBRIDGE_EDGE_PROXY_MEDIAPLAYBACKURL = "http://127.0.0.1:19851/rtc/v1/whep/?app=live&stream=cam21"
$env:HIDBRIDGE_EDGE_PROXY_MEDIABACKENDAUTOSTART = "true"
$env:HIDBRIDGE_EDGE_PROXY_MEDIABACKENDEXECUTABLEPATH = "C:\\media\\backend\\backend.exe"
$env:HIDBRIDGE_EDGE_PROXY_MEDIABACKENDARGUMENTSTEMPLATE = "--session {sessionId} --stream {streamId} --whip {whipUrl} --whep {whepUrl}"
$env:HIDBRIDGE_EDGE_PROXY_MEDIABACKENDWORKINGDIRECTORY = "C:\\media\\backend"
$env:HIDBRIDGE_EDGE_PROXY_MEDIABACKENDSTARTUPTIMEOUTSEC = "30"
$env:HIDBRIDGE_EDGE_PROXY_ASSUMEMEDIAREADYWITHOUTPROBE = "false"
$env:HIDBRIDGE_EDGE_PROXY_REQUIREMEDIAREADY = "true"
```

Optional WHIP bearer:
`$env:HIDBRIDGE_EDGE_PROXY_MEDIAWHIPBEARERTOKEN="<token>"`

## 2) Run stack and acceptance

```powershell
dotnet run --project Platform/Tools/HidBridge.RuntimeCtl/HidBridge.RuntimeCtl.csproj -- --platform-root Platform webrtc-stack -StopExisting -CommandExecutor uart -UartPort COM6 -UartHmacKey your-master-secret

dotnet run --project Platform/Tools/HidBridge.RuntimeCtl/HidBridge.RuntimeCtl.csproj -- --platform-root Platform webrtc-acceptance -CommandExecutor uart -SkipRuntimeBootstrap -StopExisting -PeerReadyTimeoutSec 45 -MaxDurationSec 420 -RequireMediaReady -RequireMediaPlaybackUrl
```

You should see in stack summary:

- `Media WHEP: http://127.0.0.1:19851/rtc/v1/whep/?app=live&stream=cam21`
- `Media WHIP: http://127.0.0.1:19851/rtc/v1/whip/?app=live&stream=cam21`
- `Media playback: http://127.0.0.1:19851/rtc/v1/whep/?app=live&stream=cam21`

## 3) Validate runtime health in UI

In session page:

- `Media state = Ready`
- `Media playback` points to your playback URL
- `Media backend autostart = Yes`
- `Media backend running = Yes`
- `Media backend state = running`
- click `Start stream`
- `Connection state` becomes `connected`
- `Tracks > 0`

If playback fails, check:

```powershell
Get-Content Platform/.logs/webrtc-stack/latest.log -Tail 200
Get-Content Platform/.logs/webrtc-edge-agent-acceptance/latest.log -Tail 200
Invoke-WebRequest "http://127.0.0.1:19851/rtc/v1/whep/?app=live&stream=cam21" -Method Options -UseBasicParsing
```
