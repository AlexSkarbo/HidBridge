param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$ForwardArgs
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$scriptPath = Join-Path $PSScriptRoot "Identity/Keycloak/Reset-HidBridgeDevRealm.ps1"
if (-not (Test-Path $scriptPath)) {
    throw "Launcher target not found: $scriptPath"
}

& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $scriptPath @ForwardArgs
exit $LASTEXITCODE
