param(
    [string]$BaseUrl = "http://127.0.0.1:8080",
    [string]$Token = "",
    [switch]$TryRtsp = $false,
    [int]$RtspTimeoutSec = 5
)

$ErrorActionPreference = "Stop"

$CurlPath = "curl.exe"
$Headers = @("Accept: application/json")
if ($Token) {
    $Headers += "X-HID-Token: $Token"
}

function Invoke-JsonGet([string]$Path) {
    $url = "$BaseUrl$Path"
    $args = @("-s")
    foreach ($h in $Headers) { $args += @("-H", $h) }
    $args += $url
    $raw = & $CurlPath @args
    if (-not $raw) {
        return $null
    }
    try {
        return $raw | ConvertFrom-Json
    } catch {
        Write-Host "invalid json from $Path"
        return $null
    }
}

Write-Host "=== HidControlServer video diagnostics ==="
Write-Host "BaseUrl: $BaseUrl"

$streams = Invoke-JsonGet "/video/streams"
$sources = Invoke-JsonGet "/video/sources"
$ffmpeg = Invoke-JsonGet "/video/ffmpeg/status"
$dshow = Invoke-JsonGet "/video/dshow/devices"
$wmi = Invoke-JsonGet "/video/sources/windows"

if ($ffmpeg) {
    $procs = @($ffmpeg.processes).Count
    $states = @($ffmpeg.states).Count
    Write-Host "ffmpeg: processes=$procs states=$states"
} else {
    Write-Host "ffmpeg: failed"
}

if ($streams -and $streams.ok) {
    $count = @($streams.streams).Count
    Write-Host "streams: $count"
    foreach ($s in $streams.streams) {
        $rtsp = $s.rtspUrl ?? ""
        $srt = $s.srtUrl ?? ""
        $rtmp = $s.rtmpUrl ?? ""
        $hls = $s.hlsUrl ?? ""
        $mjpeg = $s.mjpegUrl ?? ""
        Write-Host ("- " + $s.id + " rtsp=" + $rtsp)
        if ($srt) { Write-Host ("  srt=" + $srt) }
        if ($rtmp) { Write-Host ("  rtmp=" + $rtmp) }
        if ($hls) { Write-Host ("  hls=" + $hls) }
        if ($mjpeg) { Write-Host ("  mjpeg=" + $mjpeg) }
    }
} else {
    Write-Host "streams: failed"
}

if ($sources -and $sources.ok) {
    Write-Host ("sources: " + @($sources.sources).Count)
} else {
    Write-Host "sources: failed"
}

if ($dshow -and $dshow.ok) {
    Write-Host ("dshow devices: " + @($dshow.devices).Count)
} else {
    Write-Host "dshow devices: failed"
}

if ($wmi -and $wmi.ok) {
    Write-Host ("wmi devices: " + @($wmi.devices).Count)
} else {
    Write-Host "wmi devices: failed"
}

if ($TryRtsp -and $streams -and $streams.ok) {
    $configPath = Join-Path $PSScriptRoot "..\\hidcontrol.config.json"
    $ffmpegPath = ""
    if (Test-Path $configPath) {
        try {
            $cfg = Get-Content -Raw -Path $configPath | ConvertFrom-Json
            if ($cfg.ffmpegPath) { $ffmpegPath = $cfg.ffmpegPath }
        } catch { }
    }
    if (-not $ffmpegPath) {
        Write-Host "rtsp test: ffmpegPath not set"
        return
    }
    foreach ($s in $streams.streams) {
        $rtsp = $s.rtspUrl
        if (-not $rtsp) { continue }
        Write-Host ("rtsp test: " + $s.id)
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = $ffmpegPath
        $psi.Arguments = "-hide_banner -loglevel error -rtsp_transport tcp -i `"$rtsp`" -t 1 -f null -"
        $psi.RedirectStandardError = $true
        $psi.RedirectStandardOutput = $true
        $psi.UseShellExecute = $false
        $psi.CreateNoWindow = $true
        $proc = [System.Diagnostics.Process]::Start($psi)
        if (-not $proc) {
            Write-Host "  start failed"
            continue
        }
        if (-not $proc.WaitForExit($RtspTimeoutSec * 1000)) {
            try { $proc.Kill() } catch { }
            Write-Host "  timeout"
            continue
        }
        if ($proc.ExitCode -eq 0) {
            Write-Host "  ok"
        } else {
            $err = $proc.StandardError.ReadToEnd()
            Write-Host "  failed"
            if ($err) { Write-Host ("  " + $err.Trim()) }
        }
    }
}

Write-Host "done"
