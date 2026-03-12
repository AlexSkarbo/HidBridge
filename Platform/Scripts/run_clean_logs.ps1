param(
    [int]$KeepDays = 7,
    [switch]$IncludeSmokeData,
    [switch]$WhatIf
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$scriptsRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$platformRoot = Split-Path -Parent $scriptsRoot
$targets = @(
    Join-Path $platformRoot ".logs"
)
if ($IncludeSmokeData) {
    $targets += Join-Path $platformRoot ".smoke-data"
}

$cutoff = [DateTimeOffset]::UtcNow.AddDays(-1 * [Math]::Abs($KeepDays))
$removed = @()

foreach ($target in $targets) {
    if (-not (Test-Path $target)) { continue }
    $items = Get-ChildItem -Path $target -Directory -Recurse -ErrorAction SilentlyContinue | Sort-Object FullName -Descending
    foreach ($item in $items) {
        $lastWrite = [DateTimeOffset]$item.LastWriteTimeUtc
        if ($lastWrite -gt $cutoff) { continue }
        $removed += $item.FullName
        if (-not $WhatIf) {
            Remove-Item -Path $item.FullName -Recurse -Force -ErrorAction Stop
        }
    }
}

Write-Host "=== Cleanup Summary ==="
Write-Host "KeepDays: $KeepDays"
Write-Host "IncludeSmokeData: $($IncludeSmokeData.IsPresent)"
Write-Host "RemovedCount: $($removed.Count)"
if ($removed.Count -gt 0) {
    Write-Host "Removed paths:"
    $removed | ForEach-Object { Write-Host $_ }
}
exit 0
