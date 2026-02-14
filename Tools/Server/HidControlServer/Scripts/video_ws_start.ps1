param(
    [string]$BaseUrl = "http://127.0.0.1:8080",
    [string]$Token = "",
    [string]$SourceId = "cap-1",
    [string]$DeviceName = "USB3.0 Video",
    [string]$SourceUrl = "",
    [switch]$UseAlternativeName,
    [bool]$PreferAlternativeName = $true,
    [switch]$UseDeviceName,
    [switch]$RestartFfmpeg = $true,
    [switch]$ForceStart,
    [switch]$SkipTestCapture,
    [int]$TimeoutSec = 6
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

try { Add-Type -AssemblyName System.Net.Http } catch { }

function Get-Headers {
    $headers = @{}
    if ($Token) { $headers["X-HID-Token"] = $Token }
    return $headers
}

function Invoke-Api([string]$method, [string]$path, $body = $null) {
    $url = $BaseUrl + $path
    $headers = Get-Headers
    if ($body -ne $null) {
        return Invoke-RestMethod -Method $method -Uri $url -Headers $headers -ContentType "application/json" -Body $body
    }
    return Invoke-RestMethod -Method $method -Uri $url -Headers $headers
}

function Pick-First($items, [string]$name) {
    if (-not $items) { return $null }
    $match = $items | Where-Object { $_.name -like "*$name*" } | Select-Object -First 1
    if ($match) { return $match }
    return $items | Select-Object -First 1
}

function Ws-Send([System.Net.WebSockets.ClientWebSocket]$ws, [string]$json) {
    if ($ws.State -ne [System.Net.WebSockets.WebSocketState]::Open) { return $false }
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
    $seg = New-Object System.ArraySegment[byte] -ArgumentList (, $bytes)
    $ws.SendAsync($seg, [System.Net.WebSockets.WebSocketMessageType]::Text, $true, [Threading.CancellationToken]::None).GetAwaiter().GetResult() | Out-Null
    return $true
}

function Ws-Recv([System.Net.WebSockets.ClientWebSocket]$ws, [int]$timeoutMs) {
    if ($ws.State -ne [System.Net.WebSockets.WebSocketState]::Open) { return "(closed)" }
    $buf = New-Object byte[] 4096
    $seg = New-Object System.ArraySegment[byte] -ArgumentList (, $buf)
    $recvTask = $ws.ReceiveAsync($seg, [Threading.CancellationToken]::None)
    $done = [System.Threading.Tasks.Task]::WhenAny($recvTask, [System.Threading.Tasks.Task]::Delay($timeoutMs)).GetAwaiter().GetResult()
    if ($done -ne $recvTask) {
        return "(timeout)"
    }
    $res = $recvTask.GetAwaiter().GetResult()
    if ($res.Count -le 0) { return $null }
    return [System.Text.Encoding]::UTF8.GetString($buf, 0, $res.Count)
}

Write-Host "=== HidControlServer video+ws start ==="
Write-Host "BaseUrl: $BaseUrl"
Write-Host "SourceId: $SourceId"

try {
    $health = Invoke-Api GET "/health"
    if (-not $health.ok) { throw "health failed" }
    Write-Host "health: ok"
} catch {
    Write-Host "health: failed"
    throw
}

$serverOs = $null
$osIsWindows = $false
try {
    $diag = Invoke-Api GET "/video/diag"
    if ($diag -and $diag.os) { $serverOs = $diag.os.ToString().ToLowerInvariant() }
} catch { }
if ([string]::IsNullOrWhiteSpace($serverOs)) {
    throw "cannot detect server OS (video/diag failed). Use -SourceUrl to force a source."
}
if ($serverOs -eq "windows") {
    $osIsWindows = $true
} elseif ($serverOs -eq "linux") {
    $osIsWindows = $false
} elseif ($serverOs -eq "macos") {
    $osIsWindows = $false
} else {
    throw "unknown server OS: $serverOs"
}

if ($SourceUrl) {
    $payload = @{
        sources = @(
            @{
                id = $SourceId
                kind = "uvc"
                url = $SourceUrl
                name = $DeviceName
                enabled = $true
            }
        )
    } | ConvertTo-Json -Depth 6
    Invoke-Api POST "/video/sources" $payload | Out-Null
    Write-Host "source: set ($SourceUrl)"
} elseif ($osIsWindows) {
    $dshow = Invoke-Api GET "/video/dshow/devices"
    $wmi = Invoke-Api GET "/video/sources/windows"
    if (-not $dshow.ok -or -not $wmi.ok) { throw "cannot list windows devices" }
    $dshowDev = Pick-First $dshow.devices $DeviceName
    $wmiDev = Pick-First $wmi.devices $DeviceName
    if (-not $dshowDev -or -not $wmiDev) { throw "no windows devices found" }
    $videoArg = $dshowDev.name
    if (-not $UseDeviceName -and ($UseAlternativeName -or ($PreferAlternativeName -and $dshowDev.alternativeName))) {
        $videoArg = $dshowDev.alternativeName
    }
    $payload = @{
        sources = @(
            @{
                id = $SourceId
                kind = "uvc"
                url = $wmiDev.url
                name = $wmiDev.name
                enabled = $true
                ffmpegInputOverride = "-f dshow -i video=`"$videoArg`""
            }
        )
    } | ConvertTo-Json -Depth 6
    Invoke-Api POST "/video/sources" $payload | Out-Null
    Write-Host "source: set (windows, $($wmiDev.name))"
} else {
    $payload = @{
        sources = @(
            @{
                id = $SourceId
                kind = "uvc"
                url = "/dev/video0"
                name = "USB Capture"
                enabled = $true
            }
        )
    } | ConvertTo-Json -Depth 6
    Invoke-Api POST "/video/sources" $payload | Out-Null
    Write-Host "source: set (/dev/video0)"
}

Write-Host ""
Write-Host "== video/sources =="
$sources = Invoke-Api GET "/video/sources"
$sources | ConvertTo-Json -Depth 6 | Out-Host
$current = $sources.sources | Where-Object { $_.id -eq $SourceId } | Select-Object -First 1
if (-not $current) {
    Write-Host "warning: source not found, using first enabled"
    $current = $sources.sources | Where-Object { $_.enabled } | Select-Object -First 1
    if ($current) { $SourceId = $current.id }
}

$ffmpegRunning = $false
try {
    $diag = Invoke-Api GET "/video/diag"
    $diagSource = $diag.sources | Where-Object { $_.id -eq $SourceId } | Select-Object -First 1
    if ($diagSource -and $diagSource.ffmpegRunning) { $ffmpegRunning = $true }
} catch { }

try {
    $diagNow = Invoke-Api GET "/video/diag"
    $diagSource = $diagNow.sources | Where-Object { $_.id -eq $SourceId } | Select-Object -First 1
    if ($diagSource) {
        $input = [string]$diagSource.ffmpegInput
        $mismatch = $false
        if ($osIsWindows -and $input -match "/dev/video") { $mismatch = $true }
        if (-not $osIsWindows -and $input -match "dshow") { $mismatch = $true }
        if ($mismatch) {
            Write-Host "note: ffmpeg input does not match server OS, forcing restart"
            $RestartFfmpeg = $true
            $SkipTestCapture = $false
            try { Invoke-Api POST ("/video/capture/stop?id=" + $SourceId) | Out-Null } catch { }
            try { Invoke-Api POST "/video/ffmpeg/stop" | Out-Null } catch { }
            Start-Sleep -Milliseconds 300
        } elseif ($diagSource.workerRunning -or $diagSource.ffmpegRunning) {
            Write-Host ("note: capture already running (worker=" + $diagSource.workerRunning + ", ffmpeg=" + $diagSource.ffmpegRunning + ")")
            $RestartFfmpeg = $false
            $SkipTestCapture = $true
        }
    }
} catch { }

if ($RestartFfmpeg) {
    try { Invoke-Api POST "/video/ffmpeg/stop" | Out-Null } catch { }
    Start-Sleep -Milliseconds 300
}

try { Invoke-Api POST ("/video/capture/stop?id=" + $SourceId) | Out-Null } catch { }

$testOk = $false
Write-Host ""
Write-Host "== video/test-capture =="
if ($SkipTestCapture) {
    Write-Host "skipped (capture already running)"
    $testOk = $true
} else {
    try {
        $tc = Invoke-Api POST ("/video/test-capture?id=" + $SourceId + "&timeoutSec=" + $TimeoutSec)
        $testOk = $tc.ok -eq $true
        if ($tc.skipped) {
            Write-Host ("skipped: " + $tc.skipped)
            $testOk = $true
        }
        Write-Host ("ok=" + $testOk + " exitCode=" + $tc.exitCode)
        if (-not $testOk -and $tc.error) { Write-Host ("error: " + $tc.error) }
        if (-not $testOk -and $tc.stderr) { Write-Host ("stderr: " + $tc.stderr) }
        if (-not $testOk -and $tc.error -eq "exit_-5" -and $osIsWindows -and -not $UseAlternativeName) {
            Write-Host "retry: using alternativeName"
            $UseAlternativeName = $true
            $dshow = Invoke-Api GET "/video/dshow/devices"
            $wmi = Invoke-Api GET "/video/sources/windows"
            $dshowDev = Pick-First $dshow.devices $DeviceName
            $wmiDev = Pick-First $wmi.devices $DeviceName
            if ($dshowDev -and $wmiDev -and $dshowDev.alternativeName) {
            $payload = @{
                sources = @(
                    @{
                        id = $SourceId
                        kind = "uvc"
                        url = $wmiDev.url
                        name = $wmiDev.name
                        enabled = $true
                        ffmpegInputOverride = "-f dshow -i video=`"$($dshowDev.alternativeName)`""
                    }
                )
            } | ConvertTo-Json -Depth 6
            Invoke-Api POST "/video/sources" $payload | Out-Null
            $tc = Invoke-Api POST ("/video/test-capture?id=" + $SourceId + "&timeoutSec=" + $TimeoutSec)
            $testOk = $tc.ok -eq $true
            Write-Host ("ok=" + $testOk + " exitCode=" + $tc.exitCode)
        }
        }
        if (-not $testOk -and $osIsWindows) {
            Write-Host "hint: exit_-5 often means device busy (close OBS/Teams/Browser/ffmpeg)"
            Write-Host "hint: try .\\Scripts\\video_reset_capture.ps1"
        }
    } catch {
        Write-Host $_.Exception.Message
    }
}

