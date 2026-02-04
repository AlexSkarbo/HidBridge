using System.Text.Json;
using System.Linq;
using HidControl.ClientSdk;
using System.Net.WebSockets;

var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();
app.UseWebSockets();

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

app.Map("/ws/webrtc", async ctx =>
{
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        await ctx.Response.WriteAsync("expected websocket request", ctx.RequestAborted);
        return;
    }

    using WebSocket browserWs = await ctx.WebSockets.AcceptWebSocketAsync();

    var serverHttpBase = new Uri(serverUrl);
    Uri serverWsUri = ToWsUri(serverHttpBase, "/ws/webrtc");

    using var serverWs = new ClientWebSocket();
    if (!string.IsNullOrWhiteSpace(token))
    {
        serverWs.Options.SetRequestHeader("X-HID-Token", token!);
    }

    await serverWs.ConnectAsync(serverWsUri, ctx.RequestAborted);

    var toServer = PumpAsync(browserWs, serverWs, ctx.RequestAborted);
    var toBrowser = PumpAsync(serverWs, browserWs, ctx.RequestAborted);

    await Task.WhenAny(toServer, toBrowser);
    try { await browserWs.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing", CancellationToken.None); } catch { }
    try { await serverWs.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing", CancellationToken.None); } catch { }
});

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
    <h3>Shortcut Capture (Browser)</h3>
    <div class="row muted">
      Some OS-reserved shortcuts (like <code>Alt+Tab</code>, <code>Win+R</code>) may not be capturable in a browser. Use the buttons/manual chord above.
    </div>
    <div class="row">
      <input id="capture" style="min-width: 320px" placeholder="click here and press keys (Ctrl+Shift+X)" />
      <input id="capMods" style="width: 120px" value="0" />
      <input id="capKeys" style="min-width: 260px" value="[]" />
      <input id="capHoldMs" type="number" value="80" style="width: 90px" />
      <button id="sendCaptured">Send Captured</button>
    </div>
  </div>

  <div class="card">
    <h3>Keyboard</h3>
    <div class="row">
      <input id="text" style="min-width: 260px" placeholder="text (ASCII)" />
      <select id="textLayout">
        <option value="ascii" selected>ASCII</option>
        <option value="uk">UA (layout-dependent)</option>
        <option value="ru">RU (layout-dependent)</option>
      </select>
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

  <div class="card">
    <h3>WebRTC Signaling Demo (DataChannel)</h3>
    <div class="row muted">
      This uses a minimal signaling relay: browser &harr; <code>HidControl.Web</code> &harr; <code>HidControlServer</code> (<code>/ws/webrtc</code>).
      Open this page in <b>two tabs</b>, use the same room, then click <b>Call</b> in one tab.
    </div>
    <div class="row">
      <input id="rtcRoom" style="min-width: 220px" value="demo" />
      <button id="rtcJoin">Join</button>
      <button id="rtcCall">Call</button>
      <button id="rtcHangup">Hangup</button>
    </div>
    <div class="row">
      <input id="rtcIce" style="min-width: 420px" placeholder='ICE servers JSON, e.g. [{"urls":"stun:stun.l.google.com:19302"}]' />
    </div>
    <div class="row">
      <input id="rtcSend" style="min-width: 320px" placeholder="message over data channel" />
      <button id="rtcSendBtn">Send</button>
    </div>
    <div class="row muted" id="rtcStatus">disconnected</div>
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
      const layout = document.getElementById("textLayout").value;
      await post("/api/keyboard/text", { text, layout });
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

    // Basic `KeyboardEvent.code` -> USB HID Keyboard usage mapping.
    // Values are decimal usages (0..255).
    const codeToUsage = {
      "KeyA": 4, "KeyB": 5, "KeyC": 6, "KeyD": 7, "KeyE": 8, "KeyF": 9, "KeyG": 10, "KeyH": 11, "KeyI": 12, "KeyJ": 13, "KeyK": 14, "KeyL": 15,
      "KeyM": 16, "KeyN": 17, "KeyO": 18, "KeyP": 19, "KeyQ": 20, "KeyR": 21, "KeyS": 22, "KeyT": 23, "KeyU": 24, "KeyV": 25, "KeyW": 26, "KeyX": 27,
      "KeyY": 28, "KeyZ": 29,
      "Digit1": 30, "Digit2": 31, "Digit3": 32, "Digit4": 33, "Digit5": 34, "Digit6": 35, "Digit7": 36, "Digit8": 37, "Digit9": 38, "Digit0": 39,
      "Enter": 40, "Escape": 41, "Backspace": 42, "Tab": 43, "Space": 44,
      "Minus": 45, "Equal": 46, "BracketLeft": 47, "BracketRight": 48, "Backslash": 49,
      "Semicolon": 51, "Quote": 52, "Backquote": 53, "Comma": 54, "Period": 55, "Slash": 56,
      "CapsLock": 57,
      "F1": 58, "F2": 59, "F3": 60, "F4": 61, "F5": 62, "F6": 63, "F7": 64, "F8": 65, "F9": 66, "F10": 67, "F11": 68, "F12": 69,
      "PrintScreen": 70, "ScrollLock": 71, "Pause": 72,
      "Insert": 73, "Home": 74, "PageUp": 75, "Delete": 76, "End": 77, "PageDown": 78,
      "ArrowRight": 79, "ArrowLeft": 80, "ArrowDown": 81, "ArrowUp": 82,
      "NumLock": 83,
      "NumpadDivide": 84, "NumpadMultiply": 85, "NumpadSubtract": 86, "NumpadAdd": 87, "NumpadEnter": 88,
      "Numpad1": 89, "Numpad2": 90, "Numpad3": 91, "Numpad4": 92, "Numpad5": 93, "Numpad6": 94, "Numpad7": 95, "Numpad8": 96, "Numpad9": 97, "Numpad0": 98,
      "NumpadDecimal": 99,
      "ContextMenu": 101
    };

    function modsFromEvent(e) {
      // Boot modifier mask: Ctrl=0x01, Shift=0x02, Alt=0x04, Win/Meta=0x08 (left variants).
      return (e.ctrlKey ? 1 : 0) | (e.shiftKey ? 2 : 0) | (e.altKey ? 4 : 0) | (e.metaKey ? 8 : 0);
    }

    const pressedCodes = new Set();
    let lastCaptured = { mods: 0, keys: [] };

    function recomputeCaptured(e) {
      const mods = modsFromEvent(e);
      const keys = [];
      for (const code of pressedCodes.values()) {
        const usage = codeToUsage[code];
        if (usage === undefined) continue;
        // Ignore pure modifiers (we only use the modifier mask).
        if (usage >= 224 && usage <= 231) continue;
        if (!keys.includes(usage)) keys.push(usage);
        if (keys.length >= 6) break;
      }
      lastCaptured = { mods, keys };
      document.getElementById("capMods").value = String(mods);
      document.getElementById("capKeys").value = JSON.stringify(keys);
    }

    const capture = document.getElementById("capture");
    capture.addEventListener("keydown", (e) => {
      e.preventDefault();
      e.stopPropagation();
      if (!e.repeat) pressedCodes.add(e.code);
      recomputeCaptured(e);
    });
    capture.addEventListener("keyup", (e) => {
      e.preventDefault();
      e.stopPropagation();
      pressedCodes.delete(e.code);
      recomputeCaptured(e);
    });
    capture.addEventListener("blur", () => {
      pressedCodes.clear();
    });

    document.getElementById("sendCaptured").onclick = async () => {
      const holdMs = Number.parseInt(document.getElementById("capHoldMs").value, 10) || 80;
      await post("/api/keyboard/chord", { mods: lastCaptured.mods, keys: lastCaptured.keys, holdMs });
    };

    // --- WebRTC signaling demo ---
    let sig = null;
    let pc = null;
    let dc = null;
    let pendingCandidates = [];
    const rtcStatus = document.getElementById("rtcStatus");
    const PeerConnectionCtor = window.RTCPeerConnection || window.webkitRTCPeerConnection || window.mozRTCPeerConnection;
    let rtcDebugSeq = 0;

    function rtcLog(kind, payload) {
      rtcDebugSeq++;
      show({ webrtc: kind, seq: rtcDebugSeq, payload });
    }

    function setRtcStatus(s) { rtcStatus.textContent = s; }

    function getIceServers() {
      const t = document.getElementById("rtcIce").value.trim();
      if (!t) return [];
      try { return JSON.parse(t); } catch { return []; }
    }

    function ensureSig() {
      if (sig && (sig.readyState === WebSocket.OPEN || sig.readyState === WebSocket.CONNECTING)) return sig;

      const proto = location.protocol === "https:" ? "wss://" : "ws://";
      sig = new WebSocket(proto + location.host + "/ws/webrtc");
      sig.onopen = () => setRtcStatus("signaling: open");
      sig.onclose = () => setRtcStatus("signaling: closed");
      sig.onerror = () => setRtcStatus("signaling: error");
      sig.onmessage = async (ev) => {
        let msg = null;
        try { msg = JSON.parse(ev.data); } catch { return; }
        if (!msg || !msg.type) return;

        if (msg.type === "webrtc.hello") {
          console.log("webrtc hello", msg);
          rtcLog("hello", msg);
          return;
        }
        if (msg.type === "webrtc.joined" || msg.type === "webrtc.peer_joined") {
          console.log("webrtc room", msg);
          rtcLog("room", msg);
          return;
        }

        if (msg.type === "webrtc.signal" && msg.data) {
          rtcLog("recv", msg);
          const data = msg.data;
          if (!pc) await ensurePc();

          if (data.kind === "offer") {
            await pc.setRemoteDescription(data.sdp);
            // Apply any ICE candidates that arrived before the offer.
            for (const c of pendingCandidates) {
              try { await pc.addIceCandidate(c); } catch { /* ignore */ }
            }
            pendingCandidates = [];
            const answer = await pc.createAnswer();
            await pc.setLocalDescription(answer);
            await sigSend({ type: "signal", room: document.getElementById("rtcRoom").value, data: { kind: "answer", sdp: { type: pc.localDescription.type, sdp: pc.localDescription.sdp } } });
            return;
          }
          if (data.kind === "answer") {
            await pc.setRemoteDescription(data.sdp);
            // Apply any ICE candidates that arrived before the answer.
            for (const c of pendingCandidates) {
              try { await pc.addIceCandidate(c); } catch { /* ignore */ }
            }
            pendingCandidates = [];
            return;
          }
          if (data.kind === "candidate") {
            if (data.candidate) {
              // Some browsers deliver candidates before remote description is set.
              if (!pc.remoteDescription || !pc.remoteDescription.type) {
                pendingCandidates.push(data.candidate);
              } else {
                try { await pc.addIceCandidate(data.candidate); } catch { /* ignore */ }
              }
            }
            return;
          }
        }
      };
      return sig;
    }

    async function waitSigOpen() {
      const s = ensureSig();
      if (s.readyState === WebSocket.OPEN) return;
      if (s.readyState !== WebSocket.CONNECTING) throw new Error("signaling_not_open");
      await new Promise((resolve, reject) => {
        const onOpen = () => { cleanup(); resolve(); };
        const onErr = () => { cleanup(); reject(new Error("signaling_error")); };
        const onClose = () => { cleanup(); reject(new Error("signaling_closed")); };
        function cleanup() {
          s.removeEventListener("open", onOpen);
          s.removeEventListener("error", onErr);
          s.removeEventListener("close", onClose);
        }
        s.addEventListener("open", onOpen);
        s.addEventListener("error", onErr);
        s.addEventListener("close", onClose);
      });
    }

    async function sigSend(obj) {
      await waitSigOpen();
      rtcLog("send", obj);
      sig.send(JSON.stringify(obj));
    }

    async function ensurePc() {
      if (pc) return pc;
      if (typeof PeerConnectionCtor !== "function") {
        const detail = {
          ok: false,
          error: "webrtc_not_supported",
          rtcpType: typeof window.RTCPeerConnection,
          proto: location.protocol,
          ua: navigator.userAgent
        };
        setRtcStatus("WebRTC not supported in this browser/context");
        show(detail);
        throw new Error("webrtc_not_supported");
      }

      pc = new PeerConnectionCtor({ iceServers: getIceServers() });
      pc.onicecandidate = (e) => {
        if (!e.candidate) return;
        const cand = (typeof e.candidate.toJSON === "function") ? e.candidate.toJSON() : e.candidate;
        sigSend({ type: "signal", room: document.getElementById("rtcRoom").value, data: { kind: "candidate", candidate: cand } }).catch(() => {});
      };
      pc.onconnectionstatechange = () => setRtcStatus("pc: " + pc.connectionState);
      pc.oniceconnectionstatechange = () => console.log("ice:", pc.iceConnectionState);
      pc.onsignalingstatechange = () => console.log("signaling:", pc.signalingState);
      pc.onicegatheringstatechange = () => console.log("gathering:", pc.iceGatheringState);
      pc.ondatachannel = (e) => {
        dc = e.channel;
        wireDc();
      };
      return pc;
    }

    function wireDc() {
      if (!dc) return;
      dc.onopen = () => setRtcStatus("datachannel: open");
      dc.onclose = () => setRtcStatus("datachannel: closed");
      dc.onerror = () => setRtcStatus("datachannel: error");
      dc.onmessage = (e) => show({ webrtc: "message", data: e.data });
    }

    document.getElementById("rtcJoin").onclick = async () => {
      const room = document.getElementById("rtcRoom").value.trim() || "demo";
      await ensurePc();
      await sigSend({ type: "join", room });
      setRtcStatus("joined room: " + room);
    };

    document.getElementById("rtcCall").onclick = async () => {
      const room = document.getElementById("rtcRoom").value.trim() || "demo";
      const p = await ensurePc();
      await sigSend({ type: "join", room });

      dc = p.createDataChannel("data");
      wireDc();

      const offer = await p.createOffer();
      await p.setLocalDescription(offer);
      await sigSend({ type: "signal", room, data: { kind: "offer", sdp: { type: p.localDescription.type, sdp: p.localDescription.sdp } } });
      setRtcStatus("calling...");
    };

    document.getElementById("rtcHangup").onclick = async () => {
      try { if (sig && sig.readyState === WebSocket.OPEN) sig.send(JSON.stringify({ type: "leave" })); } catch {}
      try { if (dc) dc.close(); } catch {}
      try { if (pc) pc.close(); } catch {}
      try { if (sig) sig.close(); } catch {}
      sig = null; pc = null; dc = null;
      setRtcStatus("disconnected");
    };

    document.getElementById("rtcSendBtn").onclick = async () => {
      const text = document.getElementById("rtcSend").value;
      if (!dc || dc.readyState !== "open") {
        show({ ok: false, error: "datachannel_not_open" });
        return;
      }
      dc.send(text);
      show({ ok: true, sent: text });
    };
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
        ws.SendKeyboardTextAsync(req.Text ?? "", layout: req.Layout, itfSel: req.ItfSel, id: Guid.NewGuid().ToString("N"), ct: c), ct);

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

