param(
    [string]$BaseUrl = "http://127.0.0.1:8080",
    [string]$Token = "",
    [bool]$UseBootstrapKey = $false,
    [bool]$IncludeReportDesc = $true,
    [bool]$ShowDiagnosticsOnFail = $true,
    [int]$ItfSel = -1,
    [int]$MaxReportId = 16,
    [int]$TestUsage1 = 4,
    [int]$TestUsage2 = 5
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

function Exit-Fail {
    param(
        [string]$Message,
        [int]$Code
    )
    Write-Host $Message
    exit $Code
}

function Get-Devices {
    param(
        [bool]$Bootstrap
    )
    $desc = if ($IncludeReportDesc) { "true" } else { "false" }
    $bootstrap = if ($Bootstrap) { "true" } else { "false" }
    return Invoke-Api -Method GET -Path ("/devices?includeReportDesc={0}&useBootstrapKey={1}" -f $desc, $bootstrap)
}

function Show-Diagnostics {
    try {
        $dev = Invoke-Api -Method GET -Path "/device-id"
        Write-Host ("device-id: {0} derived={1}" -f $dev.deviceId, $dev.derivedKeyActive)
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

$EXIT_HEALTH = 2
$EXIT_DEVICES = 3
$EXIT_NO_INTERFACES = 4
$EXIT_NO_KEYBOARD = 5

Write-Host "=== HidControlServer keyboard test ==="
Write-Host "BaseUrl: $BaseUrl"

try {
    Invoke-Api -Method GET -Path "/health" | Out-Null
    Write-Host "health: ok"
} catch {
    Exit-Fail "health: failed" $EXIT_HEALTH
}

try {
    $ver = Invoke-Api -Method GET -Path "/version"
    if ($ver.ok) {
        Write-Host ("version: repo={0} asm={1}" -f $ver.repoVersion, $ver.assemblyVersion)
    }
} catch {
    Write-Host "version: not available"
}

$cfg = Invoke-Api -Method GET -Path "/config"
Write-Host ("config: baud={0} serial={1} url={2}" -f $cfg.baud, $cfg.serialPort, $cfg.url)

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
    try {
        $retry = Invoke-Api -Method POST -Path "/hmac/retry"
        if ($retry.ok) {
            Write-Host ("hmac retry: {0}" -f $retry.hmacMode)
        }
    } catch { }
    Exit-Fail "devices: failed" $EXIT_DEVICES
}

$interfaces = $devices.list.interfaces
if (-not $interfaces) {
    Exit-Fail "devices: no interfaces" $EXIT_NO_INTERFACES
}

if ($ItfSel -lt 0) {
    $kbd = $interfaces | Where-Object { $_.typeName -like "*keyboard*" } | Select-Object -First 1
    if (-not $kbd) {
        Exit-Fail "no keyboard interface found" $EXIT_NO_KEYBOARD
    }
    $ItfSel = [int]$kbd.itf
}

Write-Host ("using itf: {0}" -f $ItfSel)

Write-Host "layout: default"
try {
    $layout0 = Invoke-Api -Method GET -Path ("/keyboard/layout?itf={0}" -f $ItfSel)
    Write-Host ("layout reportId={0} len={1} hasReportId={2}" -f $layout0.reportId, $layout0.reportLen, $layout0.hasReportId)
    if ($layout0.hasReportId -or $layout0.reportId -ne 0) {
        Write-Host "note: keyboard uses report IDs"
    }
} catch {
    Write-Host "layout: failed"
}

Write-Host "layout: reportId=1"
try {
    $layout1 = Invoke-Api -Method GET -Path ("/keyboard/layout?itf={0}&reportId=1" -f $ItfSel)
    Write-Host ("layout reportId={0} len={1} hasReportId={2}" -f $layout1.reportId, $layout1.reportLen, $layout1.hasReportId)
} catch {
    Write-Host "layout: failed"
}

Write-Host "layouts scan"
try {
    $layouts = Invoke-Api -Method GET -Path ("/keyboard/layouts?itf={0}&maxReportId={1}" -f $ItfSel, $MaxReportId)
    Write-Host ("layouts found: {0}" -f $layouts.layouts.Count)
    if ($layouts.layouts.Count -eq 0) {
        Write-Host "warning: no keyboard layouts found"
    }
} catch {
    Write-Host "layouts: failed"
}

Write-Host "test: report key1"
Invoke-Api -Method POST -Path "/keyboard/report" -Body @{
    modifiers = 0
    keys = @($TestUsage1)
    itfSel = $ItfSel
    applyMapping = $true
} | Out-Null
Start-Sleep -Milliseconds 80

Write-Host "test: report key2"
Invoke-Api -Method POST -Path "/keyboard/report" -Body @{
    modifiers = 0
    keys = @($TestUsage2)
    itfSel = $ItfSel
    applyMapping = $true
} | Out-Null
Start-Sleep -Milliseconds 80

Write-Host "test: report Shift+key1"
Invoke-Api -Method POST -Path "/keyboard/report" -Body @{
    modifiers = 2
    keys = @($TestUsage1)
    itfSel = $ItfSel
    applyMapping = $true
} | Out-Null
Start-Sleep -Milliseconds 80

Write-Host "reset"
Invoke-Api -Method POST -Path "/keyboard/reset" -Body @{ itfSel = $ItfSel } | Out-Null

$state = Invoke-Api -Method GET -Path "/keyboard/state"
Write-Host ("state: modifiers=0x{0} keys={1}" -f ($state.modifiers.ToString("X2")), ($state.keys -join ",")) 

Write-Host "done"
