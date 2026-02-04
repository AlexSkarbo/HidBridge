param(
    [string]$BaseUrl = "http://127.0.0.1:8080",
    [string]$WsUrl = "ws://127.0.0.1:8080/ws/hid",
    [string]$Token = "",
    [int]$TimeoutSec = 5,
    [byte]$MouseItf = 0,
    [byte]$KeyboardItf = 2,
    [bool]$IncludeReportDesc = $false,
    [bool]$AutoItf = $true,
    [int]$StressCount = 0,
    [int]$StressDelayMs = 250
)

$ErrorActionPreference = "Stop"

function Invoke-Api {
    param(
        [string]$Method,
        [string]$Path,
        $Body = $null
    )
    $headers = @{}
    if ($Token -and $Token.Trim().Length -gt 0) {
        $headers["X-HID-Token"] = $Token
    }
    $uri = $BaseUrl.TrimEnd("/") + $Path
    if ($null -ne $Body) {
        $json = $Body | ConvertTo-Json -Depth 6
        return Invoke-RestMethod -Method $Method -Uri $uri -Headers $headers -ContentType "application/json" -Body $json
    }
    return Invoke-RestMethod -Method $Method -Uri $uri -Headers $headers
}

function Get-StatsDelta {
    param([int]$prev)
    try {
        $stats = Invoke-Api -Method GET -Path "/stats"
        if (-not $stats -or -not $stats.uart) { return @{ cur = $prev; delta = 0 } }
        $cur = [int]$stats.uart.reportsSent
        return @{ cur = $cur; delta = ($cur - $prev) }
    } catch {
        return @{ cur = $prev; delta = 0 }
    }
}
function Build-WsUrl {
    if ([string]::IsNullOrWhiteSpace($Token)) { return $WsUrl }
    if ($WsUrl.Contains("?")) { return "$WsUrl&access_token=$Token" }
    return "$WsUrl?access_token=$Token"
}

function Send-Json([System.Net.WebSockets.ClientWebSocket]$ws, [object]$obj) {
    $json = ($obj | ConvertTo-Json -Depth 5 -Compress)
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
    $seg = New-Object System.ArraySegment[byte] -ArgumentList (, $bytes)
    $ws.SendAsync($seg, [System.Net.WebSockets.WebSocketMessageType]::Text, $true, [System.Threading.CancellationToken]::None).GetAwaiter().GetResult() | Out-Null
    return $json
}

function Recv-Json([System.Net.WebSockets.ClientWebSocket]$ws, [int]$timeoutMs) {
    $buf = New-Object byte[] 8192
    $seg = New-Object System.ArraySegment[byte] -ArgumentList (, $buf)
    $cts = New-Object System.Threading.CancellationTokenSource $timeoutMs
    try {
        $res = $ws.ReceiveAsync($seg, $cts.Token).GetAwaiter().GetResult()
        if ($res.Count -le 0) { return $null }
        return [System.Text.Encoding]::UTF8.GetString($buf, 0, $res.Count)
    } catch [System.Threading.Tasks.TaskCanceledException] {
        return "(timeout)"
    } catch {
        return "(recv_error)"
    }
}

Write-Host "=== HidControlServer UART+WS full test ==="
Write-Host "BaseUrl: $BaseUrl"
Write-Host "WsUrl: $WsUrl"

try {
    Invoke-Api -Method GET -Path "/health" | Out-Null
    Write-Host "health: ok"
} catch {
    Write-Host "health: failed"
}

try {
    $cfg = Invoke-Api -Method GET -Path "/config"
    Write-Host ("config: serial={0} baud={1} url={2}" -f $cfg.serialPort, $cfg.baud, $cfg.url)
} catch {
    Write-Host "config: failed"
}

try {
    $stats = Invoke-Api -Method GET -Path "/stats"
    $uart = $stats.uart
    Write-Host ("uart: framesTx={0} framesRx={1} errors={2} hmacMode={3}" -f $uart.framesSent, $uart.framesReceived, $uart.errors, $stats.hmacMode)
} catch {
    Write-Host "stats: failed"
}

try {
    $ping = Invoke-Api -Method GET -Path "/uart/ping"
    Write-Host ("uart/ping: ok={0} elapsedMs={1} deviceId={2}" -f $ping.ok, $ping.elapsedMs, $ping.deviceId)
} catch {
    Write-Host ("uart/ping: failed ({0})" -f $_.Exception.Message)
}

try {
    $ports = Invoke-Api -Method GET -Path "/serial/ports"
    Write-Host ("serial/ports: {0}" -f $ports.ports.Count)
} catch {
    Write-Host "serial/ports: failed"
}

try {
    $probe = Invoke-Api -Method GET -Path "/serial/probe"
    $ok = ($probe.ports | Where-Object { $_.ok }).Count
    Write-Host ("serial/probe: ok={0} total={1}" -f $ok, $probe.ports.Count)
} catch {
    Write-Host "serial/probe: failed"
}

