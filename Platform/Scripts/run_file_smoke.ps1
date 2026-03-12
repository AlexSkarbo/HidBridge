param(
    [string]$Configuration = "Debug",
    [string]$BaseUrl = "http://127.0.0.1:18093",
    [string]$DataRoot = "",
    [string]$AccessToken = "",
    [switch]$EnableApiAuth,
    [string]$AuthAuthority = "",
    [string]$AuthAudience = "",
    [string]$TokenClientId = "controlplane-smoke",
    [string]$TokenClientSecret = "",
    [string]$TokenUsername = "operator.smoke.admin",
    [string]$TokenPassword = "ChangeMe123!",
    [string]$ViewerTokenUsername = "operator.smoke.viewer",
    [string]$ViewerTokenPassword = "ChangeMe123!",
    [string]$ForeignTokenUsername = "operator.smoke.foreign",
    [string]$ForeignTokenPassword = "ChangeMe123!",
    [switch]$DisableHeaderFallback,
    [switch]$BearerOnly,
    [switch]$NoBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$scriptPath = Join-Path $PSScriptRoot "run_backend_smoke.ps1"

# File smoke uses an isolated data root by default, so each run is deterministic.
& $scriptPath `
    -Provider "File" `
    -Configuration $Configuration `
    -BaseUrl $BaseUrl `
    -DataRoot $DataRoot `
    -AccessToken $AccessToken `
    -EnableApiAuth:$EnableApiAuth `
    -AuthAuthority $AuthAuthority `
    -AuthAudience $AuthAudience `
    -TokenClientId $TokenClientId `
    -TokenClientSecret $TokenClientSecret `
    -TokenUsername $TokenUsername `
    -TokenPassword $TokenPassword `
    -ViewerTokenUsername $ViewerTokenUsername `
    -ViewerTokenPassword $ViewerTokenPassword `
    -ForeignTokenUsername $ForeignTokenUsername `
    -ForeignTokenPassword $ForeignTokenPassword `
    -DisableHeaderFallback:$DisableHeaderFallback `
    -BearerOnly:$BearerOnly `
    -NoBuild:$NoBuild

exit $LASTEXITCODE
