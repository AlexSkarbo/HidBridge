# MediaLocal Profile (exp-022 style backend, no MediaMTX)

This profile uses your existing media backend.
RuntimeCtl only passes media endpoints to edge-agent and UI.

## 1) Apply environment for edge-agent launched by RuntimeCtl

```powershell
$env:HIDBRIDGE_EDGE_PROXY_MEDIAENGINE = "ffmpeg-dcd"
$env:HIDBRIDGE_EDGE_PROXY_FFMPEGEXECUTABLEPATH = "ffmpeg"
$env:HIDBRIDGE_EDGE_PROXY_MEDIAHEALTHURL = "http://127.0.0.1:28092/health"
$env:HIDBRIDGE_EDGE_PROXY_MEDIAWHIPURL = "http://127.0.0.1:19851/rtc/v1/whip/?app=live&stream=cam21"
$env:HIDBRIDGE_EDGE_PROXY_MEDIAWHEPURL = "http://127.0.0.1:19851/rtc/v1/whep/?app=live&stream=cam21"
$env:HIDBRIDGE_EDGE_PROXY_MEDIAPLAYBACKURL = "http://127.0.0.1:19851/rtc/v1/whep/?app=live&stream=cam21"
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

## 3) Validate in UI

In session page:

- `Media state = Ready`
- `Media playback` points to your playback URL
- click `Start stream`
- `Connection state` becomes `connected`
- `Tracks > 0`

If playback fails, check:

```powershell
Get-Content Platform/.logs/webrtc-stack/latest.log -Tail 200
Get-Content Platform/.logs/webrtc-edge-agent-acceptance/latest.log -Tail 200
Invoke-WebRequest "http://127.0.0.1:19851/rtc/v1/whep/?app=live&stream=cam21" -Method Options -UseBasicParsing
```
