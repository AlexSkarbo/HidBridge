param(
    [string]$InstallDir = (Join-Path $PSScriptRoot "..\\.deps"),
    [switch]$UpdateConfig = $true,
    [string]$ConfigPath = (Join-Path $PSScriptRoot "..\\hidcontrol.config.json")
)

$ErrorActionPreference = "Stop"

function New-CleanDir([string]$Path) {
    if (Test-Path $Path) {
        Remove-Item -Recurse -Force $Path
    }
    New-Item -ItemType Directory -Force -Path $Path | Out-Null
}

function Get-Json([string]$Url) {
    $headers = @{ "User-Agent" = "HidControlServer" }
    return Invoke-RestMethod -Uri $Url -Headers $headers
}

function Download-File([string]$Url, [string]$OutPath) {
    Write-Host "download: $Url"
    Invoke-WebRequest -Uri $Url -OutFile $OutPath
}

function Find-Exe([string]$Root, [string]$Name) {
    $item = Get-ChildItem -Path $Root -Recurse -Filter $Name -File | Select-Object -First 1
    if ($null -eq $item) {
        throw "not found: $Name in $Root"
    }
    return $item.FullName
}

$installRoot = Resolve-Path -Path $InstallDir -ErrorAction SilentlyContinue
if ($null -eq $installRoot) {
    $installRoot = New-Item -ItemType Directory -Force -Path $InstallDir
}
$installRoot = $installRoot.ToString()

$tmpDir = Join-Path $installRoot "tmp"
New-CleanDir $tmpDir

Write-Host "install dir: $installRoot"

# FFmpeg (Windows)
$ffmpegUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip"
$ffmpegZip = Join-Path $tmpDir "ffmpeg.zip"
Download-File $ffmpegUrl $ffmpegZip

$ffmpegDir = Join-Path $installRoot "ffmpeg"
New-CleanDir $ffmpegDir
Expand-Archive -Path $ffmpegZip -DestinationPath $ffmpegDir -Force
$ffmpegExe = Find-Exe $ffmpegDir "ffmpeg.exe"

Write-Host "ffmpeg:  $ffmpegExe"

if ($UpdateConfig) {
    if (-not (Test-Path $ConfigPath)) {
        throw "config not found: $ConfigPath"
    }
    $json = Get-Content -Raw -Path $ConfigPath | ConvertFrom-Json
    $json.ffmpegPath = $ffmpegExe
    $json | ConvertTo-Json -Depth 50 | Set-Content -Path $ConfigPath -Encoding UTF8
    Write-Host "config updated: $ConfigPath"
}

Write-Host "done"
