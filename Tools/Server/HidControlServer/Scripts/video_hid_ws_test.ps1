param(
    [string]$BaseUrl = "http://127.0.0.1:8080",
    [string]$Token = "",
    [string]$SourceId = "cap-1"
)

$ErrorActionPreference = "Stop"

function Invoke-Api([string]$method, [string]$path, $body = $null) {
    $headers = @{}
    if ($Token) { $headers["X-HID-Token"] = $Token }
    $url = $BaseUrl + $path
    if ($body -ne $null) {
        return Invoke-RestMethod -Method $method -Uri $url -Headers $headers -ContentType "application/json" -Body $body
    }
    return Invoke-RestMethod -Method $method -Uri $url -Headers $headers
}

Write-Host "=== HidControlServer video+hid+ws test ==="
Write-Host "BaseUrl: $BaseUrl"
Write-Host "SourceId: $SourceId"

Write-Host ""
Write-Host "== health =="
Invoke-Api GET "/health" | ConvertTo-Json -Depth 6 | Out-Host

Write-Host ""
Write-Host "== config =="
Invoke-Api GET "/config" | ConvertTo-Json -Depth 6 | Out-Host

Write-Host ""
Write-Host "== devices (allowStale) =="
try {
    Invoke-Api GET "/devices?allowStale=true" | ConvertTo-Json -Depth 6 | Out-Host
} catch {
    Write-Host $_.Exception.Message
}

Write-Host ""
Write-Host "== video/test-capture =="
try {
    Invoke-Api POST ("/video/test-capture?id=" + $SourceId) | ConvertTo-Json -Depth 6 | Out-Host
} catch {
    Write-Host $_.Exception.Message
}

Write-Host ""
Write-Host "== video/snapshot =="
try {
    $headers = @{}
    if ($Token) { $headers["X-HID-Token"] = $Token }
    $snap = Invoke-WebRequest -Method GET -Uri ($BaseUrl + "/video/snapshot/" + $SourceId) -Headers $headers -TimeoutSec 6
    Write-Host ("snapshot bytes: " + $snap.RawContentLength)
} catch {
    Write-Host $_.Exception.Message
}

Write-Host ""
Write-Host "== video/mjpeg (first chunk) =="
try {
    $req = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::Get, "$BaseUrl/video/mjpeg/$SourceId?fps=5")
    if ($Token) {
        $req.Headers.Add("X-HID-Token", $Token)
    }
    $client = New-Object System.Net.Http.HttpClient
    $client.Timeout = [TimeSpan]::FromSeconds(6)
    $resp = $client.SendAsync($req).GetAwaiter().GetResult()
    $stream = $resp.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
    $buf = New-Object byte[] 4096
    $read = $stream.Read($buf, 0, $buf.Length)
    $txt = [System.Text.Encoding]::ASCII.GetString($buf, 0, $read)
    Write-Host $txt
    $stream.Dispose()
    $resp.Dispose()
    $client.Dispose()
} catch {
    Write-Host $_.Exception.Message
}

Write-Host ""
Write-Host "== WS HID test (ping) =="
try {
    $wsUrl = $BaseUrl.Replace("http://", "ws://").Replace("https://", "wss://") + "/ws/hid"
    if ($Token) { $wsUrl = $wsUrl + "?access_token=" + [System.Uri]::EscapeDataString($Token) }
    $ws = New-Object System.Net.WebSockets.ClientWebSocket
    $ws.ConnectAsync([Uri]$wsUrl, [Threading.CancellationToken]::None).GetAwaiter().GetResult()
    $msg = '{"type":"ping","id":"1"}'
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($msg)
    $ws.SendAsync([ArraySegment[byte]]$bytes, [System.Net.WebSockets.WebSocketMessageType]::Text, $true, [Threading.CancellationToken]::None).GetAwaiter().GetResult()
    $buf = New-Object byte[] 2048
    $res = $ws.ReceiveAsync([ArraySegment[byte]]$buf, [Threading.CancellationToken]::None).GetAwaiter().GetResult()
    $txt = [System.Text.Encoding]::UTF8.GetString($buf, 0, $res.Count)
    Write-Host $txt
    $ws.CloseAsync([System.Net.WebSockets.WebSocketCloseStatus]::NormalClosure, "ok", [Threading.CancellationToken]::None).GetAwaiter().GetResult()
} catch {
    Write-Host $_.Exception.Message
}
