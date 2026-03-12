param(
    [string]$OutputRoot = "",
    [switch]$IncludeSmokeData,
    [switch]$IncludeBackups
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$scriptsRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$platformRoot = Split-Path -Parent $scriptsRoot
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $platformRoot ("Artifacts/{0}" -f [DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmss'))
}

New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null

$copied = @()
$targets = @(
    @{ Source = Join-Path $platformRoot ".logs"; Destination = Join-Path $OutputRoot ".logs" }
)
if ($IncludeSmokeData) {
    $targets += @{ Source = Join-Path $platformRoot ".smoke-data"; Destination = Join-Path $OutputRoot ".smoke-data" }
}
if ($IncludeBackups) {
    $targets += @{ Source = Join-Path $platformRoot "Identity/Keycloak/backups"; Destination = Join-Path $OutputRoot "keycloak-backups" }
}

foreach ($target in $targets) {
    if (-not (Test-Path $target.Source)) { continue }
    Copy-Item -Path $target.Source -Destination $target.Destination -Recurse -Force
    $copied += $target.Destination
}

Write-Host "=== Artifact Export Summary ==="
Write-Host "OutputRoot: $OutputRoot"
Write-Host "CopiedCount: $($copied.Count)"
$copied | ForEach-Object { Write-Host $_ }
exit 0
