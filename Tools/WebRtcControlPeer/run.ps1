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

go run .

