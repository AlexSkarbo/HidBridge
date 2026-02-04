using System.Text.Json;
using HidControl.ClientSdk;

var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

string serverUrl = Environment.GetEnvironmentVariable("HIDBRIDGE_SERVER_URL") ?? "http://127.0.0.1:8080";
string? token = Environment.GetEnvironmentVariable("HIDBRIDGE_TOKEN");

static Uri ToWsUri(Uri httpBase, string path)
{
    var b = new UriBuilder(httpBase);
    b.Scheme = b.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws";
    b.Path = path.StartsWith("/") ? path : "/" + path;
    b.Query = "";
    b.Fragment = "";
    return b.Uri;
}

static async Task<string> SendWsOnceAsync(Uri serverHttpBase, string? token, Func<HidControlWsClient, CancellationToken, Task> send, CancellationToken ct)
{
    Uri wsUri = ToWsUri(serverHttpBase, "/ws/hid");
    await using var ws = new HidControlWsClient();
    if (!string.IsNullOrWhiteSpace(token))
    {
        ws.SetRequestHeader("X-HID-Token", token!);
    }

    await ws.ConnectAsync(wsUri, ct);
    await send(ws, ct);

    // Server responds with a JSON object with ok/type/id and details.
    string? resp = await ws.ReceiveTextOnceAsync(ct);
    await ws.CloseAsync(ct);
    return resp ?? "{\"ok\":false,\"error\":\"no_response\"}";
}

app.MapGet("/", () =>
{
    // Minimal UI to prove that the SDK can drive `/ws/hid`.
    return Results.Text($$"""
<!doctype html>
<html>
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>HidBridge Web Client (Skeleton)</title>
  <style>
    body { font-family: system-ui, -apple-system, Segoe UI, Roboto, Arial, sans-serif; margin: 18px; }
    input, button { font-size: 14px; padding: 8px 10px; }
    code { background: #f3f3f3; padding: 2px 6px; border-radius: 6px; }
    .row { margin: 10px 0; display: flex; gap: 8px; flex-wrap: wrap; align-items: center; }
    .card { border: 1px solid #e2e2e2; border-radius: 12px; padding: 12px; margin: 12px 0; }
    .card h3 { margin: 0 0 10px; font-size: 14px; }
    pre { background: #0b0d12; color: #d5d9e0; padding: 12px; border-radius: 10px; overflow: auto; }
    .muted { color: #666; font-size: 12px; }
  </style>
</head>
<body>
  <h2>HidBridge Web Client (Skeleton)</h2>
  <div class="row">
    <div>Server:</div>
    <code>{{serverUrl}}</code>
  </div>
  <div class="row muted">
    Token is read from <code>HIDBRIDGE_TOKEN</code> environment variable (server-side).
  </div>

  <div class="card">
    <h3>Shortcuts</h3>
    <div class="row">
      <button data-shortcut="Ctrl+C" class="quickShortcut">Ctrl+C</button>
      <button data-shortcut="Alt+Tab" class="quickShortcut">Alt+Tab</button>
      <button data-shortcut="Win+R" class="quickShortcut">Win+R</button>
      <button data-shortcut="Ctrl+Alt+Del" class="quickShortcut">Ctrl+Alt+Del</button>
    </div>
    <div class="row">
      <input id="shortcut" style="min-width: 260px" value="Ctrl+Alt+Del" />
      <input id="holdMs" type="number" value="80" style="width: 90px" />
      <button id="sendShortcut">Send</button>
    </div>
  </div>

  <div class="card">
    <h3>Keyboard</h3>
    <div class="row">
      <input id="text" style="min-width: 260px" placeholder="text (ASCII)" />
      <button id="sendText">Send Text</button>
    </div>
    <div class="row">
      <input id="usage" style="width: 140px" value="6" />
      <input id="mods" style="width: 140px" value="1" />
      <span class="muted">usage=6 (C), mods=1 (Ctrl) => Ctrl+C</span>
      <button id="sendPress">Send Press</button>
    </div>
  </div>

  <div class="card">
    <h3>Mouse</h3>
    <div class="row">
      <input id="dx" type="number" value="20" style="width: 120px" />
      <input id="dy" type="number" value="0" style="width: 120px" />
      <button id="sendMove">Move</button>
    </div>
    <div class="row">
      <button data-btn="left" class="click">Left click</button>
      <button data-btn="right" class="click">Right click</button>
      <button data-btn="middle" class="click">Middle click</button>
    </div>
  </div>

  <pre id="out">ready</pre>

  <script>
    const out = document.getElementById("out");

    function show(x) {
      out.textContent = typeof x === "string" ? x : JSON.stringify(x, null, 2);
    }

    async function post(path, payload) {
      out.textContent = "sending...";
      const res = await fetch(path, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(payload)
      });
      const text = await res.text();
      show(text);
    }

    for (const btn of document.querySelectorAll(".quickShortcut")) {
      btn.addEventListener("click", () => {
        document.getElementById("shortcut").value = btn.getAttribute("data-shortcut");
      });
    }

    document.getElementById("sendShortcut").onclick = async () => {
      const shortcut = document.getElementById("shortcut").value;
      const holdMs = Number.parseInt(document.getElementById("holdMs").value, 10) || 80;
      await post("/api/shortcut", { shortcut, holdMs });
    };

    document.getElementById("sendText").onclick = async () => {
      const text = document.getElementById("text").value;
      await post("/api/keyboard/text", { text });
    };

    document.getElementById("sendPress").onclick = async () => {
      const usage = document.getElementById("usage").value.trim();
      const mods = document.getElementById("mods").value.trim();
      await post("/api/keyboard/press", { usage, mods });
    };

    document.getElementById("sendMove").onclick = async () => {
      const dx = Number.parseInt(document.getElementById("dx").value, 10) || 0;
      const dy = Number.parseInt(document.getElementById("dy").value, 10) || 0;
      await post("/api/mouse/move", { dx, dy });
    };

    for (const btn of document.querySelectorAll(".click")) {
      btn.addEventListener("click", async () => {
        const button = btn.getAttribute("data-btn");
        await post("/api/mouse/click", { button });
      });
    }
  </script>
</body>
</html>
""", "text/html");
});

