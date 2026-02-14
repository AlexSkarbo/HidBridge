param(
    [string]$BaseUrl = "http://127.0.0.1:8080",
    [string]$Token = "",
    [string]$SourceId = "",
    [int]$TimeoutSec = 6,
    [switch]$AutoFixSources,
    [switch]$Strict,
    [switch]$ScanApply,
    [switch]$StartFfmpeg
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

function Write-Section([string]$title) {
    Write-Host ""
    Write-Host "=== $title ==="
}

function Get-Headers {
    $headers = @{}
    if ($Token) { $headers["X-HID-Token"] = $Token }
    return $headers
}

function Invoke-Json([string]$method, [string]$url, $body = $null) {
    $headers = Get-Headers
    if ($body -ne $null) {
        return Invoke-RestMethod -Method $method -Uri $url -Headers $headers -ContentType "application/json" -Body $body
    }
    return Invoke-RestMethod -Method $method -Uri $url -Headers $headers
}

function Try-FixWindowsSource([string]$sourceId) {
    try {
        $dshow = Invoke-Json "GET" "$BaseUrl/video/dshow/devices"
        $wmi = Invoke-Json "GET" "$BaseUrl/video/sources/windows"
        if (-not $dshow.ok -or -not $wmi.ok) { return $false }
        $dshowDev = $dshow.devices | Where-Object { $_.name -like "*USB3.0 Video*" } | Select-Object -First 1
        if (-not $dshowDev) { $dshowDev = $dshow.devices | Select-Object -First 1 }
        $wmiDev = $wmi.devices | Where-Object { $_.name -like "*USB3.0 Video*" } | Select-Object -First 1
        if (-not $wmiDev) { $wmiDev = $wmi.devices | Select-Object -First 1 }
        if (-not $dshowDev -or -not $wmiDev) { return $false }

        $payload = @{
            id = $sourceId
            kind = "uvc"
            url = $wmiDev.url
            name = $wmiDev.name
            enabled = $true
            ffmpegInputOverride = "-f dshow -i video=`"$($dshowDev.name)`""
        } | ConvertTo-Json -Depth 6

        $up = Invoke-Json "POST" "$BaseUrl/video/sources/upsert" $payload
        return $up.ok -eq $true
    } catch {
        return $false
    }
}

function Test-Ok([string]$name, [bool]$ok, [string]$details = "") {
    if ($ok) {
        Write-Host "[PASS] $name"
    } else {
        if ($details) { Write-Host "[FAIL] $name :: $details" } else { Write-Host "[FAIL] $name" }
    }
    return $ok
}

$passed = 0
$failed = 0
$osIsWindows = $false
$osIsLinux = $false
$osIsMac = $false
try { $osIsWindows = [System.OperatingSystem]::IsWindows() } catch { }
try { $osIsLinux = [System.OperatingSystem]::IsLinux() } catch { }
try { $osIsMac = [System.OperatingSystem]::IsMacOS() } catch { }

$shouldRestartFfmpeg = $false
try {
    $ffStatus = Invoke-Json "GET" "$BaseUrl/video/ffmpeg/status"
    if ($ffStatus.ok -and $ffStatus.processes -and $ffStatus.processes.Count -gt 0) {
        $shouldRestartFfmpeg = $true
        Invoke-Json "POST" "$BaseUrl/video/ffmpeg/stop" | Out-Null
        Start-Sleep -Milliseconds 300
    }
} catch { }

Write-Host "=== HidControlServer video full test ==="
Write-Host "BaseUrl: $BaseUrl"

try {
    $health = Invoke-Json "GET" "$BaseUrl/health"
    if (Test-Ok "health" ($health.ok -eq $true)) { $passed++ } else { $failed++ }
} catch {
    Test-Ok "health" $false $_.Exception.Message | Out-Null
    $failed++
}

try {
    $cfg = Invoke-Json "GET" "$BaseUrl/config"
    $ffmpegPath = $cfg.ffmpegPath
    Write-Host "ffmpegPath: $ffmpegPath"
    $passed++
} catch {
    Test-Ok "config" $false $_.Exception.Message | Out-Null
    $failed++
}

if ($ScanApply) {
    Write-Section "scan sources (apply)"
    try {
        $scan = Invoke-Json "POST" "$BaseUrl/video/sources/scan?apply=true"
        if (Test-Ok "scan/apply" ($scan.ok -eq $true)) { $passed++ } else { $failed++ }
    } catch {
        Test-Ok "scan/apply" $false $_.Exception.Message | Out-Null
        $failed++
    }
}

Write-Section "sources"
$sources = @()
try {
    $src = Invoke-Json "GET" "$BaseUrl/video/sources"
    $sources = @($src.sources)
    if (Test-Ok "sources/list" ($src.ok -eq $true)) { $passed++ } else { $failed++ }
    Write-Host ("sources: " + $sources.Count)
} catch {
    Test-Ok "sources/list" $false $_.Exception.Message | Out-Null
    $failed++
}

Write-Section "streams"
try {
    $streams = Invoke-Json "GET" "$BaseUrl/video/streams"
    if (Test-Ok "streams/list" ($streams.ok -eq $true)) { $passed++ } else { $failed++ }
} catch {
    Test-Ok "streams/list" $false $_.Exception.Message | Out-Null
    $failed++
}

Write-Section "profiles"
try {
    $profiles = Invoke-Json "GET" "$BaseUrl/video/profiles"
    if (Test-Ok "profiles/list" ($profiles.ok -eq $true)) { $passed++ } else { $failed++ }
} catch {
    Test-Ok "profiles/list" $false $_.Exception.Message | Out-Null
    $failed++
}

if ($StartFfmpeg) {
    Write-Section "ffmpeg start"
    try {
        $start = Invoke-Json "POST" "$BaseUrl/video/ffmpeg/start" "{}"
        if (Test-Ok "ffmpeg/start" ($start.ok -eq $true)) { $passed++ } else { $failed++ }
    } catch {
        Test-Ok "ffmpeg/start" $false $_.Exception.Message | Out-Null
        $failed++
    }
}

$target = $null
if ($SourceId) {
    $target = $sources | Where-Object { $_.id -eq $SourceId } | Select-Object -First 1
} else {
    $target = $sources | Where-Object { $_.enabled } | Select-Object -First 1
}

if ($null -eq $target) {
    Test-Ok "select source" $false "no enabled sources" | Out-Null
    $failed++
} else {
    Write-Host "using source: $($target.id)"

    $skipStreamTests = $false
    Write-Section "test-capture"
    try {
        $tc = Invoke-Json "POST" "$BaseUrl/video/test-capture?id=$($target.id)&timeoutSec=$TimeoutSec"
        $retry = $false
        $mismatch = $false
        if ($tc.ok -eq $true) {
            Test-Ok "test-capture" $true | Out-Null
            $passed++
        } else {
            $mismatch = $tc.error -eq "linux_source_on_windows" -or $tc.error -eq "windows_source_on_linux" -or $tc.error -eq "windows_source_on_macos"
            if ($mismatch) {
                if ($AutoFixSources -and $osIsWindows) {
                    Write-Host "[WARN] test-capture :: source/OS mismatch, trying to auto-fix"
                    if (Try-FixWindowsSource $target.id) {
                        $retry = $true
                    } else {
                        Write-Host "auto-fix failed"
                    }
                } else {
                    Write-Host "[WARN] test-capture :: source/OS mismatch"
                }
            } else {
                Test-Ok "test-capture" $false | Out-Null
                $failed++
            }
            Write-Host ("error: " + $tc.error)
            if ($tc.stderr) { Write-Host ("stderr: " + $tc.stderr) }
        }
        if ($retry) {
            $tc = Invoke-Json "POST" "$BaseUrl/video/test-capture?id=$($target.id)&timeoutSec=$TimeoutSec"
            if ($tc.ok -eq $true) {
                Test-Ok "test-capture (retry)" $true | Out-Null
                $passed++
            } else {
                $mismatch = $tc.error -eq "linux_source_on_windows" -or $tc.error -eq "windows_source_on_linux" -or $tc.error -eq "windows_source_on_macos"
                if ($mismatch -and -not $Strict) {
                    Write-Host "[WARN] test-capture (retry) :: source/OS mismatch"
                    $skipStreamTests = $true
                } else {
                    Test-Ok "test-capture (retry)" $false | Out-Null
                    $failed++
                }
                Write-Host ("error: " + $tc.error)
                if ($tc.stderr) { Write-Host ("stderr: " + $tc.stderr) }
            }
        }
        if ($mismatch -and -not $retry) {
            if ($Strict) {
                $failed++
            } else {
                Write-Host "stream tests skipped due to source/OS mismatch"
                $skipStreamTests = $true
            }
        }
    } catch {
        Test-Ok "test-capture" $false $_.Exception.Message | Out-Null
        $failed++
    }

    if ($shouldRestartFfmpeg) {
        try { Invoke-Json "POST" "$BaseUrl/video/ffmpeg/start" "{}" | Out-Null } catch { }
    }

    if (-not $skipStreamTests) {
        Write-Section "snapshot"
        try {
            $headers = Get-Headers
            $snap = Invoke-WebRequest -Method GET -Uri "$BaseUrl/video/snapshot/$($target.id)" -Headers $headers -TimeoutSec $TimeoutSec
            $bytes = $snap.Content
            $ok = $bytes.Length -gt 4 -and $bytes[0] -eq 0xFF -and $bytes[1] -eq 0xD8
            if (Test-Ok "snapshot" $ok ("len=" + $bytes.Length)) { $passed++ } else { $failed++ }
        } catch {
            Test-Ok "snapshot" $false $_.Exception.Message | Out-Null
            $failed++
        }

        Write-Section "mjpeg"
        try {
            $client = New-Object System.Net.Http.HttpClient
            $client.Timeout = [TimeSpan]::FromSeconds($TimeoutSec)
            $req = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::Get, "$BaseUrl/video/mjpeg/$($target.id)?fps=5")
            if ($Token) {
                $req.Headers.Add("X-HID-Token", $Token)
            }
            $resp = $client.SendAsync($req).GetAwaiter().GetResult()
            $stream = $resp.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
            $buf = New-Object byte[] 65536
            $maxBytes = 262144
            $readTotal = 0
            $bytes = New-Object System.Collections.Generic.List[byte]
            while ($readTotal -lt $maxBytes) {
                $read = $stream.Read($buf, 0, $buf.Length)
                if ($read -le 0) { break }
                $readTotal += $read
                for ($i = 0; $i -lt $read; $i++) { $bytes.Add($buf[$i]) }
                if ($readTotal -ge 64) {
                    $text = [System.Text.Encoding]::ASCII.GetString($bytes.ToArray())
                    if ($text -match "--frame") { break }
                }
            }
            $text = [System.Text.Encoding]::ASCII.GetString($bytes.ToArray())
            $ok = ($text -match "--frame") -and ($readTotal -gt 256)
            if (Test-Ok "mjpeg" $ok ("len=" + $readTotal)) { $passed++ } else { $failed++ }
            $stream.Dispose()
            $resp.Dispose()
            $client.Dispose()
        } catch {
            Test-Ok "mjpeg" $false $_.Exception.Message | Out-Null
            $failed++
        }

        Write-Section "hls"
        try {
            $headers = Get-Headers
            $hls = Invoke-WebRequest -Method GET -Uri "$BaseUrl/video/hls/$($target.id)/index.m3u8" -Headers $headers -TimeoutSec $TimeoutSec
            $text = $hls.Content
            $ok = $text -match "#EXTM3U"
            if (Test-Ok "hls" $ok ("len=" + $text.Length)) { $passed++ } else { $failed++ }
        } catch {
            Test-Ok "hls" $false $_.Exception.Message | Out-Null
            $failed++
        }
    }
}

Write-Host ""
Write-Host "passed: $passed"
Write-Host "failed: $failed"

if ($failed -gt 0) { exit 1 }
exit 0