app.MapPost("/api/keyboard/chord", async (KeyboardChordRequest req, CancellationToken ct) =>
{
    byte mods = req.Mods ?? 0;
    int holdMs = req.HoldMs ?? 80;
    int[] keys = req.Keys ?? Array.Empty<int>();
    if (keys.Length > 6)
    {
        return Results.BadRequest(new { ok = false, error = "keys max length is 6" });
    }
    foreach (int k in keys)
    {
        if (k < 0 || k > 255)
        {
            return Results.BadRequest(new { ok = false, error = "keys must be 0..255" });
        }
    }

    Uri baseUri = new Uri(serverUrl);
    Uri wsUri = ToWsUri(baseUri, "/ws/hid");

    await using var ws = new HidControlWsClient();
    if (!string.IsNullOrWhiteSpace(token))
    {
        ws.SetRequestHeader("X-HID-Token", token!);
    }

    await ws.ConnectAsync(wsUri, ct);

    var keyBytes = keys.Select(k => (byte)k).ToArray();
    string downId = Guid.NewGuid().ToString("N");
    await ws.SendKeyboardReportAsync(mods, keyBytes, applyMapping: req.ApplyMapping ?? true, itfSel: req.ItfSel, id: downId, ct: ct);
    string downResp = await ws.ReceiveTextOnceAsync(ct) ?? "{\"ok\":false,\"error\":\"no_response\"}";

    if (holdMs > 0)
    {
        await Task.Delay(holdMs, ct);
    }

    string upId = Guid.NewGuid().ToString("N");
    await ws.SendKeyboardReportAsync(0, Array.Empty<byte>(), applyMapping: req.ApplyMapping ?? true, itfSel: req.ItfSel, id: upId, ct: ct);
    string upResp = await ws.ReceiveTextOnceAsync(ct) ?? "{\"ok\":false,\"error\":\"no_response\"}";

    await ws.CloseAsync(ct);

    return Results.Json(new
    {
        down = ParseJsonOrString(downResp),
        up = ParseJsonOrString(upResp)
    });
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

static async Task PumpAsync(WebSocket src, WebSocket dst, CancellationToken ct)
{
    var buffer = new byte[64 * 1024];
    while (!ct.IsCancellationRequested && src.State == WebSocketState.Open && dst.State == WebSocketState.Open)
    {
        WebSocketReceiveResult result;
        try
        {
            result = await src.ReceiveAsync(buffer, ct);
        }
        catch
        {
            break;
        }

        if (result.MessageType == WebSocketMessageType.Close)
        {
            break;
        }

        if (result.Count > 0)
        {
            await dst.SendAsync(new ArraySegment<byte>(buffer, 0, result.Count), result.MessageType, result.EndOfMessage, ct);
        }
    }
}

internal sealed record KeyboardShortcutRequest(string Shortcut, int? HoldMs = null, byte? ItfSel = null, bool? ApplyMapping = null);
internal sealed record KeyboardTextRequest(string? Text, string? Layout = null, byte? ItfSel = null);
internal sealed record KeyboardPressApiRequest(string? Usage, string? Mods, byte? ItfSel = null);
internal sealed record KeyboardChordRequest(byte? Mods, int[]? Keys, int? HoldMs = null, byte? ItfSel = null, bool? ApplyMapping = null);
internal sealed record MouseMoveApiRequest(int? Dx, int? Dy, byte? ItfSel = null);
internal sealed record MouseClickApiRequest(string? Button, byte? ItfSel = null);
