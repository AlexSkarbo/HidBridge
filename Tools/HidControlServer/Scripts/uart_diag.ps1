param(
    [string]$BaseUrl = "http://127.0.0.1:8080",
    [string]$Token = "",
    [bool]$IncludeReportDesc = $false
)

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

Write-Host "=== HidControlServer UART diagnostics ==="
Write-Host "BaseUrl: $BaseUrl"

try {
    Invoke-Api -Method GET -Path "/health" | Out-Null
    Write-Host "health: ok"
} catch {
    Write-Host "health: failed"
}

try {
    $ver = Invoke-Api -Method GET -Path "/version"
    if ($ver.ok) {
        Write-Host ("version: repo={0} asm={1}" -f $ver.repoVersion, $ver.assemblyVersion)
    }
} catch {
    Write-Host "version: not available"
}

try {
    $dev = Invoke-Api -Method GET -Path "/device-id"
    Write-Host ("device-id: {0} derived={1} hmacMode={2}" -f $dev.deviceId, $dev.derivedKeyActive, $dev.hmacMode)
} catch {
    Write-Host "device-id: failed"
}

try {
    $stats = Invoke-Api -Method GET -Path "/stats"
    $uart = $stats.uart
    Write-Host ("uart: framesRx={0} framesTx={1} rxBadHmac={2} rxBadCrc={3} hmacMode={4}" -f $uart.framesReceived, $uart.framesSent, $uart.rxBadHmac, $uart.rxBadCrc, $stats.hmacMode)
} catch {
    Write-Host "stats: failed"
}

$desc = if ($IncludeReportDesc) { "true" } else { "false" }
try {
    $devs = Invoke-Api -Method GET -Path ("/devices?includeReportDesc={0}" -f $desc)
    Write-Host ("devices: ok (default) count={0}" -f $devs.list.interfaces.Count)
} catch {
    Write-Host "devices: failed (default)"
}

try {
    $devs = Invoke-Api -Method GET -Path ("/devices?includeReportDesc={0}&useBootstrapKey=true" -f $desc)
    Write-Host ("devices: ok (bootstrap) count={0}" -f $devs.list.interfaces.Count)
} catch {
    Write-Host "devices: failed (bootstrap)"
}

try {
    $retry = Invoke-Api -Method POST -Path "/hmac/retry"
    Write-Host ("hmac retry: {0}" -f $retry.hmacMode)
} catch {
    Write-Host "hmac retry: failed"
}
