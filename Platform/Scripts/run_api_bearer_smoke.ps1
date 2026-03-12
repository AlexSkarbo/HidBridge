param(
    [string]$Configuration = "Debug",
    [string]$ConnectionString = "Host=127.0.0.1;Port=5434;Database=hidbridge;Username=hidbridge;Password=hidbridge",
    [string]$Schema = "hidbridge",
    [string]$BaseUrl = "http://127.0.0.1:18093",
    [string]$AccessToken = "<token>",
    [string]$AuthAuthority = "http://127.0.0.1:18096/realms/hidbridge-dev",
    [string]$AuthAudience = "",
    [string]$TokenClientId = "controlplane-smoke",
    [string]$TokenClientSecret = "",
    [string]$TokenScope = "openid profile email",
    [string]$TokenUsername = "operator.smoke.admin",
    [string]$TokenPassword = "ChangeMe123!",
    [string]$ViewerTokenUsername = "operator.smoke.viewer",
    [string]$ViewerTokenPassword = "ChangeMe123!",
    [string]$ForeignTokenUsername = "operator.smoke.foreign",
    [string]$ForeignTokenPassword = "ChangeMe123!",
    [switch]$NoBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$scriptPath = Join-Path $PSScriptRoot "run_backend_smoke.ps1"

& $scriptPath `
    -Provider "Sql" `
    -Configuration $Configuration `
    -ConnectionString $ConnectionString `
    -Schema $Schema `
    -BaseUrl $BaseUrl `
    -AccessToken $AccessToken `
    -EnableApiAuth `
    -AuthAuthority $AuthAuthority `
    -AuthAudience $AuthAudience `
    -TokenClientId $TokenClientId `
    -TokenClientSecret $TokenClientSecret `
    -TokenScope $TokenScope `
    -TokenUsername $TokenUsername `
    -TokenPassword $TokenPassword `
    -ViewerTokenUsername $ViewerTokenUsername `
    -ViewerTokenPassword $ViewerTokenPassword `
    -ForeignTokenUsername $ForeignTokenUsername `
    -ForeignTokenPassword $ForeignTokenPassword `
    -BearerOnly `
    -NoBuild:$NoBuild

exit $LASTEXITCODE
