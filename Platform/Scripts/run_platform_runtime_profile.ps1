param(
    [ValidateSet("up", "down", "restart", "status", "logs")]
    [string]$Action = "up",
    [string]$ComposeFile = "docker-compose.platform-runtime.yml",
    [switch]$Build,
    [switch]$Pull,
    [switch]$RemoveVolumes,
    [switch]$Follow,
    [switch]$SkipReadyWait,
    [int]$ReadyTimeoutSec = 180
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Resolve-ComposeFilePath {
    param(
        [string]$ScriptRoot,
        [string]$ComposeFilePath
    )

    if ([System.IO.Path]::IsPathRooted($ComposeFilePath)) {
        return (Resolve-Path $ComposeFilePath).Path
    }

    $repoRoot = (Resolve-Path (Join-Path $ScriptRoot "..\..")).Path
    $candidate = Join-Path $repoRoot $ComposeFilePath
    if (-not (Test-Path $candidate)) {
        throw "Compose file was not found: $candidate"
    }

    return (Resolve-Path $candidate).Path
}

function Invoke-Compose {
    param(
        [string]$ComposePath,
        [string[]]$ComposeArgs
    )

    $docker = Get-Command docker -ErrorAction SilentlyContinue
    if ($null -eq $docker) {
        throw "docker was not found in PATH. Install Docker Desktop/Engine before running platform-runtime."
    }

    & $docker.Source compose -f $ComposePath @ComposeArgs
    if ($LASTEXITCODE -ne 0) {
        throw "docker compose failed with exit code $LASTEXITCODE."
    }
}

function Test-HttpReady {
    param(
        [string]$Url
    )

    try {
        $response = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 5
        return ($response.StatusCode -ge 200 -and $response.StatusCode -lt 400)
    }
    catch {
        return $false
    }
}

function Wait-HttpReady {
    param(
        [string]$Name,
        [string]$Url,
        [int]$TimeoutSec
    )

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds([Math]::Max(5, $TimeoutSec))
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        if (Test-HttpReady -Url $Url) {
            return
        }

        Start-Sleep -Milliseconds 1000
    }

    throw "$Name did not become ready within $TimeoutSec sec: $Url"
}

function Print-RuntimeSummary {
    param(
        [string]$ComposePath
    )

    Write-Host ""
    Write-Host "=== Platform Runtime Summary ==="
    Write-Host "Compose file: $ComposePath"
    Write-Host "API:          http://127.0.0.1:18093"
    Write-Host "Web:          http://127.0.0.1:18110"
    Write-Host "Keycloak:     http://127.0.0.1:18096"
    Write-Host "Postgres:     127.0.0.1:5434"
    Write-Host "Identity DB:  127.0.0.1:5435"
    Write-Host ""
    Write-Host "Edge agent remains external to this compose profile."
}

$composePath = Resolve-ComposeFilePath -ScriptRoot $PSScriptRoot -ComposeFilePath $ComposeFile

switch ($Action) {
    "up" {
        if ($Pull) {
            Invoke-Compose -ComposePath $composePath -ComposeArgs @("pull")
        }

        $upArgs = @("up", "-d")
        if ($Build) {
            $upArgs += "--build"
        }

        Invoke-Compose -ComposePath $composePath -ComposeArgs $upArgs

        if (-not $SkipReadyWait) {
            Wait-HttpReady -Name "API health" -Url "http://127.0.0.1:18093/health" -TimeoutSec $ReadyTimeoutSec
            Wait-HttpReady -Name "Web shell" -Url "http://127.0.0.1:18110/" -TimeoutSec $ReadyTimeoutSec
            Wait-HttpReady -Name "Keycloak realm" -Url "http://127.0.0.1:18096/realms/hidbridge-dev" -TimeoutSec $ReadyTimeoutSec
        }

        Print-RuntimeSummary -ComposePath $composePath
        Invoke-Compose -ComposePath $composePath -ComposeArgs @("ps")
    }
    "down" {
        $downArgs = @("down")
        if ($RemoveVolumes) {
            $downArgs += "--volumes"
        }

        Invoke-Compose -ComposePath $composePath -ComposeArgs $downArgs
    }
    "restart" {
        $restartDownArgs = @("down")
        if ($RemoveVolumes) {
            $restartDownArgs += "--volumes"
        }

        Invoke-Compose -ComposePath $composePath -ComposeArgs $restartDownArgs

        $restartUpArgs = @("up", "-d")
        if ($Build) {
            $restartUpArgs += "--build"
        }

        Invoke-Compose -ComposePath $composePath -ComposeArgs $restartUpArgs

        if (-not $SkipReadyWait) {
            Wait-HttpReady -Name "API health" -Url "http://127.0.0.1:18093/health" -TimeoutSec $ReadyTimeoutSec
            Wait-HttpReady -Name "Web shell" -Url "http://127.0.0.1:18110/" -TimeoutSec $ReadyTimeoutSec
            Wait-HttpReady -Name "Keycloak realm" -Url "http://127.0.0.1:18096/realms/hidbridge-dev" -TimeoutSec $ReadyTimeoutSec
        }

        Print-RuntimeSummary -ComposePath $composePath
        Invoke-Compose -ComposePath $composePath -ComposeArgs @("ps")
    }
    "status" {
        Invoke-Compose -ComposePath $composePath -ComposeArgs @("ps")
    }
    "logs" {
        $logArgs = @("logs")
        if ($Follow) {
            $logArgs += "--follow"
        }

        Invoke-Compose -ComposePath $composePath -ComposeArgs $logArgs
    }
    default {
        throw "Unsupported action: $Action"
    }
}
