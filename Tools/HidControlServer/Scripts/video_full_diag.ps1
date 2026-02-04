param(
    [string]$BaseUrl = "http://127.0.0.1:8080",
    [string]$Token = "",
    [switch]$TryRtsp = $false,
    [int]$RtspTimeoutSec = 5
)

$ErrorActionPreference = "Stop"

$Headers = @("Accept: application/json")
if ($Token) { $Headers += "X-HID-Token: $Token" }

function Invoke-JsonGet([string]$Path) {
    $url = "$BaseUrl$Path"
    $args = @("-s")
    foreach ($h in $Headers) { $args += @("-H", $h) }
    $args += $url
    $raw = & curl.exe @args
    if (-not $raw) { return $null }
    try { return $raw | ConvertFrom-Json } catch { return $null }
}

Write-Host "=== HidControlServer video full diagnostics ==="
Write-Host "BaseUrl: $BaseUrl"

Write-Host "`n[ffmpeg/status]"
$ffmpeg = Invoke-JsonGet "/video/ffmpeg/status"
if ($ffmpeg) { $ffmpeg | ConvertTo-Json -Depth 10 } else { Write-Host "failed" }

Write-Host "`n[streams]"
$streams = Invoke-JsonGet "/video/streams"
if ($streams) { $streams | ConvertTo-Json -Depth 10 } else { Write-Host "failed" }

Write-Host "`n[sources]"
$sources = Invoke-JsonGet "/video/sources"
if ($sources) { $sources | ConvertTo-Json -Depth 10 } else { Write-Host "failed" }

Write-Host "`n[dshow/devices]"
$dshow = Invoke-JsonGet "/video/dshow/devices"
if ($dshow) { $dshow | ConvertTo-Json -Depth 10 } else { Write-Host "failed" }

Write-Host "`n[sources/windows]"
$wmi = Invoke-JsonGet "/video/sources/windows"
if ($wmi) { $wmi | ConvertTo-Json -Depth 10 } else { Write-Host "failed" }

if ($TryRtsp -and $streams -and $streams.ok) {
    Write-Host "`n[rtsp test]"
    $configPath = Join-Path $PSScriptRoot "..\\hidcontrol.config.json"
    if (Test-Path $configPath) {
        $cfg = Get-Content -Raw -Path $configPath | ConvertFrom-Json
        $ff = $cfg.ffmpegPath
        foreach ($s in $streams.streams) {
            $rtsp = $s.rtspUrl
            if (-not $rtsp) { continue }
            Write-Host ("Testing: " + $s.id + " -> " + $rtsp)
            & $ff -hide_banner -loglevel error -rtsp_transport tcp -i $rtsp -t 1 -f null -
        }
    } else {
        Write-Host "ffmpegPath not found in config"
    }
}

if ($streams -and $streams.ok) {
    $first = $streams.streams | Select-Object -First 1
    if ($first -and $first.hlsUrl) {
        Write-Host "`n[hls playlist]"
        $url = "$BaseUrl/video/hls/$($first.id)/index.m3u8"
        $args = @("-s")
        foreach ($h in $Headers) { $args += @("-H", $h) }
        $args += $url
        $raw = & curl.exe @args
        if ($raw) {
            $lines = $raw -split "`n"
            $lines | Select-Object -First 8 | ForEach-Object { Write-Host $_ }
        } else {
            Write-Host "failed"
        }
    }
}

Write-Host "`n[ffmpeg logs tail]"
$logDir = Join-Path $PSScriptRoot ".."
$ffLogs = Get-ChildItem -Path $logDir -Filter "ffmpeg_*.log" -File -ErrorAction SilentlyContinue
if ($ffLogs) {
    foreach ($log in $ffLogs) {
        Write-Host ("--- " + $log.Name + " ---")
        Get-Content $log.FullName -Tail 40
    }
} else {
    Write-Host "none"
}

Write-Host "`n[done]"
