param(
    [string]$WsUrl = "ws://127.0.0.1:8080/ws/hid",
    [string]$Token = "",
    [int]$TimeoutSec = 5,
    [byte]$MouseItf = 0,
    [byte]$KeyboardItf = 2,
    [bool]$AutoItf = $true,
    [string]$BaseUrl = "",
    [int]$StressCount = 0,
    [int]$StressDelayMs = 250
)

$ErrorActionPreference = "Stop"
$script:StopAfterTimeout = $false

function Build-Url {
    if ([string]::IsNullOrWhiteSpace($Token)) { return $WsUrl }
    if ($WsUrl.Contains("?")) { return "$WsUrl&access_token=$Token" }
    return "$WsUrl?access_token=$Token"
}

function Ws-To-Http([string]$ws) {
    if ($ws.StartsWith("wss://")) { return "https://" + $ws.Substring(6) }
    if ($ws.StartsWith("ws://")) { return "http://" + $ws.Substring(5) }
    return $ws
}

function Invoke-Api([string]$Path) {
    $headers = @{}
    if ($Token -and $Token.Trim().Length -gt 0) { $headers["X-HID-Token"] = $Token }
    $base = $BaseUrl
    if ([string]::IsNullOrWhiteSpace($base)) { $base = Ws-To-Http $WsUrl }
    $base = $base.TrimEnd("/") -replace "/ws/hid$", ""
    $uri = $base + $Path
    return Invoke-RestMethod -Method GET -Uri $uri -Headers $headers
}

function Get-StatsDelta {
    param([int]$prev)
    try {
        $stats = Invoke-Api "/stats"
        if (-not $stats -or -not $stats.uart) { return @{ cur = $prev; delta = 0 } }
        $cur = [int]$stats.uart.reportsSent
        return @{ cur = $cur; delta = ($cur - $prev) }
    } catch {
        return @{ cur = $prev; delta = 0 }
    }
}

function Resolve-Itf {
    if (-not $AutoItf) { return }
    try {
        $devs = Invoke-Api "/devices?allowStale=true"
        if (-not $devs -or -not $devs.ok -or -not $devs.list) { return }
        $itfs = $devs.list.interfaces
        $mouse = $itfs | Where-Object { $_.typeName -eq "mouse" } | Select-Object -First 1
        $kbd = $itfs | Where-Object { $_.typeName -eq "keyboard" } | Select-Object -First 1
        if ($mouse -and $mouse.itf -ne $null) { $script:MouseItf = [byte]$mouse.itf }
        if ($kbd -and $kbd.itf -ne $null) { $script:KeyboardItf = [byte]$kbd.itf }
        Write-Host ("auto itf: mouse={0} keyboard={1}" -f $script:MouseItf, $script:KeyboardItf)
    } catch {
        Write-Host "auto itf: failed"
    }
}

function Send-Json([System.Net.WebSockets.ClientWebSocket]$ws, [object]$obj) {
    if ($ws.State -ne [System.Net.WebSockets.WebSocketState]::Open -and
        $ws.State -ne [System.Net.WebSockets.WebSocketState]::CloseReceived) {
        return $false
    }
    $json = ($obj | ConvertTo-Json -Depth 5 -Compress)
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
    $seg = New-Object System.ArraySegment[byte] -ArgumentList (, $bytes)
    try {
        $ws.SendAsync($seg, [System.Net.WebSockets.WebSocketMessageType]::Text, $true, [System.Threading.CancellationToken]::None).GetAwaiter().GetResult() | Out-Null
        return $json
    } catch {
        return $false
    }
}

function Recv-Json([System.Net.WebSockets.ClientWebSocket]$ws, [int]$timeoutMs) {
    if ($ws.State -ne [System.Net.WebSockets.WebSocketState]::Open -and
        $ws.State -ne [System.Net.WebSockets.WebSocketState]::CloseReceived) {
        return "(closed)"
    }
    $buf = New-Object byte[] 8192
    $seg = New-Object System.ArraySegment[byte] -ArgumentList (, $buf)
    $recvTask = $ws.ReceiveAsync($seg, [System.Threading.CancellationToken]::None)
    $done = [System.Threading.Tasks.Task]::WhenAny($recvTask, [System.Threading.Tasks.Task]::Delay($timeoutMs)).GetAwaiter().GetResult()
    if ($done -ne $recvTask) {
        $script:StopAfterTimeout = $true
        return "(timeout)"
    }
    try {
        $res = $recvTask.GetAwaiter().GetResult()
        if ($res.Count -le 0) { return $null }
        return [System.Text.Encoding]::UTF8.GetString($buf, 0, $res.Count)
    } catch {
        return "(recv_error)"
    }
}

Write-Host "=== HidControlServer WS HID test ==="
Write-Host "WsUrl: $WsUrl"

Resolve-Itf

$uri = Build-Url
$ws = New-Object System.Net.WebSockets.ClientWebSocket
$ws.Options.KeepAliveInterval = [TimeSpan]::FromSeconds(10)
$ws.ConnectAsync($uri, [System.Threading.CancellationToken]::None).GetAwaiter().GetResult()
Write-Host "connected"
$stats0 = Get-StatsDelta 0