if ($RestartFfmpeg) {
    if ($testOk -or $ForceStart) {
        if ($ffmpegRunning -and -not $ForceStart) {
            Write-Host "ffmpeg: already running"
        } else {
            Invoke-Api POST "/video/ffmpeg/start" "{}" | Out-Null
            Write-Host "ffmpeg: started"
            Start-Sleep -Milliseconds 1500
        }
    } else {
        Write-Host "ffmpeg: skipped (test-capture failed)"
    }
}

Write-Host ""
Write-Host "== video/streams =="
try {
    Invoke-Api GET "/video/streams" | ConvertTo-Json -Depth 6 | Out-Host
} catch {
    Write-Host $_.Exception.Message
}

if (-not $testOk) {
    Write-Host ""
    Write-Host "== video/diag =="
    try {
        Invoke-Api GET "/video/diag" | ConvertTo-Json -Depth 6 | Out-Host
    } catch {
        Write-Host $_.Exception.Message
    }
}

if ($testOk) {
    $secondaryAllowed = $true
    try {
        $diagNow = Invoke-Api GET "/video/diag"
        if ($diagNow.videoSecondaryCaptureEnabled -eq $false -and $diagNow.ffmpegProcesses.Count -gt 0) {
            $secondaryAllowed = $false
        }
    } catch { }

    Write-Host ""
    Write-Host "== video/snapshot =="
    if (-not $secondaryAllowed) {
        Write-Host "skipped (videoSecondaryCaptureEnabled=false)"
    } else {
        try {
            $headers = Get-Headers
            $snap = Invoke-WebRequest -Method GET -Uri ($BaseUrl + "/video/snapshot/" + $SourceId) -Headers $headers -TimeoutSec $TimeoutSec
            Write-Host ("snapshot bytes: " + $snap.RawContentLength)
        } catch {
            Write-Host $_.Exception.Message
            try {
                Write-Host "diag (snapshot failure):"
                Invoke-Api GET "/video/diag" | ConvertTo-Json -Depth 6 | Out-Host
            } catch { }
        }
    }

    Write-Host ""
    Write-Host "== video/mjpeg (first chunk) =="
    if (-not $secondaryAllowed) {
        Write-Host "skipped (videoSecondaryCaptureEnabled=false)"
    } else {
        try {
            $req = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::Get, "$BaseUrl/video/mjpeg/$SourceId?fps=5")
            if ($Token) { $req.Headers.Add("X-HID-Token", $Token) }
            $client = New-Object System.Net.Http.HttpClient
            $client.Timeout = [TimeSpan]::FromSeconds($TimeoutSec)
            $resp = $client.SendAsync($req).GetAwaiter().GetResult()
            if (-not $resp.IsSuccessStatusCode) {
                Write-Host ("http: " + [int]$resp.StatusCode + " " + $resp.ReasonPhrase)
                $body = $resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                Write-Host $body
                $resp.Dispose()
                $client.Dispose()
                throw "mjpeg_failed"
            }
            $stream = $resp.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
            $buf = New-Object byte[] 4096
            $read = $stream.Read($buf, 0, $buf.Length)
            $txt = [System.Text.Encoding]::ASCII.GetString($buf, 0, $read)
            Write-Host $txt
            $stream.Dispose()
            $resp.Dispose()
            $client.Dispose()
        } catch {
            Write-Host $_.Exception.Message
            try {
                Write-Host "diag (mjpeg failure):"
                Invoke-Api GET "/video/diag" | ConvertTo-Json -Depth 6 | Out-Host
            } catch { }
        }
    }
} else {
    Write-Host ""
    Write-Host "== video/snapshot =="
    Write-Host "skipped (test-capture failed)"
    Write-Host ""
    Write-Host "== video/mjpeg (first chunk) =="
    Write-Host "skipped (test-capture failed)"
}

