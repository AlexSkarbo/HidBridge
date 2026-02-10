param(
  [string]$ServerUrl = $env:HIDBRIDGE_SERVER_URL,
  [string]$Token = $env:HIDBRIDGE_TOKEN,
  [string]$Room = $env:HIDBRIDGE_WEBRTC_ROOM
)

if ([string]::IsNullOrWhiteSpace($ServerUrl)) { $ServerUrl = "http://127.0.0.1:8080" }
if ([string]::IsNullOrWhiteSpace($Room)) { $Room = "video" }

$env:HIDBRIDGE_SERVER_URL = $ServerUrl
$env:HIDBRIDGE_TOKEN = $Token
$env:HIDBRIDGE_WEBRTC_ROOM = $Room

Write-Host "HIDBRIDGE_SERVER_URL=$env:HIDBRIDGE_SERVER_URL"
Write-Host "HIDBRIDGE_WEBRTC_ROOM=$env:HIDBRIDGE_WEBRTC_ROOM"

$safeRoom = ($Room -replace '[^A-Za-z0-9_.-]', '_')
$logDir = Join-Path $PSScriptRoot "logs"
New-Item -ItemType Directory -Path $logDir -Force | Out-Null
$logPath = Join-Path $logDir ("videopeer_{0}.log" -f $safeRoom)
Write-Host "HIDBRIDGE_LOG_PATH=$logPath"

if (-not (Get-Command go -ErrorAction SilentlyContinue)) {
  Write-Error "go command not found in PATH"
  exit 127
}

& go run . *>> $logPath
$code = $LASTEXITCODE
if ($null -eq $code) {
  # LASTEXITCODE can stay null for some PowerShell command failures.
  $code = if ($?) { 0 } else { 1 }
}
exit $code
