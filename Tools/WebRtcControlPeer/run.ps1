param(
  [string]$ServerUrl = $env:HIDBRIDGE_SERVER_URL,
  [string]$Token = $env:HIDBRIDGE_TOKEN,
  [string]$Room = $env:HIDBRIDGE_WEBRTC_ROOM
)

if ([string]::IsNullOrWhiteSpace($ServerUrl)) { $ServerUrl = "http://127.0.0.1:8080" }
if ([string]::IsNullOrWhiteSpace($Room)) { $Room = "control" }

$env:HIDBRIDGE_SERVER_URL = $ServerUrl
$env:HIDBRIDGE_TOKEN = $Token
$env:HIDBRIDGE_WEBRTC_ROOM = $Room

Write-Host "HIDBRIDGE_SERVER_URL=$env:HIDBRIDGE_SERVER_URL"
Write-Host "HIDBRIDGE_WEBRTC_ROOM=$env:HIDBRIDGE_WEBRTC_ROOM"
if (-not [string]::IsNullOrWhiteSpace($env:HIDBRIDGE_HELPER_PIDFILE)) {
  Write-Host "HIDBRIDGE_HELPER_PIDFILE=$env:HIDBRIDGE_HELPER_PIDFILE"
}

if (-not (Get-Command go -ErrorAction SilentlyContinue)) {
  Write-Error "go command not found in PATH"
  exit 127
}

$binDir = Join-Path $PSScriptRoot "bin"
New-Item -ItemType Directory -Path $binDir -Force | Out-Null
$exePath = Join-Path $binDir "webrtccontrolpeer.exe"
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
    go build -o $exePath .
    if ($LASTEXITCODE -ne 0) {
      $code = $LASTEXITCODE
      if ($null -eq $code) { $code = if ($?) { 0 } else { 1 } }
      exit $code
    }
  }
  & $exePath
} else {
  go run .
}

$code = $LASTEXITCODE
if ($null -eq $code) {
  $code = if ($?) { 0 } else { 1 }
}
exit $code
