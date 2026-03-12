param(
    [ValidateSet("File", "Sql")]
    [string]$Provider = "File",
    [string]$Configuration = "Debug",
    [string]$ConnectionString = "Host=127.0.0.1;Port=5434;Database=hidbridge;Username=hidbridge;Password=hidbridge",
    [string]$Schema = "hidbridge",
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

if ($Provider -eq "Sql") {
    $scriptPath = Join-Path $PSScriptRoot "run_sql_smoke.ps1"

    & $scriptPath `
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
}
else {
    $scriptPath = Join-Path $PSScriptRoot "run_file_smoke.ps1"

    & $scriptPath `
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
}

exit $LASTEXITCODE
