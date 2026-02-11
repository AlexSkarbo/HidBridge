param(
  [string]$ServerUrl = $env:HIDBRIDGE_SERVER_URL,
  [string]$Token = $env:HIDBRIDGE_TOKEN,
  [string]$Room = $env:HIDBRIDGE_WEBRTC_ROOM,
  [string]$SourceMode = $env:HIDBRIDGE_VIDEO_SOURCE_MODE,
  [string]$QualityPreset = $env:HIDBRIDGE_VIDEO_QUALITY_PRESET,
  [string]$FfmpegArgs = $env:HIDBRIDGE_VIDEO_FFMPEG_ARGS,
  [string]$CaptureInput = $env:HIDBRIDGE_VIDEO_CAPTURE_INPUT
)

if ([string]::IsNullOrWhiteSpace($ServerUrl)) { $ServerUrl = "http://127.0.0.1:8080" }
if ([string]::IsNullOrWhiteSpace($Room)) { $Room = "video" }
if ([string]::IsNullOrWhiteSpace($SourceMode)) { $SourceMode = "testsrc" }
if ([string]::IsNullOrWhiteSpace($QualityPreset)) { $QualityPreset = "balanced" }

$env:HIDBRIDGE_SERVER_URL = $ServerUrl
$env:HIDBRIDGE_TOKEN = $Token
$env:HIDBRIDGE_WEBRTC_ROOM = $Room
$env:HIDBRIDGE_VIDEO_SOURCE_MODE = $SourceMode
$env:HIDBRIDGE_VIDEO_QUALITY_PRESET = $QualityPreset
if (-not [string]::IsNullOrWhiteSpace($FfmpegArgs)) { $env:HIDBRIDGE_VIDEO_FFMPEG_ARGS = $FfmpegArgs }
if (-not [string]::IsNullOrWhiteSpace($CaptureInput)) { $env:HIDBRIDGE_VIDEO_CAPTURE_INPUT = $CaptureInput }

Write-Host "HIDBRIDGE_SERVER_URL=$env:HIDBRIDGE_SERVER_URL"
Write-Host "HIDBRIDGE_WEBRTC_ROOM=$env:HIDBRIDGE_WEBRTC_ROOM"
Write-Host "HIDBRIDGE_VIDEO_SOURCE_MODE=$env:HIDBRIDGE_VIDEO_SOURCE_MODE"
Write-Host "HIDBRIDGE_VIDEO_QUALITY_PRESET=$env:HIDBRIDGE_VIDEO_QUALITY_PRESET"
if (-not [string]::IsNullOrWhiteSpace($env:HIDBRIDGE_HELPER_PIDFILE)) {
  Write-Host "HIDBRIDGE_HELPER_PIDFILE=$env:HIDBRIDGE_HELPER_PIDFILE"
}

$safeRoom = ($Room -replace '[^A-Za-z0-9_.-]', '_')
$logDir = Join-Path $PSScriptRoot "logs"
New-Item -ItemType Directory -Path $logDir -Force | Out-Null
$logPath = Join-Path $logDir ("videopeer_{0}.log" -f $safeRoom)
Write-Host "HIDBRIDGE_LOG_PATH=$logPath"

if (-not (Get-Command go -ErrorAction SilentlyContinue)) {
  Write-Error "go command not found in PATH"
  exit 127
}

$binDir = Join-Path $PSScriptRoot "bin"
New-Item -ItemType Directory -Path $binDir -Force | Out-Null
$exePath = Join-Path $binDir "webrtcvideopeer.exe"
$forceGoRun = ($env:HIDBRIDGE_HELPER_FORCE_GORUN -eq "1")
$needBuild = -not (Test-Path $exePath)
if (-not $needBuild) {
  $exeStamp = (Get-Item $exePath).LastWriteTimeUtc
  $srcChanged = Get-ChildItem -Path $PSScriptRoot -Filter "*.go" -File |
    Where-Object { $_.LastWriteTimeUtc -gt $exeStamp } |
    Select-Object -First 1
  if ($null -ne $srcChanged) { $needBuild = $true }
}

if (-not $forceGoRun) {
  if ($needBuild) {
    & go build -o $exePath . *>> $logPath
    if ($LASTEXITCODE -ne 0) {
      $code = $LASTEXITCODE
      if ($null -eq $code) { $code = if ($?) { 0 } else { 1 } }
      exit $code
    }
  }
  & $exePath *>> $logPath
} else {
  & go run . *>> $logPath
}

$code = $LASTEXITCODE
if ($null -eq $code) {
  # LASTEXITCODE can stay null for some PowerShell command failures.
  $code = if ($?) { 0 } else { 1 }
}
exit $code
