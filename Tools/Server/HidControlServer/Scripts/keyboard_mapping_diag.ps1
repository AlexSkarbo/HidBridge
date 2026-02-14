param(
    [string]$BaseUrl = "http://127.0.0.1:8080",
    [string]$Token = "",
    [int]$ItfSel = -1,
    [bool]$UseBootstrapKey = $false,
    [bool]$IncludeReportDesc = $true,
    [bool]$ShowDiagnosticsOnFail = $true
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

function Show-Diagnostics {
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
}

function Get-Devices {
    param(
        [bool]$Bootstrap
    )
    $desc = if ($IncludeReportDesc) { "true" } else { "false" }
    $bootstrap = if ($Bootstrap) { "true" } else { "false" }
    return Invoke-Api -Method GET -Path ("/devices?includeReportDesc={0}&useBootstrapKey={1}" -f $desc, $bootstrap)
}

Write-Host "=== HidControlServer keyboard mapping diagnostics ==="
Write-Host "BaseUrl: $BaseUrl"

$devices = $null
try {
    $devices = Get-Devices -Bootstrap:$UseBootstrapKey
} catch { }
if (-not $devices -or -not $devices.ok) {
    if (-not $UseBootstrapKey) {
        try {
            $devices = Get-Devices -Bootstrap:$true
            if ($devices.ok) {
                Write-Host "devices: ok (bootstrap)"
            }
        } catch { }
    }
}
if (-not $devices -or -not $devices.ok) {
    if ($ShowDiagnosticsOnFail) {
        Show-Diagnostics
    }
    Write-Host "devices: failed"
    exit 2
}

$interfaces = $devices.list.interfaces
if (-not $interfaces) {
    Write-Host "devices: no interfaces"
    exit 3
}

if ($ItfSel -lt 0) {
    $kbd = $interfaces | Where-Object { $_.typeName -like "*keyboard*" } | Select-Object -First 1
    if (-not $kbd) {
        Write-Host "no keyboard interface found"
        exit 4
    }
    $ItfSel = [int]$kbd.itf
}

Write-Host ("keyboard itf: {0}" -f $ItfSel)

try {
    $layout = Invoke-Api -Method GET -Path ("/keyboard/layout?itf={0}" -f $ItfSel)
    Write-Host ("layout: reportId={0} len={1} hasReportId={2}" -f $layout.reportId, $layout.reportLen, $layout.hasReportId)
} catch {
    Write-Host "layout: failed"
}

try {
    $mapByItf = Invoke-Api -Method GET -Path ("/keyboard/mapping/byItf?itf={0}" -f $ItfSel)
    if ($mapByItf.ok) {
        Write-Host ("mapping/byItf: ok deviceId={0}" -f $mapByItf.deviceId)
    } else {
        Write-Host "mapping/byItf: not ok"
    }
} catch {
    Write-Host "mapping/byItf: not found"
}

$deviceId = $null
foreach ($i in $interfaces) {
    if ($i.itf -eq $ItfSel -and $i.deviceId) { $deviceId = $i.deviceId; break }
}

if ($deviceId) {
    Write-Host ("deviceId: {0}" -f $deviceId)
    try {
        $map = Invoke-Api -Method GET -Path ("/keyboard/mapping?deviceId={0}" -f $deviceId)
        if ($map.ok) {
            Write-Host "mapping: ok"
        } else {
            Write-Host "mapping: not ok"
        }
    } catch {
        Write-Host "mapping: not found"
    }
} else {
    Write-Host "deviceId: missing"
}

Write-Host "done"