$id = 1
function Next-Id { $script:id += 1; return "$script:id" }

if (Send-Json $ws @{ id = "$id"; type = "ping" }) {
    $pong = Recv-Json $ws ($TimeoutSec * 1000)
    Write-Host "recv: $pong"
}

if (-not $StopAfterTimeout -and (Send-Json $ws @{ id = (Next-Id); type = "mouse.move"; dx = 200; dy = 0; itfSel = $MouseItf })) {
    Write-Host "recv: $(Recv-Json $ws ($TimeoutSec * 1000))"
}
if (-not $StopAfterTimeout -and (Send-Json $ws @{ id = (Next-Id); type = "mouse.move"; dx = 0; dy = 200; itfSel = $MouseItf })) {
    Write-Host "recv: $(Recv-Json $ws ($TimeoutSec * 1000))"
}
if (-not $StopAfterTimeout -and (Send-Json $ws @{ id = (Next-Id); type = "mouse.move"; dx = -200; dy = 0; itfSel = $MouseItf })) {
    Write-Host "recv: $(Recv-Json $ws ($TimeoutSec * 1000))"
}
if (-not $StopAfterTimeout -and (Send-Json $ws @{ id = (Next-Id); type = "mouse.move"; dx = 0; dy = -200; itfSel = $MouseItf })) {
    Write-Host "recv: $(Recv-Json $ws ($TimeoutSec * 1000))"
}

if (-not $StopAfterTimeout -and (Send-Json $ws @{ id = (Next-Id); type = "mouse.wheel"; delta = 1; itfSel = $MouseItf })) {
    Write-Host "recv: $(Recv-Json $ws ($TimeoutSec * 1000))"
}

if (-not $StopAfterTimeout -and (Send-Json $ws @{ id = (Next-Id); type = "mouse.button"; button = "left"; down = $true; itfSel = $MouseItf })) {
    Write-Host "recv: $(Recv-Json $ws ($TimeoutSec * 1000))"
}
if (-not $StopAfterTimeout -and (Send-Json $ws @{ id = (Next-Id); type = "mouse.button"; button = "left"; down = $false; itfSel = $MouseItf })) {
    Write-Host "recv: $(Recv-Json $ws ($TimeoutSec * 1000))"
}

if (-not $StopAfterTimeout -and (Send-Json $ws @{ id = (Next-Id); type = "keyboard.press"; usage = 4; itfSel = $KeyboardItf })) {
    Write-Host "recv: $(Recv-Json $ws ($TimeoutSec * 1000))"
}

if (-not $StopAfterTimeout -and (Send-Json $ws @{ id = (Next-Id); type = "keyboard.text"; text = "WS_TEST_123 "; itfSel = $KeyboardItf })) {
    Write-Host "recv: $(Recv-Json $ws ($TimeoutSec * 1000))"
}
$stats1 = Get-StatsDelta $stats0.cur
Write-Host ("uart reports delta: +{0}" -f $stats1.delta)

if ($StressCount -gt 0) {
    Write-Host ("stress: count={0} delayMs={1}" -f $StressCount, $StressDelayMs)
    for ($i = 0; $i -lt $StressCount -and -not $StopAfterTimeout; $i += 1) {
        if (Send-Json $ws @{ id = (Next-Id); type = "mouse.move"; dx = 200; dy = 0; itfSel = $MouseItf }) {
            Write-Host "recv: $(Recv-Json $ws ($TimeoutSec * 1000))"
        } else { break }
        if (Send-Json $ws @{ id = (Next-Id); type = "mouse.move"; dx = 0; dy = 200; itfSel = $MouseItf }) {
            Write-Host "recv: $(Recv-Json $ws ($TimeoutSec * 1000))"
        } else { break }
        if (Send-Json $ws @{ id = (Next-Id); type = "mouse.move"; dx = -200; dy = 0; itfSel = $MouseItf }) {
            Write-Host "recv: $(Recv-Json $ws ($TimeoutSec * 1000))"
        } else { break }
        if (Send-Json $ws @{ id = (Next-Id); type = "mouse.move"; dx = 0; dy = -200; itfSel = $MouseItf }) {
            Write-Host "recv: $(Recv-Json $ws ($TimeoutSec * 1000))"
        } else { break }
        if (Send-Json $ws @{ id = (Next-Id); type = "keyboard.text"; text = "WS_STRESS_OK "; itfSel = $KeyboardItf }) {
            Write-Host "recv: $(Recv-Json $ws ($TimeoutSec * 1000))"
        } else { break }
        $stats1 = Get-StatsDelta $stats1.cur
        Write-Host ("uart reports delta: +{0}" -f $stats1.delta)
        Start-Sleep -Milliseconds $StressDelayMs
    }
}

if ($ws.State -eq [System.Net.WebSockets.WebSocketState]::Open -or $ws.State -eq [System.Net.WebSockets.WebSocketState]::CloseReceived) {
    $ws.CloseAsync([System.Net.WebSockets.WebSocketCloseStatus]::NormalClosure, "done", [System.Threading.CancellationToken]::None).GetAwaiter().GetResult() | Out-Null
}
Write-Host "done"
