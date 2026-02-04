using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

string url = "ws://127.0.0.1:8080/control";
string token = "";
string targetId = "";
int dx = 10;
int dy = 0;
int wheel = 0;
bool doClick = true;
int batchCount = 0;
bool statusMode = false;

foreach (string arg in args)
{
    if (arg.StartsWith("--url=")) url = arg.Substring("--url=".Length);
    else if (arg.StartsWith("--token=")) token = arg.Substring("--token=".Length);
    else if (arg.StartsWith("--target=")) targetId = arg.Substring("--target=".Length);
    else if (arg.StartsWith("--dx=")) int.TryParse(arg.Substring("--dx=".Length), out dx);
    else if (arg.StartsWith("--dy=")) int.TryParse(arg.Substring("--dy=".Length), out dy);
    else if (arg.StartsWith("--wheel=")) int.TryParse(arg.Substring("--wheel=".Length), out wheel);
    else if (arg.StartsWith("--batch=")) int.TryParse(arg.Substring("--batch=".Length), out batchCount);
    else if (arg == "--status") statusMode = true;
    else if (arg == "--no-click") doClick = false;
}

using var ws = new ClientWebSocket();
if (!string.IsNullOrWhiteSpace(token))
{
    ws.Options.SetRequestHeader("X-HID-Token", token);
}

Console.WriteLine($"Connecting to {url}...");
await ws.ConnectAsync(new Uri(url), CancellationToken.None);

if (statusMode)
{
    Console.WriteLine("status mode: listening...");
    while (ws.State == WebSocketState.Open)
    {
        string? msg = await ReceiveTextAsync(ws);
        if (msg is null) break;
        Console.WriteLine(msg);
    }
    return;
}

await SendJsonAsync(ws, new
{
    t = "hello",
    v = 1,
    client = "hid-control-client",
    targets = string.IsNullOrWhiteSpace(targetId) ? Array.Empty<string>() : new[] { targetId }
});

string? welcome = await ReceiveTextAsync(ws);
if (welcome is not null)
{
    Console.WriteLine($"welcome: {welcome}");
}

int seq = 1;
if (batchCount > 0)
{
    var items = new List<object>(batchCount);
    for (int i = 0; i < batchCount; i++)
    {
        items.Add(new
        {
            seq = seq++,
            targetId = string.IsNullOrWhiteSpace(targetId) ? null : targetId,
            device = "mouse",
            payload = new { dx, dy, wheel = 0, buttons = 0 }
        });
    }
    await SendJsonAsync(ws, new { t = "batch", items });
    Console.WriteLine($"sent batch: {batchCount} moves");
}
else
{
    await SendJsonAsync(ws, new
    {
        t = "input",
        seq = seq++,
        targetId = string.IsNullOrWhiteSpace(targetId) ? null : targetId,
        device = "mouse",
        payload = new { dx, dy, wheel, buttons = 0 }
    });
    Console.WriteLine("sent mouse move");
}

if (doClick)
{
    await SendJsonAsync(ws, new
    {
        t = "input",
        seq = seq++,
        targetId = string.IsNullOrWhiteSpace(targetId) ? null : targetId,
        device = "mouse",
        payload = new { buttons = 1 }
    });
    await SendJsonAsync(ws, new
    {
        t = "input",
        seq = seq++,
        targetId = string.IsNullOrWhiteSpace(targetId) ? null : targetId,
        device = "mouse",
        payload = new { buttons = 0 }
    });
    Console.WriteLine("sent left click");
}

await SendJsonAsync(ws, new
{
    t = "input",
    seq = seq++,
    targetId = string.IsNullOrWhiteSpace(targetId) ? null : targetId,
    device = "keyboard",
    payload = new { type = "press", usage = 4 }
});
Console.WriteLine("sent key press");

Console.WriteLine("done");

static async Task SendJsonAsync(ClientWebSocket ws, object payload)
{
    byte[] data = JsonSerializer.SerializeToUtf8Bytes(payload);
    await ws.SendAsync(data, WebSocketMessageType.Text, true, CancellationToken.None);
}

static async Task<string?> ReceiveTextAsync(ClientWebSocket ws)
{
    var buffer = new byte[8192];
    using var ms = new MemoryStream();
    while (true)
    {
        WebSocketReceiveResult result = await ws.ReceiveAsync(buffer, CancellationToken.None);
        if (result.MessageType == WebSocketMessageType.Close)
        {
            return null;
        }
        if (result.Count > 0)
        {
            ms.Write(buffer, 0, result.Count);
        }
        if (result.EndOfMessage)
        {
            break;
        }
    }
    return Encoding.UTF8.GetString(ms.ToArray());
}