app.MapPost("/api/shortcut", async (KeyboardShortcutRequest req, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.Shortcut))
    {
        return Results.BadRequest(new { ok = false, error = "shortcut is required" });
    }

    Uri baseUri = new Uri(serverUrl);
    string resp = await SendWsOnceAsync(baseUri, token, (ws, c) =>
        ws.SendKeyboardShortcutAsync(req.Shortcut, itfSel: req.ItfSel, holdMs: req.HoldMs ?? 80, applyMapping: req.ApplyMapping ?? true, id: Guid.NewGuid().ToString("N"), ct: c), ct);

    return Results.Text(resp, "application/json");
});

app.MapPost("/api/keyboard/text", async (KeyboardTextRequest req, CancellationToken ct) =>
{
    Uri baseUri = new Uri(serverUrl);
    string resp = await SendWsOnceAsync(baseUri, token, (ws, c) =>
        ws.SendKeyboardTextAsync(req.Text ?? "", itfSel: req.ItfSel, id: Guid.NewGuid().ToString("N"), ct: c), ct);

    return Results.Text(resp, "application/json");
});

app.MapPost("/api/keyboard/press", async (KeyboardPressApiRequest req, CancellationToken ct) =>
{
    if (!TryParseByte(req.Usage, out byte usage))
    {
        return Results.BadRequest(new { ok = false, error = "usage must be 0..255 (decimal) or 0x.." });
    }
    if (!TryParseByte(req.Mods, out byte mods))
    {
        return Results.BadRequest(new { ok = false, error = "mods must be 0..255 (decimal) or 0x.." });
    }

    Uri baseUri = new Uri(serverUrl);
    string resp = await SendWsOnceAsync(baseUri, token, (ws, c) =>
        ws.SendKeyboardPressAsync(usage, mods: mods, itfSel: req.ItfSel, id: Guid.NewGuid().ToString("N"), ct: c), ct);

    return Results.Text(resp, "application/json");
});

app.MapPost("/api/mouse/move", async (MouseMoveApiRequest req, CancellationToken ct) =>
{
    Uri baseUri = new Uri(serverUrl);
    string resp = await SendWsOnceAsync(baseUri, token, (ws, c) =>
        ws.SendMouseMoveAsync(req.Dx ?? 0, req.Dy ?? 0, itfSel: req.ItfSel, id: Guid.NewGuid().ToString("N"), ct: c), ct);

    return Results.Text(resp, "application/json");
});

app.MapPost("/api/mouse/click", async (MouseClickApiRequest req, CancellationToken ct) =>
{
    string button = string.IsNullOrWhiteSpace(req.Button) ? "left" : req.Button!;
    Uri baseUri = new Uri(serverUrl);
    Uri wsUri = ToWsUri(baseUri, "/ws/hid");

    await using var ws = new HidControlWsClient();
    if (!string.IsNullOrWhiteSpace(token))
    {
        ws.SetRequestHeader("X-HID-Token", token!);
    }

    await ws.ConnectAsync(wsUri, ct);

    await ws.SendMouseButtonAsync(button, down: true, itfSel: req.ItfSel, id: Guid.NewGuid().ToString("N"), ct: ct);
    string downResp = await ws.ReceiveTextOnceAsync(ct) ?? "{\"ok\":false,\"error\":\"no_response\"}";

    await ws.SendMouseButtonAsync(button, down: false, itfSel: req.ItfSel, id: Guid.NewGuid().ToString("N"), ct: ct);
    string upResp = await ws.ReceiveTextOnceAsync(ct) ?? "{\"ok\":false,\"error\":\"no_response\"}";

    await ws.CloseAsync(ct);

    // Keep a structured response even though we send 2 WS messages.
    return Results.Json(new
    {
        down = ParseJsonOrString(downResp),
        up = ParseJsonOrString(upResp)
    });
});

// Touch the SDK so the project reference is exercised.
app.MapGet("/sdk/ping", () =>
{
    _ = new HidControlClient(new HttpClient(), new Uri("http://127.0.0.1:8080"));
    return Results.Ok(new { ok = true });
});

app.Run();

static bool TryParseByte(string? s, out byte value)
{
    value = 0;
    if (string.IsNullOrWhiteSpace(s)) return false;
    string t = s.Trim();
    if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
    {
        return byte.TryParse(t.AsSpan(2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out value);
    }
    return byte.TryParse(t, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out value);
}

internal sealed record KeyboardShortcutRequest(string Shortcut, int? HoldMs = null, byte? ItfSel = null, bool? ApplyMapping = null);
internal sealed record KeyboardTextRequest(string? Text, byte? ItfSel = null);
internal sealed record KeyboardPressApiRequest(string? Usage, string? Mods, byte? ItfSel = null);
internal sealed record MouseMoveApiRequest(int? Dx, int? Dy, byte? ItfSel = null);
internal sealed record MouseClickApiRequest(string? Button, byte? ItfSel = null);

static object ParseJsonOrString(string s)
{
    try
    {
        using var doc = JsonDocument.Parse(s);
        return doc.RootElement.Clone();
    }
    catch
    {
        return s;
    }
}
