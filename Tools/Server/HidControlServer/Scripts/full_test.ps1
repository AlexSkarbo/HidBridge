param(
    [string]$BaseUrl = "http://127.0.0.1:8080",
    [string]$Token = "",
    [switch]$SkipVideo,
    [switch]$SkipHid,
    [switch]$SkipWs,
    [switch]$Strict
)

$ErrorActionPreference = "Stop"

function Run-Step([string]$name, [scriptblock]$cmd) {
    Write-Host ""
    Write-Host "=== $name ==="
    try {
        & $cmd | Out-Null
        return 0
    } catch {
        Write-Host $_.Exception.Message
        return 1
    }
}

$fail = 0
$root = Split-Path -Parent $PSCommandPath

if (-not $SkipVideo) {
    $fail += Run-Step "video_full_test" {
        & "$root\video_full_test.ps1" -BaseUrl $BaseUrl -AutoFixSources -Strict:$Strict
    }
}

if (-not $SkipHid) {
    $fail += Run-Step "hid_full_test" {
        & "$root\hid_full_test.ps1" -BaseUrl $BaseUrl -Token $Token -AllowStale -UseBootstrapKey
    }
}

if (-not $SkipWs) {
    $fail += Run-Step "ws_hid_test" {
        $ws = $BaseUrl.Replace("http://", "ws://").Replace("https://", "wss://")
        $ws = $ws.TrimEnd("/") + "/ws/hid"
        & "$root\ws_hid_test.ps1" -WsUrl $ws -Token $Token
    }
}

Write-Host ""
Write-Host "failures: $fail"
if ($fail -gt 0) { exit 1 }
exit 0