$desc = if ($IncludeReportDesc) { "true" } else { "false" }
try {
    $devs = Invoke-Api -Method GET -Path ("/devices?includeReportDesc={0}&useBootstrapKey=true&allowStale=true" -f $desc)
    Write-Host ("devices: ok count={0}" -f $devs.list.interfaces.Count)
    if ($AutoItf -and $devs.list -and $devs.list.interfaces) {
        $mouse = $devs.list.interfaces | Where-Object { $_.typeName -eq "mouse" } | Select-Object -First 1
        $kbd = $devs.list.interfaces | Where-Object { $_.typeName -eq "keyboard" } | Select-Object -First 1
        if ($mouse -and $mouse.itf -ne $null) { $script:MouseItf = [byte]$mouse.itf }
        if ($kbd -and $kbd.itf -ne $null) { $script:KeyboardItf = [byte]$kbd.itf }
        Write-Host ("auto itf: mouse={0} keyboard={1}" -f $script:MouseItf, $script:KeyboardItf)
    }
} catch {
    Write-Host ("devices: failed ({0})" -f $_.Exception.Message)
}

try {
    $trace = Invoke-Api -Method GET -Path "/uart/trace?limit=6"
    Write-Host ("uart/trace: {0}" -f $trace.trace.Count)
} catch {
    Write-Host ("uart/trace: failed ({0})" -f $_.Exception.Message)
}

Write-Host ""
Write-Host "=== WS HID test ==="
try {
    $uri = Build-WsUrl
    $ws = New-Object System.Net.WebSockets.ClientWebSocket
    $ws.Options.KeepAliveInterval = [TimeSpan]::FromSeconds(10)
    $ws.ConnectAsync($uri, [System.Threading.CancellationToken]::None).GetAwaiter().GetResult()
    Write-Host "connected"
    $stats0 = Get-StatsDelta 0

    $id = 1
    function Next-Id { $script:id += 1; return "$script:id" }

    Send-Json $ws @{ id = "$id"; type = "ping" } | Out-Null
    Write-Host "recv: $(Recv-Json $ws ($TimeoutSec * 1000))"

    Send-Json $ws @{ id = (Next-Id); type = "mouse.move"; dx = 200; dy = 0; itfSel = $MouseItf } | Out-Null
    Write-Host "recv: $(Recv-Json $ws ($TimeoutSec * 1000))"
    Send-Json $ws @{ id = (Next-Id); type = "mouse.move"; dx = 0; dy = 200; itfSel = $MouseItf } | Out-Null
    Write-Host "recv: $(Recv-Json $ws ($TimeoutSec * 1000))"
    Send-Json $ws @{ id = (Next-Id); type = "mouse.move"; dx = -200; dy = 0; itfSel = $MouseItf } | Out-Null
    Write-Host "recv: $(Recv-Json $ws ($TimeoutSec * 1000))"
    Send-Json $ws @{ id = (Next-Id); type = "mouse.move"; dx = 0; dy = -200; itfSel = $MouseItf } | Out-Null
    Write-Host "recv: $(Recv-Json $ws ($TimeoutSec * 1000))"

    Send-Json $ws @{ id = (Next-Id); type = "keyboard.text"; text = "WS_TEST_123 "; itfSel = $KeyboardItf } | Out-Null
    Write-Host "recv: $(Recv-Json $ws ($TimeoutSec * 1000))"
    $stats1 = Get-StatsDelta $stats0.cur
    Write-Host ("uart reports delta: +{0}" -f $stats1.delta)

    if ($StressCount -gt 0) {
        Write-Host ("stress: count={0} delayMs={1}" -f $StressCount, $StressDelayMs)
        for ($i = 0; $i -lt $StressCount; $i += 1) {
            Send-Json $ws @{ id = (Next-Id); type = "mouse.move"; dx = 200; dy = 0; itfSel = $MouseItf } | Out-Null
            Write-Host "recv: $(Recv-Json $ws ($TimeoutSec * 1000))"
            Send-Json $ws @{ id = (Next-Id); type = "mouse.move"; dx = 0; dy = 200; itfSel = $MouseItf } | Out-Null
            Write-Host "recv: $(Recv-Json $ws ($TimeoutSec * 1000))"
            Send-Json $ws @{ id = (Next-Id); type = "mouse.move"; dx = -200; dy = 0; itfSel = $MouseItf } | Out-Null
            Write-Host "recv: $(Recv-Json $ws ($TimeoutSec * 1000))"
            Send-Json $ws @{ id = (Next-Id); type = "mouse.move"; dx = 0; dy = -200; itfSel = $MouseItf } | Out-Null
            Write-Host "recv: $(Recv-Json $ws ($TimeoutSec * 1000))"
            Send-Json $ws @{ id = (Next-Id); type = "keyboard.text"; text = "WS_STRESS_OK "; itfSel = $KeyboardItf } | Out-Null
            Write-Host "recv: $(Recv-Json $ws ($TimeoutSec * 1000))"
            $stats1 = Get-StatsDelta $stats1.cur
            Write-Host ("uart reports delta: +{0}" -f $stats1.delta)
            Start-Sleep -Milliseconds $StressDelayMs
        }
    }

    $ws.CloseAsync([System.Net.WebSockets.WebSocketCloseStatus]::NormalClosure, "done", [System.Threading.CancellationToken]::None).GetAwaiter().GetResult() | Out-Null
    Write-Host "done"
} catch {
    Write-Host ("ws: failed ({0})" -f $_.Exception.Message)
}
