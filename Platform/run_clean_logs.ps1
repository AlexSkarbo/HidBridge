param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$ForwardArgs
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$scriptPath = Join-Path $PSScriptRoot "Scripts/run_clean_logs.ps1"
if (-not (Test-Path $scriptPath)) {
    throw "Launcher target not found: $scriptPath"
}

$pwsh = Get-Command powershell.exe -ErrorAction SilentlyContinue
if ($null -eq $pwsh) {
    throw "powershell.exe not found in PATH"
}

& $pwsh.Source -NoProfile -ExecutionPolicy Bypass -File $scriptPath @ForwardArgs
exit $LASTEXITCODE
