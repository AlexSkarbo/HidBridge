param(
    [string]$BaseUrl = "http://127.0.0.1:8080",
    [string]$Token = "",
    [string]$DeviceName = "USB3.0 Video",
    [string]$DeviceId = "",
    [int]$WaitMs = 1200
)

$ErrorActionPreference = "Stop"

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

function Is-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $p = New-Object Security.Principal.WindowsPrincipal($id)
    return $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

Write-Host "=== HidControlServer video reset capture ==="
Write-Host "BaseUrl: $BaseUrl"

if (-not (Is-Admin)) {
    Write-Host "error: run PowerShell as Administrator"
    exit 1
}

try {
    Invoke-Api POST "/video/ffmpeg/stop" | Out-Null
} catch { }

if (-not $DeviceId) {
    try {
        $wmi = Invoke-Api GET "/video/sources/windows"
        if (-not $wmi.ok) { throw "no devices" }
        $dev = $wmi.devices | Where-Object { $_.name -like "*$DeviceName*" } | Select-Object -First 1
        if (-not $dev) { $dev = $wmi.devices | Select-Object -First 1 }
        if ($dev) { $DeviceId = $dev.deviceId }
    } catch {
        Write-Host "error: cannot resolve deviceId"
        exit 1
    }
}

if (-not $DeviceId) {
    Write-Host "error: deviceId not found"
    exit 1
}

Write-Host ("deviceId: " + $DeviceId)

try {
    $pnp = Get-PnpDevice -InstanceId $DeviceId -ErrorAction Stop
} catch {
    Write-Host "error: device not found in PnP list"
    exit 1
}

try {
    Disable-PnpDevice -InstanceId $DeviceId -Confirm:$false -ErrorAction Stop | Out-Null
    Start-Sleep -Milliseconds $WaitMs
    Enable-PnpDevice -InstanceId $DeviceId -Confirm:$false -ErrorAction Stop | Out-Null
    Write-Host "device: reset ok"
} catch {
    Write-Host $_.Exception.Message
    exit 1
}

Write-Host "done"
