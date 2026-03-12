param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$ForwardArgs
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$scriptPath = Join-Path (Join-Path $PSScriptRoot "Scripts") "run_token_debug.ps1"
& $scriptPath @ForwardArgs
exit $LASTEXITCODE
