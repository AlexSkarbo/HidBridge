Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function New-OperationalLogRoot {
    param(
        [string]$PlatformRoot,
        [string]$Category
    )

    $logRoot = Join-Path $PlatformRoot (".logs/{0}/{1}" -f $Category, [DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmss'))
    New-Item -ItemType Directory -Force -Path $logRoot | Out-Null
    return $logRoot
}

function New-OperationalResultList {
    return ,([System.Collections.ArrayList]::new())
}

function Add-OperationalResult {
    param(
        [System.Collections.IList]$Results,
        [string]$Name,
        [string]$Status,
        [double]$Seconds,
        [int]$ExitCode,
        [string]$LogPath
    )

    $Results.Add([pscustomobject]@{
        Name     = $Name
        Status   = $Status
        Seconds  = [Math]::Round($Seconds, 2)
        ExitCode = $ExitCode
        LogPath  = $LogPath
    }) | Out-Null
}

function Convert-OperationalParametersToHashtable {
    param(
        [hashtable]$Parameters
    )

    if ($null -eq $Parameters -or $Parameters.Count -eq 0) {
        return @{}
    }

    # Keep native values as-is for PowerShell splatting semantics.
    # Converting switches/bools to strings can break parameter binding.
    return $Parameters
}

function Invoke-LoggedCommand {
    param(
        [System.Collections.IList]$Results,
        [string]$Name,
        [string]$LogRoot,
        [string]$FilePath,
        [string[]]$Arguments,
        [string]$WorkingDirectory,
        [switch]$StopOnFailure
    )

    Write-Host ""
    Write-Host "=== $Name ==="
    $safeName = (($Name -replace '[^A-Za-z0-9._-]', '-') -replace '-+', '-').Trim('-')
    $logPath = Join-Path $LogRoot "$safeName.log"

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $exitCode = 0
    Push-Location $WorkingDirectory
    $exceptionText = $null
    try {
        $global:LASTEXITCODE = 0
        & $FilePath @Arguments *> $logPath
        $exitCode = if (Get-Variable LASTEXITCODE -Scope Global -ErrorAction SilentlyContinue) { $global:LASTEXITCODE } else { 0 }
    }
    catch {
        $exitCode = 1
        $exceptionText = $_.Exception.ToString()
        Add-Content -Path $logPath -Value $exceptionText
    }
    finally {
        Pop-Location
        $sw.Stop()
    }

    if ($exitCode -eq 0) {
        Add-OperationalResult -Results $Results -Name $Name -Status "PASS" -Seconds $sw.Elapsed.TotalSeconds -ExitCode $exitCode -LogPath $logPath
        Write-Host "PASS  $Name"
        Write-Host "Log:   $logPath"
        return
    }

    Add-OperationalResult -Results $Results -Name $Name -Status "FAIL" -Seconds $sw.Elapsed.TotalSeconds -ExitCode $exitCode -LogPath $logPath
    Write-Host "FAIL  $Name" -ForegroundColor Red
    Write-Host "Log:   $logPath"
    if ($StopOnFailure) {
        if (-not [string]::IsNullOrWhiteSpace($exceptionText)) {
            throw "$Name failed. See log: $logPath"
        }
        throw "$Name failed with exit code $exitCode"
    }
}

function Invoke-LoggedScript {
    param(
        [System.Collections.IList]$Results,
        [string]$Name,
        [string]$LogRoot,
        [string]$ScriptPath,
        [hashtable]$Parameters,
        [switch]$StopOnFailure
    )

    Write-Host ""
    Write-Host "=== $Name ==="
    $safeName = (($Name -replace '[^A-Za-z0-9._-]', '-') -replace '-+', '-').Trim('-')
    $logPath = Join-Path $LogRoot "$safeName.log"

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $exitCode = 0
    $exceptionText = $null
    try {
        $normalizedParameters = Convert-OperationalParametersToHashtable -Parameters $Parameters
        $global:LASTEXITCODE = 0
        & $ScriptPath @normalizedParameters *> $logPath
        $exitCode = if (Get-Variable LASTEXITCODE -Scope Global -ErrorAction SilentlyContinue) { $global:LASTEXITCODE } else { 0 }
    }
    catch {
        $exitCode = 1
        $exceptionText = $_.Exception.ToString()
        Add-Content -Path $logPath -Value $exceptionText
    }
    finally {
        $sw.Stop()
    }

    if ($exitCode -eq 0) {
        Add-OperationalResult -Results $Results -Name $Name -Status "PASS" -Seconds $sw.Elapsed.TotalSeconds -ExitCode $exitCode -LogPath $logPath
        Write-Host "PASS  $Name"
        Write-Host "Log:   $logPath"
        return
    }

    Add-OperationalResult -Results $Results -Name $Name -Status "FAIL" -Seconds $sw.Elapsed.TotalSeconds -ExitCode $exitCode -LogPath $logPath
    Write-Host "FAIL  $Name" -ForegroundColor Red
    Write-Host "Log:   $logPath"
    if ($StopOnFailure) {
        if (-not [string]::IsNullOrWhiteSpace($exceptionText)) {
            throw "$Name failed. See log: $logPath"
        }
        throw "$Name failed with exit code $exitCode"
    }
}

function Write-OperationalSummary {
    param(
        [System.Collections.IList]$Results,
        [string]$LogRoot,
        [string]$Header = "Summary"
    )

    Write-Host ""
    Write-Host "=== $Header ==="
    $Results | Select-Object Name, Status, Seconds, ExitCode | Format-Table -AutoSize

    Write-Host ""
    Write-Host "Logs root: $LogRoot"
    foreach ($result in $Results) {
        Write-Host "$($result.Name): $($result.LogPath)"
    }
}

function Test-TcpPort {
    param(
        [string]$TargetHost,
        [int]$TargetPort,
        [int]$TimeoutMs = 1500
    )

    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $async = $client.BeginConnect($TargetHost, $TargetPort, $null, $null)
        if (-not $async.AsyncWaitHandle.WaitOne($TimeoutMs, $false)) {
            $client.Close()
            return $false
        }

        $client.EndConnect($async)
        return $true
    }
    catch {
        return $false
    }
    finally {
        $client.Dispose()
    }
}

function Test-LegacyExp022Enabled {
    $value = [Environment]::GetEnvironmentVariable("HIDBRIDGE_ENABLE_LEGACY_EXP022", [EnvironmentVariableTarget]::Process)
    if ([string]::IsNullOrWhiteSpace($value)) {
        $value = [Environment]::GetEnvironmentVariable("HIDBRIDGE_ENABLE_LEGACY_EXP022", [EnvironmentVariableTarget]::User)
    }
    if ([string]::IsNullOrWhiteSpace($value)) {
        $value = [Environment]::GetEnvironmentVariable("HIDBRIDGE_ENABLE_LEGACY_EXP022", [EnvironmentVariableTarget]::Machine)
    }

    if ([string]::IsNullOrWhiteSpace($value)) {
        return $false
    }

    switch ($value.Trim().ToLowerInvariant()) {
        "1" { return $true }
        "true" { return $true }
        "yes" { return $true }
        "on" { return $true }
        default { return $false }
    }
}

function Assert-LegacyExp022Enabled {
    param(
        [string]$Context = "Legacy exp-022 compatibility mode"
    )

    if (-not (Test-LegacyExp022Enabled)) {
        throw "$Context is disabled. Set HIDBRIDGE_ENABLE_LEGACY_EXP022=true in your shell to run exp-022/controlws lab flows."
    }
}
