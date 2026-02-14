param(
    [string]$BaseUrl = "http://127.0.0.1:8080",
    [string]$Token = "",
    [bool]$UseBootstrapKey = $false,
    [bool]$IncludeReportDesc = $true,
    [bool]$ShowDiagnosticsOnFail = $true,
    [int]$ItfSel = -1,
    [int]$Dx = 10,
    [int]$Dy = 0,
    [int]$Wheel = 1
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
$EXIT_NO_MOUSE = 6

Write-Host "=== HidControlServer mouse test ==="
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
    $mouse = $interfaces | Where-Object { $_.typeName -like "*mouse*" } | Select-Object -First 1
    if (-not $mouse) {
        Exit-Fail "no mouse interface found" $EXIT_NO_MOUSE
    }
    $ItfSel = [int]$mouse.itf
}

Write-Host ("using itf: {0}" -f $ItfSel)

Write-Host "move"
Invoke-Api -Method POST -Path "/mouse/move" -Body @{
    dx = $Dx
    dy = $Dy
    itfSel = $ItfSel
} | Out-Null
Start-Sleep -Milliseconds 80

Write-Host "wheel"
Invoke-Api -Method POST -Path "/mouse/wheel" -Body @{
    delta = $Wheel
    itfSel = $ItfSel
} | Out-Null
Start-Sleep -Milliseconds 80

Write-Host "left click"
Invoke-Api -Method POST -Path "/mouse/leftClick" -Body @{ itfSel = $ItfSel } | Out-Null
Start-Sleep -Milliseconds 80

Write-Host "right click"
Invoke-Api -Method POST -Path "/mouse/rightClick" -Body @{ itfSel = $ItfSel } | Out-Null
Start-Sleep -Milliseconds 80

Write-Host "buttons mask=1 then clear"
Invoke-Api -Method POST -Path "/mouse/buttons" -Body @{ buttonsMask = 1; itfSel = $ItfSel } | Out-Null
Start-Sleep -Milliseconds 80
Invoke-Api -Method POST -Path "/mouse/buttons" -Body @{ buttonsMask = 0; itfSel = $ItfSel } | Out-Null

Write-Host "done"