Write-Host ""
Write-Host "== WS HID quick test =="
try {
    try { Invoke-Api GET "/devices?useBootstrapKey=true" | Out-Null } catch { }
    $wsUrl = $BaseUrl.Replace("http://", "ws://").Replace("https://", "wss://") + "/ws/hid"
    if ($Token) { $wsUrl = $wsUrl + "?access_token=" + [System.Uri]::EscapeDataString($Token) }
    $ws = New-Object System.Net.WebSockets.ClientWebSocket
    $ws.ConnectAsync([Uri]$wsUrl, [Threading.CancellationToken]::None).GetAwaiter().GetResult()
    $mouseItf = 0
    $keyboardItf = 0
    try {
        $dev = Invoke-Api GET "/devices?useBootstrapKey=true"
        $mouseItf = ($dev.list.interfaces | Where-Object { $_.typeName -eq "mouse" } | Select-Object -First 1).itf
        $keyboardItf = ($dev.list.interfaces | Where-Object { $_.typeName -eq "keyboard" } | Select-Object -First 1).itf
    } catch { }
    $msgs = @(
        '{"type":"ping","id":"1"}',
        ('{"type":"mouse.move","dx":10,"dy":0,"itfSel":' + $mouseItf + ',"id":"2"}'),
        ('{"type":"keyboard.text","text":"WS_OK ","itfSel":' + $keyboardItf + ',"id":"3"}')
    )
    foreach ($m in $msgs) {
        if (-not (Ws-Send $ws $m)) { break }
        $resp = Ws-Recv $ws 3000
        Write-Host $resp
        if ($resp -eq "(timeout)") { break }
    }
    if ($ws.State -eq [System.Net.WebSockets.WebSocketState]::Open) {
        $ws.CloseAsync([System.Net.WebSockets.WebSocketCloseStatus]::NormalClosure, "ok", [Threading.CancellationToken]::None).GetAwaiter().GetResult()
    }
} catch {
    Write-Host $_.Exception.Message
}
