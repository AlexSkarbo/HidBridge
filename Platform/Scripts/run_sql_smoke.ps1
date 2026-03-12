param(
    [string]$Configuration = "Debug",
    [string]$ConnectionString = "Host=127.0.0.1;Port=5434;Database=hidbridge;Username=hidbridge;Password=hidbridge",
    [string]$Schema = "hidbridge",
    [string]$BaseUrl = "http://127.0.0.1:18093",
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

# Delegate to the generic backend smoke runner so SQL and file checks stay aligned.
& $scriptPath `
    -Provider "Sql" `
    -Configuration $Configuration `
    -ConnectionString $ConnectionString `
    -Schema $Schema `
    -BaseUrl $BaseUrl `
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
