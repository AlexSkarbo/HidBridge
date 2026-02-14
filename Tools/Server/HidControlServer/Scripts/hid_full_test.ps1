param(
    [string]$BaseUrl = "http://127.0.0.1:8080",
    [string]$Token = "",
    [byte]$MouseItf = 255,
    [byte]$KeyboardItf = 254,
    [switch]$UseBootstrapKey,
    [switch]$AllowStale,
    [switch]$SkipInject
)

$ErrorActionPreference = "Stop"

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

Write-Host "=== HidControlServer HID full test ==="
Write-Host "BaseUrl: $BaseUrl"

try {
    $health = Invoke-Json "GET" "$BaseUrl/health"
    if (Test-Ok "health" ($health.ok -eq $true)) { $passed++ } else { $failed++ }
} catch {
    Test-Ok "health" $false $_.Exception.Message | Out-Null
    $failed++
}

$cfg = $null
try {
    $cfg = Invoke-Json "GET" "$BaseUrl/config"
    Write-Host ("config: baud={0} serial={1} url={2}" -f $cfg.baud, $cfg.serialPort, $cfg.url)
    if (Test-Ok "config" ($cfg.ok -eq $true)) { $passed++ } else { $failed++ }
} catch {
    Test-Ok "config" $false $_.Exception.Message | Out-Null
    $failed++
}

$devices = $null
try {
    $qs = @()
    if ($UseBootstrapKey) { $qs += "useBootstrapKey=true" }
    if ($AllowStale) { $qs += "allowStale=true" }
    $qs += "includeReportDesc=true"
    $query = if ($qs.Count -gt 0) { "?" + ($qs -join "&") } else { "" }
    $devices = Invoke-Json "GET" "$BaseUrl/devices$query"
    if (Test-Ok "devices" ($devices.ok -eq $true)) { $passed++ } else { $failed++ }
} catch {
    Test-Ok "devices" $false $_.Exception.Message | Out-Null
    $failed++
}

if ($devices -eq $null -or $devices.ok -ne $true) {
    try {
        $last = Invoke-Json "GET" "$BaseUrl/devices/last?includeReportDesc=true"
        $devices = $last
        if (Test-Ok "devices/last" ($last.ok -eq $true)) { $passed++ } else { $failed++ }
    } catch {
        Test-Ok "devices/last" $false $_.Exception.Message | Out-Null
        $failed++
    }
}

$itfMouse = $MouseItf
$itfKeyboard = $KeyboardItf

if ($devices -ne $null -and $devices.ok -eq $true) {
    $interfaces = $devices.list.interfaces
    $mouse = $interfaces | Where-Object { $_.typeName -eq "mouse" } | Select-Object -First 1
    $keyboard = $interfaces | Where-Object { $_.typeName -eq "keyboard" } | Select-Object -First 1
    if ($mouse) { $itfMouse = [byte]$mouse.itf }
    if ($keyboard) { $itfKeyboard = [byte]$keyboard.itf }
    Write-Host ("mouse itf: {0}" -f $itfMouse)
    Write-Host ("keyboard itf: {0}" -f $itfKeyboard)
}

try {
    $layout = Invoke-Json "GET" "$BaseUrl/keyboard/layout?itf=$itfKeyboard"
    if (Test-Ok "keyboard/layout" ($layout.ok -eq $true)) { $passed++ } else { $failed++ }
} catch {
    $msg = $_.Exception.Message
    if ($msg -match "404") {
        Write-Host "[WARN] keyboard/layout not available"
    } else {
        Test-Ok "keyboard/layout" $false $msg | Out-Null
        $failed++
    }
}

if (-not $SkipInject) {
    try {
        $body = @{ dx = 1; dy = 0; itfSel = $itfMouse } | ConvertTo-Json
        $res = Invoke-Json "POST" "$BaseUrl/mouse/move" $body
        if (Test-Ok "mouse/move" ($res.ok -eq $true)) { $passed++ } else { $failed++ }
    } catch {
        Test-Ok "mouse/move" $false $_.Exception.Message | Out-Null
        $failed++
    }

    try {
        $body = @{ delta = 1; itfSel = $itfMouse } | ConvertTo-Json
        $res = Invoke-Json "POST" "$BaseUrl/mouse/wheel" $body
        if (Test-Ok "mouse/wheel" ($res.ok -eq $true)) { $passed++ } else { $failed++ }
    } catch {
        Test-Ok "mouse/wheel" $false $_.Exception.Message | Out-Null
        $failed++
    }

    try {
        $body = @{ buttonsMask = 1; itfSel = $itfMouse } | ConvertTo-Json
        $res = Invoke-Json "POST" "$BaseUrl/mouse/buttons" $body
        $body2 = @{ buttonsMask = 0; itfSel = $itfMouse } | ConvertTo-Json
        $res2 = Invoke-Json "POST" "$BaseUrl/mouse/buttons" $body2
        $ok = ($res.ok -eq $true) -and ($res2.ok -eq $true)
        if (Test-Ok "mouse/buttons" $ok) { $passed++ } else { $failed++ }
    } catch {
        Test-Ok "mouse/buttons" $false $_.Exception.Message | Out-Null
        $failed++
    }

    try {
        $body = @{ modifiers = 0; keys = @(4); itfSel = $itfKeyboard; applyMapping = $false } | ConvertTo-Json
        $res = Invoke-Json "POST" "$BaseUrl/keyboard/report" $body
        if (Test-Ok "keyboard/report" ($res.ok -eq $true)) { $passed++ } else { $failed++ }
    } catch {
        Test-Ok "keyboard/report" $false $_.Exception.Message | Out-Null
        $failed++
    }

    try {
        $body = @{ itfSel = $itfKeyboard } | ConvertTo-Json
        $res = Invoke-Json "POST" "$BaseUrl/keyboard/reset" $body
        if (Test-Ok "keyboard/reset" ($res.ok -eq $true)) { $passed++ } else { $failed++ }
    } catch {
        Test-Ok "keyboard/reset" $false $_.Exception.Message | Out-Null
        $failed++
    }
}
else {
    Write-Host "[SKIP] inject tests"
}

try {
    $state = Invoke-Json "GET" "$BaseUrl/keyboard/state"
    if (Test-Ok "keyboard/state" ($state.ok -eq $true)) { $passed++ } else { $failed++ }
} catch {
    Test-Ok "keyboard/state" $false $_.Exception.Message | Out-Null
    $failed++
}

Write-Host ""
Write-Host "passed: $passed"
Write-Host "failed: $failed"

if ($failed -gt 0) { exit 1 }
exit 0
