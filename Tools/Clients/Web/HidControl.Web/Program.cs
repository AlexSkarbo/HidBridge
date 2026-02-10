using System.Text.Json;
using System.Linq;
using HidControl.ClientSdk;
using System.Net.WebSockets;
using System.Net.Http;
using System.IO;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

var app = builder.Build();
app.UseWebSockets();
app.UseStaticFiles();

string serverUrl = Environment.GetEnvironmentVariable("HIDBRIDGE_SERVER_URL") ?? "http://127.0.0.1:8080";
string? token = Environment.GetEnvironmentVariable("HIDBRIDGE_TOKEN");

// Suppress noisy 404s in dev-tools scenarios.
app.MapGet("/favicon.ico", () => Results.Text("", "image/x-icon"));
app.MapGet("/_vs/browserLink", () => Results.Text("", "application/javascript"));

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
	    <h3>WebRTC Control (MVP)</h3>
	    <div class="row muted">
	      WebRTC DataChannel for control messages (keyboard/mouse JSON) using a minimal signaling relay: browser &harr; <code>HidControl.Web</code> &harr; <code>HidControlServer</code> (<code>/ws/webrtc</code>).
	    </div>
	    <div class="row muted">
	      Quick guide:
	      1) Pick a <b>Room</b> (use <b>Generate</b> for a unique one).
	      2) Click <b>Connect</b>.
	      3) When status becomes <code>datachannel: open</code>, click <b>Send</b>.
	      4) Click <b>Hangup</b> when done.
	      If you see <code>room_full</code>: someone is already controlling that room (close the other tab or generate a new room).
	    </div>
	    <div class="row muted">
	      Note: server-side helpers listen on <code>control</code> (control-plane) and <code>video</code> (video-plane) by default. Generated rooms auto-start helpers when possible.
	    </div>
	    <div class="row">
	      <select id="rtcMode" title="Room mode">
	        <option value="control" selected>control</option>
	        <option value="video">video</option>
	      </select>
	      <input id="rtcRoom" style="min-width: 220px" value="control" placeholder="room" />
	      <button id="rtcGenRoom" title="Generate a random room id">Generate</button>
	      <button id="rtcRefreshRooms" title="Fetch rooms from server">Refresh Rooms</button>
	      <button id="rtcStartHelper" title="Ensure the server-side helper is started for this room">Start Helper</button>
	      <button id="rtcConnect">Connect</button>
	      <button id="rtcHangup">Hangup</button>
	    </div>
	    <div class="row">
	      <label class="muted"><input id="rtcAutoRefresh" type="checkbox" checked /> Auto-refresh rooms</label>
	      <span class="muted">every <span id="rtcAutoRefreshMs">2000</span>ms</span>
	    </div>
	    <div class="row" style="width: 100%">
	      <table style="width: 100%; border-collapse: collapse">
		        <thead>
		          <tr class="muted">
		            <th style="text-align:left; padding: 6px 4px">Room</th>
		            <th style="text-align:left; padding: 6px 4px">Kind</th>
		            <th style="text-align:left; padding: 6px 4px">Peers</th>
		            <th style="text-align:left; padding: 6px 4px">Tags</th>
		            <th style="text-align:left; padding: 6px 4px">Status</th>
		            <th style="text-align:left; padding: 6px 4px">Actions</th>
	          </tr>
	        </thead>
	        <tbody id="rtcRoomsBody"></tbody>
	      </table>
	    </div>
	    <div class="card">
	      <h4>Control Actions (DataChannel)</h4>
	      <div class="row">
	        <input id="rtcShortcut" style="min-width: 220px" value="Ctrl+C" placeholder="Shortcut, e.g. Ctrl+C" />
	        <input id="rtcShortcutHoldMs" type="number" style="width: 140px" value="80" />
	        <button id="rtcSendShortcutBtn" disabled>Send Shortcut</button>
	      </div>
	      <div class="row">
	        <button class="rtcPreset" data-shortcut="Ctrl+C">Ctrl+C</button>
	        <button class="rtcPreset" data-shortcut="Ctrl+V">Ctrl+V</button>
	        <button class="rtcPreset" data-shortcut="Alt+Tab">Alt+Tab</button>
	        <button class="rtcPreset" data-shortcut="Win+R">Win+R</button>
	        <button class="rtcPreset" data-shortcut="Ctrl+Alt+Del">Ctrl+Alt+Del</button>
	        <button class="rtcPreset" data-shortcut="Esc">Esc</button>
	        <button class="rtcPreset" data-shortcut="Enter">Enter</button>
	      </div>
	      <div class="row">
	        <input id="rtcText" style="min-width: 320px" placeholder="Text (layout-dependent)" />
	        <select id="rtcTextLayout">
	          <option value="en">EN (layout-dependent)</option>
	          <option value="uk">UA (layout-dependent)</option>
	          <option value="ru">RU (layout-dependent)</option>
	        </select>
	        <button id="rtcSendTextBtn" disabled>Send Text</button>
	      </div>
	      <div class="row">
	        <input id="rtcMouseDx" type="number" value="20" style="width: 120px" />
	        <input id="rtcMouseDy" type="number" value="0" style="width: 120px" />
	        <button id="rtcMouseMoveBtn" disabled>Move</button>
	        <button class="rtcClick" data-btn="left" disabled>Left click</button>
	        <button class="rtcClick" data-btn="right" disabled>Right click</button>
	        <button class="rtcClick" data-btn="middle" disabled>Middle click</button>
	      </div>
	      <div class="row muted">
	        Note: text input is layout-dependent and currently not guaranteed for all Unicode characters (see docs).
	      </div>
	    </div>
	    <details id="rtcDebug">
	      <summary class="muted">Advanced / Debug</summary>
	      <div class="row">
	        <input id="rtcIce" style="min-width: 420px" value='[{"urls":"stun:stun.l.google.com:19302"}]' placeholder='ICE servers JSON, e.g. [{"urls":"stun:stun.l.google.com:19302"}]' />
	        <label class="muted"><input id="rtcRelayOnly" type="checkbox" /> Force TURN relay</label>
	      </div>
	      <div class="row">
	        <input id="rtcSend" style="min-width: 520px" value='{"type":"keyboard.shortcut","shortcut":"Ctrl+C","holdMs":80}' placeholder='JSON to forward to /ws/hid, e.g. {"type":"keyboard.shortcut","shortcut":"Ctrl+C","holdMs":80}' />
	        <button id="rtcSendBtn" disabled>Send (raw JSON)</button>
	      </div>
	      <div class="row">
	        <button id="rtcJoin" title="Debug: join without calling">Join</button>
	        <button id="rtcCall" title="Debug: call (create datachannel + offer)">Call</button>
	      </div>
	    </details>
	    <div class="row muted" id="rtcStatus">disconnected</div>
	    <pre id="rtcOut">webrtc: ready</pre>
	  </div>

	  <pre id="out">ready</pre>

	  <script src="/webrtcControl.js"></script>
	  <script src="/webrtcVideo.js"></script>
	  <script>
	    const out = document.getElementById("out");
	    const rtcOut = document.getElementById("rtcOut");

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

    

	    // --- WebRTC signaling demo (module) ---
	    const rtcStatus = document.getElementById("rtcStatus");
	    const rtcSendBtn = document.getElementById("rtcSendBtn"); // raw JSON (debug)
	    const rtcSendShortcutBtn = document.getElementById("rtcSendShortcutBtn");
	    const rtcSendTextBtn = document.getElementById("rtcSendTextBtn");
	    const rtcMouseMoveBtn = document.getElementById("rtcMouseMoveBtn");
	    const rtcRelayOnly = document.getElementById("rtcRelayOnly");
	    // Timeouts are loaded from HidControlServer config via /api/webrtc/config.
	    // Defaults are intentionally minimal.
	    // Default values are overwritten by /api/webrtc/config on page load. Keep them small for fast fail on LAN.
	    let rtcCfg = { joinTimeoutMs: 250, connectTimeoutMs: 5000 };
	    function setRtcStatus(s) {
	      rtcStatus.textContent = s;
	      const open = (s === "datachannel: open");
	      rtcSendBtn.disabled = !open;
	      rtcSendShortcutBtn.disabled = !open;
	      rtcSendTextBtn.disabled = !open;
	      rtcMouseMoveBtn.disabled = !open;
	      for (const b of document.querySelectorAll(".rtcClick")) b.disabled = !open;
	    }

    function getIceServers() {
      const t = document.getElementById("rtcIce").value.trim();
      if (!t) return [{ urls: "stun:stun.l.google.com:19302" }];
      try { return JSON.parse(t); } catch { return []; }
    }

    function rtcLog(entry) {
      const line = JSON.stringify(entry);
      const lines = rtcOut.textContent.split("\n");
      lines.push(line);
      while (lines.length > 60) lines.shift();
      rtcOut.textContent = lines.join("\n");
    }

	    let webrtcClient = null;
	    function isVideoRoomId(r) {
	      if (!r) return false;
	      const x = String(r).toLowerCase();
	      return x === "video" || x.startsWith("hb-v-") || x.startsWith("video-");
	    }
	    function getMode() { return (document.getElementById("rtcMode").value || "control"); }
	    function getRoom() { return (document.getElementById("rtcRoom").value.trim() || "control"); }
	    function setRoom(r) {
	      if (!r) return;
	      document.getElementById("rtcRoom").value = r;
	      document.getElementById("rtcMode").value = isVideoRoomId(r) ? "video" : "control";
	      resetWebRtcClient();
	      rtcLog({ webrtc: "ui.room_selected", room: r });
	    }
	    function resetWebRtcClient() {
	      if (webrtcClient) {
	        try { webrtcClient.hangup(); } catch {}
	      }
	      const mode = getMode();
	      const module = (mode === "video")
	        ? (window.hidbridge && window.hidbridge.webrtcVideo ? window.hidbridge.webrtcVideo : null)
	        : (window.hidbridge && window.hidbridge.webrtcControl ? window.hidbridge.webrtcControl : null);
	      const fallback = (window.hidbridge && window.hidbridge.webrtcControl) ? window.hidbridge.webrtcControl : null;
	      const factory = module && typeof module.createClient === "function"
	        ? module.createClient
	        : (fallback && typeof fallback.createClient === "function" ? fallback.createClient : null);
	      if (!factory) {
	        const err = { ok: false, error: "webrtc_module_missing" };
	        rtcLog(err);
	        show(err);
	        webrtcClient = null;
	        return;
	      }
	      // Ensure we start from a clean UI state.
	      setRtcStatus("disconnected");
	      webrtcClient = factory({
	        room: getRoom(),
	        iceServers: getIceServers(),
	        iceTransportPolicy: rtcRelayOnly.checked ? "relay" : "all",
	        joinTimeoutMs: rtcCfg.joinTimeoutMs,
	        onLog: rtcLog,
	        onStatus: setRtcStatus,
	        onMessage: (data) => show({ webrtc: "message", data })
	      });
	    }
	    resetWebRtcClient();

	    async function refreshWebRtcConfig() {
	      try {
	        const res = await fetch("/api/webrtc/config");
	        if (!res.ok) return;
	        const j = await res.json().catch(() => null);
	        if (!j || !j.ok) return;
	        if (typeof j.joinTimeoutMs === "number") rtcCfg.joinTimeoutMs = j.joinTimeoutMs;
	        if (typeof j.connectTimeoutMs === "number") rtcCfg.connectTimeoutMs = j.connectTimeoutMs;
	        rtcLog({ webrtc: "ui.cfg_loaded", rtcCfg });
	        // Recreate client so join timeout changes apply immediately.
	        resetWebRtcClient();
	      } catch {}
	    }

	    async function refreshRooms() {
	      try {
	        const res = await fetch("/api/webrtc/rooms");
	        if (!res.ok) return;
	        const j = await res.json();
	        if (!j || !j.ok || !Array.isArray(j.rooms)) return;
	        const body = document.getElementById("rtcRoomsBody");
	        body.innerHTML = "";
	        const mode = getMode();
	        for (const r of j.rooms) {
	          const room = String(r.room || "");
	          const isVideo = isVideoRoomId(room);
	          const kind = isVideo ? "video" : "control";
	          if (mode !== "video" && isVideo) continue;
	          if (mode === "video" && !isVideo) continue;

	          const tags = [];
	          if (r.isControl) tags.push("control");
	          if (isVideo) tags.push("video");
	          if (r.hasHelper) tags.push("helper");

	          const status = (r.peers >= 2) ? "busy" : (r.hasHelper ? "idle" : "empty");
	          const canDelete = !r.isControl && room.toLowerCase() !== "video";
	          const canStart = !r.hasHelper && status !== "busy";
	          const canConnect = status !== "busy" || room.toLowerCase() === getRoom().toLowerCase();

	          const tr = document.createElement("tr");
	          tr.innerHTML = `
	            <td style="padding: 6px 4px; font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, 'Liberation Mono', 'Courier New', monospace">${room}</td>
	            <td style="padding: 6px 4px"><code>${kind}</code></td>
	            <td style="padding: 6px 4px">${r.peers}</td>
	            <td style="padding: 6px 4px" class="muted">${tags.join(", ") || "-"}</td>
	            <td style="padding: 6px 4px">${status}</td>
	            <td style="padding: 6px 4px">
	              <button data-act="start" data-room="${room}" ${canStart ? "" : "disabled"} title="${canStart ? "start helper for this room" : (r.hasHelper ? "helper already started" : "room is busy")}">Start</button>
	              <button data-act="use" data-room="${room}">Use</button>
	              <button data-act="connect" data-room="${room}" ${canConnect ? "" : "disabled"} title="${canConnect ? "connect using this room" : "room is busy"}">Connect</button>
	              <button data-act="delete" data-room="${room}" ${canDelete ? "" : "disabled"} title="${canDelete ? "stop helper for this room" : "default room cannot be deleted"}">Delete</button>
	            </td>
	          `;
	          body.appendChild(tr);
	        }
	      } catch {}
	    }

	    document.getElementById("rtcRefreshRooms").addEventListener("click", async () => {
	      await refreshRooms();
	      rtcLog({ webrtc: "ui.rooms_refreshed" });
	    });

	    document.getElementById("rtcRoomsBody").addEventListener("click", async (ev) => {
	      const btn = ev.target && ev.target.closest ? ev.target.closest("button[data-act]") : null;
	      if (!btn) return;
	      const act = btn.getAttribute("data-act");
	      const room = btn.getAttribute("data-room");
	      if (!room) return;

	      if (act === "start") {
	        try {
	          const prefix = getRoomsApiPrefix(room);
	          const res = await fetch(prefix, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ room }) });
	          const j = await res.json().catch(() => null);
	          rtcLog({ webrtc: "ui.room_started", room, endpoint: prefix, payload: j });
	          show(j || { ok: false, error: "start_failed" });
	          await refreshRooms();
	        } catch (e) {
	          rtcLog({ webrtc: "ui.room_start_error", room, error: String(e) });
	          show({ ok: false, error: String(e) });
	        }
	        return;
	      }
	      if (act === "use") {
	        setRoom(room);
	        return;
	      }
	      if (act === "connect") {
	        setRoom(room);
	        document.getElementById("rtcConnect").click();
	        return;
	      }
	      if (act === "delete") {
	        try {
	          const prefix = getRoomsApiPrefix(room);
	          const res = await fetch(prefix + "/" + encodeURIComponent(room), { method: "DELETE" });
	          const j = await res.json().catch(() => null);
	          rtcLog({ webrtc: "ui.room_deleted", room, endpoint: prefix, payload: j });
	          show(j || { ok: false, error: "delete_failed" });
	          await refreshRooms();
	        } catch (e) {
	          rtcLog({ webrtc: "ui.room_delete_error", room, error: String(e) });
	          show({ ok: false, error: String(e) });
	        }
	      }
	    });

	    document.getElementById("rtcGenRoom").addEventListener("click", async () => {
	      try {
	        const mode = getMode();
	        const createUrl = (mode === "video") ? "/api/webrtc/video/rooms" : "/api/webrtc/rooms";
	        const res = await fetch(createUrl, { method: "POST", headers: { "Content-Type": "application/json" }, body: "{}" });
	        const j = await res.json().catch(() => null);
	        if (!j || !j.ok || !j.room) {
	          rtcLog({ webrtc: "ui.room_create_failed", payload: j });
	          show(j || { ok: false, error: "room_create_failed" });
	          return;
	        }
	        setRoom(j.room);
	        rtcLog({ webrtc: "ui.room_created", room: j.room, started: j.started, pid: j.pid });
	        await refreshRooms();
	      } catch (e) {
	        rtcLog({ webrtc: "ui.room_create_error", error: String(e) });
	        show({ ok: false, error: String(e) });
	      }
	    });

	    // Try to auto-load ICE servers from the server (TURN REST) if available.
	    (async () => {
	      try {
	        const res = await fetch("/api/webrtc/ice");
	        if (!res.ok) return;
	        const j = await res.json();
	        if (j && j.ok && Array.isArray(j.iceServers) && j.iceServers.length > 0) {
	          document.getElementById("rtcIce").value = JSON.stringify(j.iceServers);
	          // If TURN is configured, we *optionally* force relay-only. Some browsers (Edge/Opera in hardened
	          // environments) produce 0 candidates unless TURN is used. Chrome/Firefox usually work fine with "all".
	          const hasTurn = j.iceServers.some(s => Array.isArray(s.urls) && s.urls.some(u => (u || "").startsWith("turn:") || (u || "").startsWith("turns:")));
	          const ua = navigator.userAgent || "";
	          const isEdge = ua.includes("Edg/");
	          const isOpera = ua.includes("OPR/") || ua.includes("Opera");
	          if (hasTurn && (isEdge || isOpera)) rtcRelayOnly.checked = true;
	        }
	      } catch {}
	    })();
	    refreshWebRtcConfig();
	    refreshRooms();
	    document.getElementById("rtcMode").addEventListener("change", () => {
	      refreshRooms();
	    });

	    // Room list auto-refresh (best-effort).
	    const rtcAutoRefresh = document.getElementById("rtcAutoRefresh");
	    const rtcAutoRefreshMsEl = document.getElementById("rtcAutoRefreshMs");
	    let rtcRoomsTimer = null;
	    function startRoomsTimer() {
	      stopRoomsTimer();
	      const intervalMs = 2000;
	      rtcAutoRefreshMsEl.textContent = String(intervalMs);
	      rtcRoomsTimer = setInterval(() => { if (rtcAutoRefresh.checked) refreshRooms(); }, intervalMs);
	    }
	    function stopRoomsTimer() {
	      if (!rtcRoomsTimer) return;
	      clearInterval(rtcRoomsTimer);
	      rtcRoomsTimer = null;
	    }
	    rtcAutoRefresh.addEventListener("change", () => {
	      rtcLog({ webrtc: "ui.auto_refresh", enabled: rtcAutoRefresh.checked });
	    });
	    startRoomsTimer();

	    async function connectWithTimeout(timeoutMs) {
	      const start = Date.now();
	      while (Date.now() - start < timeoutMs) {
	        const s = rtcStatus.textContent;
	        if (s === "datachannel: open") return true;
	        if (s === "no_local_candidates") return false;
	        if (s && (s.startsWith("error:") || s === "room_full" || s === "disconnected")) return false;
	        await new Promise(r => setTimeout(r, 50));
	      }
	      return false;
	    }

	    function getRoomsApiPrefix(room) {
	      if (isVideoRoomId(room)) return "/api/webrtc/video/rooms";
	      return "/api/webrtc/rooms";
	    }

	    async function ensureHelper(room) {
	      try {
	        const wanted = (room || "").trim();
	        const wantedLc = wanted.toLowerCase();
	        const prefix = getRoomsApiPrefix(wanted);

	        // "control" is expected to exist from startup defaults; do not create here to avoid accidental room drift.
	        if (wantedLc === "control") {
	          const listRes = await fetch(prefix);
	          const list = await listRes.json().catch(() => null);
	          rtcLog({ webrtc: "ui.ensure_helper_builtin", room: wanted, endpoint: prefix, payload: list });
	          if (list && list.ok && Array.isArray(list.rooms)) {
	            const rr = list.rooms.find(x => x && x.room && String(x.room).toLowerCase() === wantedLc);
	            if (rr && rr.hasHelper) return true;
	            show({ ok: false, error: "helper_missing_builtin_room", room: wanted });
	            return false;
	          }
	          show(list || { ok: false, error: "ensure_helper_failed" });
	          return false;
	        }

	        // "video" may be absent until first use; auto-create helper when missing.
	        if (wantedLc === "video") {
	          const listRes = await fetch(prefix);
	          const list = await listRes.json().catch(() => null);
	          rtcLog({ webrtc: "ui.ensure_helper_builtin", room: wanted, endpoint: prefix, payload: list });
	          if (list && list.ok && Array.isArray(list.rooms)) {
	            const rr = list.rooms.find(x => x && x.room && String(x.room).toLowerCase() === wantedLc);
	            if (rr && rr.hasHelper) return true;
	          }
	          const createRes = await fetch(prefix, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ room: wanted }) });
	          const created = await createRes.json().catch(() => null);
	          rtcLog({ webrtc: "ui.ensure_helper_video_create", room: wanted, endpoint: prefix, payload: created });
	          if (created && created.ok && created.room && String(created.room).toLowerCase() === wantedLc) return true;
	          if (created && created.ok && created.room && String(created.room).toLowerCase() !== wantedLc) {
	            show({ ok: false, error: "room_mismatch", requested: wanted, actual: created.room });
	            return false;
	          }
	          show(created || { ok: false, error: "ensure_helper_failed" });
	          return false;
	        }

	        const res = await fetch(prefix, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ room: wanted }) });
	        const j = await res.json().catch(() => null);
	        rtcLog({ webrtc: "ui.ensure_helper", room: wanted, endpoint: prefix, payload: j });
	        if (j && j.ok && j.room && String(j.room).toLowerCase() === wantedLc) return true;
	        if (j && j.ok && j.room && String(j.room).toLowerCase() !== wantedLc) {
	          show({ ok: false, error: "room_mismatch", requested: wanted, actual: j.room });
	          return false;
	        }
	        show(j || { ok: false, error: "ensure_helper_failed" });
	      } catch (e) {
	        rtcLog({ webrtc: "ui.ensure_helper_error", room, error: String(e) });
	        show({ ok: false, error: String(e) });
	      }
	      return false;
	    }

	    async function waitForHelperPeer(room, timeoutMs) {
	      const prefix = getRoomsApiPrefix(room);
	      const start = Date.now();
	      while (Date.now() - start < timeoutMs) {
	        try {
	          const res = await fetch(prefix);
	          if (res.ok) {
	            const j = await res.json().catch(() => null);
	            if (j && j.ok && Array.isArray(j.rooms)) {
	              const rr = j.rooms.find(x => x && x.room && x.room.toLowerCase() === room.toLowerCase());
	              if (rr && rr.hasHelper && typeof rr.peers === "number" && rr.peers >= 1) return true;
	            }
	          }
	        } catch {}
	        await new Promise(r => setTimeout(r, 150));
	      }
	      return false;
	    }

	    function getHelperReadyTimeoutMs(room) {
	      const r = (room || "").toLowerCase();
	      const base = Math.max(1000, rtcCfg.connectTimeoutMs || 0);
	      // Video helper often starts slower on first run (go toolchain warm-up).
	      if (r === "video" || r.startsWith("hb-v-") || r.startsWith("video-")) {
	        return Math.min(60000, Math.max(20000, base * 4));
	      }
	      return Math.min(30000, Math.max(10000, base * 2));
	    }

	    document.getElementById("rtcStartHelper").addEventListener("click", async () => {
	      const room = getRoom();
	      const ok = await ensureHelper(room);
	      if (ok) {
	        const ready = await waitForHelperPeer(room, getHelperReadyTimeoutMs(room));
	        rtcLog({ webrtc: "ui.helper_ready", room, ready });
	      }
	      await refreshRooms();
	    });

	    document.getElementById("rtcConnect").addEventListener("click", async () => {
	      resetWebRtcClient();
	      if (!webrtcClient) return;
	      try {
	        const room = getRoom();
	        // If the room has no helper yet, start it before calling (gives a consistent UX for generated rooms).
	        const helperOk = await ensureHelper(room);
	        if (!helperOk) return;
	        // Important: signaling is a relay. If we send an offer before the helper joins the room, it will be lost.
	        const helperReadyTimeoutMs = getHelperReadyTimeoutMs(room);
	        const ready = await waitForHelperPeer(room, helperReadyTimeoutMs);
	        if (!ready) {
	          show({
	            ok: false,
	            error: "helper_not_ready",
	            hint: "Helper did not join the room in time. On first start this may take longer; retry once.",
	            room,
	            helperReadyTimeoutMs
	          });
	          return;
	        }
	        await webrtcClient.call();
	        const connectedOk = await connectWithTimeout(rtcCfg.connectTimeoutMs);
	        if (!connectedOk) {
	          const statusNow = rtcStatus.textContent || "";
	          if (statusNow === "no_local_candidates" || statusNow.startsWith("error: no_local_candidates")) {
	            show({
	              ok: false,
	              error: "no_local_candidates",
	              hint: "This browser produced 0 ICE candidates. Try Chrome/Firefox or configure TURN in ICE servers JSON."
	            });
	            return;
	          }
	          if (statusNow.startsWith("error: room_full") || statusNow === "room_full") {
	            show({ ok: false, error: "room_full", hint: "This room already has a controller. Close the other tab/browser or generate a new room.", room: getRoom() });
	            return;
	          }
	          rtcLog({ webrtc: "ui.connect_timeout", room: getRoom(), debug: webrtcClient.getDebug ? webrtcClient.getDebug() : null });
	          const dbg = webrtcClient.getDebug ? webrtcClient.getDebug() : null;
	          if (dbg && dbg.lastJoinedPeers === 1) {
	            show({
	              ok: false,
	              error: "no_peer_in_room",
	              hint: "No peer is present in this room. Use room 'control' (server helper) or start WebRtcControlPeer with the same room.",
	              room: getRoom()
	            });
	            return;
	          }
	          show({ ok: false, error: "connect_timeout", debug: webrtcClient.getDebug ? webrtcClient.getDebug() : null });
	        }
	      } catch (e) {
	        rtcLog({ webrtc: "ui.connect_error", error: String(e) });
	        show({ ok: false, error: String(e) });
	      }
	    });

		    document.getElementById("rtcJoin").addEventListener("click", async () => {
		      resetWebRtcClient();
		      if (!webrtcClient) return;
		      try {
		        await webrtcClient.join();
		      } catch (e) {
		        rtcLog({ webrtc: "ui.join_error", error: String(e) });
		        show({ ok: false, error: String(e) });
	      }
	    });

		    document.getElementById("rtcCall").addEventListener("click", async () => {
		      resetWebRtcClient();
		      if (!webrtcClient) return;
		      try {
		        await webrtcClient.call();
		      } catch (e) {
		        rtcLog({ webrtc: "ui.call_error", error: String(e) });
		        show({ ok: false, error: String(e) });
	      }
	    });

	    document.getElementById("rtcHangup").addEventListener("click", async () => {
	      if (webrtcClient) webrtcClient.hangup();
	      setRtcStatus("disconnected");
	    });

    function sendDcJson(obj) {
      if (!webrtcClient) throw new Error("webrtc_not_ready");
      const s = rtcStatus.textContent;
      if (s !== "datachannel: open") throw new Error("datachannel_not_open");
      const text = JSON.stringify(obj);
      webrtcClient.send(text);
      show({ ok: true, sent: obj });
    }

    for (const btn of document.querySelectorAll(".rtcPreset")) {
      btn.addEventListener("click", () => {
        const sc = btn.getAttribute("data-shortcut") || "";
        document.getElementById("rtcShortcut").value = sc;
      });
    }

    document.getElementById("rtcSendShortcutBtn").addEventListener("click", async () => {
      const shortcut = document.getElementById("rtcShortcut").value.trim();
      const holdMs = Number.parseInt(document.getElementById("rtcShortcutHoldMs").value, 10) || 80;
      if (!shortcut) {
        show({ ok: false, error: "shortcut_required" });
        return;
      }
      try { sendDcJson({ type: "keyboard.shortcut", shortcut, holdMs }); } catch (e) { show({ ok: false, error: String(e) }); }
    });

    document.getElementById("rtcSendTextBtn").addEventListener("click", async () => {
      const text = document.getElementById("rtcText").value || "";
      const layout = document.getElementById("rtcTextLayout").value || "en";
      try { sendDcJson({ type: "keyboard.text", text, layout }); } catch (e) { show({ ok: false, error: String(e) }); }
    });

    document.getElementById("rtcMouseMoveBtn").addEventListener("click", async () => {
      const dx = Number.parseInt(document.getElementById("rtcMouseDx").value, 10) || 0;
      const dy = Number.parseInt(document.getElementById("rtcMouseDy").value, 10) || 0;
      try { sendDcJson({ type: "mouse.move", dx, dy }); } catch (e) { show({ ok: false, error: String(e) }); }
    });

    function sleep(ms) { return new Promise(r => setTimeout(r, ms)); }
    for (const btn of document.querySelectorAll(".rtcClick")) {
      btn.addEventListener("click", async () => {
        const button = btn.getAttribute("data-btn") || "left";
        try {
          sendDcJson({ type: "mouse.button", button, down: true });
          await sleep(20);
          sendDcJson({ type: "mouse.button", button, down: false });
        } catch (e) {
          show({ ok: false, error: String(e) });
        }
      });
    }

    document.getElementById("rtcSendBtn").addEventListener("click", async () => {
      const text = document.getElementById("rtcSend").value;
      try { JSON.parse(text); } catch {
        show({ ok: false, error: "expected_json", hint: "{\"type\":\"keyboard.shortcut\",\"shortcut\":\"Ctrl+C\",\"holdMs\":80}" });
        return;
      }
      try {
        webrtcClient.send(text);
        show({ ok: true, sent: ParseJsonOrString(text) });
      } catch (e) {
        show({ ok: false, error: String(e) });
      }
    });
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

app.MapGet("/api/webrtc/ice", async (CancellationToken ct) =>
{
    try
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        if (!string.IsNullOrWhiteSpace(token)) http.DefaultRequestHeaders.Add("X-HID-Token", token);
        var client = new HidControl.ClientSdk.HidControlClient(http, new Uri(serverUrl));
        var res = await HidControl.ClientSdk.WebRtcClientExtensions.GetWebRtcIceAsync(client, ct);
        return Results.Json(res ?? new HidControl.Contracts.WebRtcIceResponse(false, null, Array.Empty<HidControl.Contracts.WebRtcIceServerDto>()));
    }
    catch (TaskCanceledException)
    {
        return Results.Json(new HidControl.Contracts.WebRtcIceResponse(false, null, Array.Empty<HidControl.Contracts.WebRtcIceServerDto>()));
    }
    catch (Exception ex)
    {
        // Keep the same response shape so the JS client can always parse it.
        return Results.Json(new HidControl.Contracts.WebRtcIceResponse(false, null, Array.Empty<HidControl.Contracts.WebRtcIceServerDto>()));
    }
});

app.MapGet("/api/webrtc/config", async (CancellationToken ct) =>
{
    try
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        if (!string.IsNullOrWhiteSpace(token)) http.DefaultRequestHeaders.Add("X-HID-Token", token);
        var client = new HidControl.ClientSdk.HidControlClient(http, new Uri(serverUrl));
        var res = await HidControl.ClientSdk.WebRtcClientExtensions.GetWebRtcConfigAsync(client, ct);
        return Results.Json(res ?? new HidControl.Contracts.WebRtcConfigResponse(false, 0, 0, 0, 0, 0));
    }
    catch (TaskCanceledException)
    {
        return Results.Json(new HidControl.Contracts.WebRtcConfigResponse(false, 0, 0, 0, 0, 0));
    }
    catch (Exception ex)
    {
        return Results.Json(new HidControl.Contracts.WebRtcConfigResponse(false, 0, 0, 0, 0, 0));
    }
});

app.MapGet("/api/webrtc/rooms", async (CancellationToken ct) =>
{
    try
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        if (!string.IsNullOrWhiteSpace(token)) http.DefaultRequestHeaders.Add("X-HID-Token", token);
        var client = new HidControl.ClientSdk.HidControlClient(http, new Uri(serverUrl));
        var res = await HidControl.ClientSdk.WebRtcClientExtensions.ListWebRtcRoomsAsync(client, ct);
        return Results.Json(res ?? new HidControl.Contracts.WebRtcRoomsResponse(false, Array.Empty<HidControl.Contracts.WebRtcRoomDto>()));
    }
    catch (TaskCanceledException)
    {
        return Results.Json(new HidControl.Contracts.WebRtcRoomsResponse(false, Array.Empty<HidControl.Contracts.WebRtcRoomDto>()));
    }
    catch (Exception ex)
    {
        return Results.Json(new HidControl.Contracts.WebRtcRoomsResponse(false, Array.Empty<HidControl.Contracts.WebRtcRoomDto>()));
    }
});

app.MapPost("/api/webrtc/rooms", async (HttpRequest req, CancellationToken ct) =>
{
    try
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        if (!string.IsNullOrWhiteSpace(token)) http.DefaultRequestHeaders.Add("X-HID-Token", token);
        var client = new HidControl.ClientSdk.HidControlClient(http, new Uri(serverUrl));
        var body = await req.ReadFromJsonAsync<HidControl.Contracts.WebRtcCreateRoomRequest>(cancellationToken: ct)
            ?? new HidControl.Contracts.WebRtcCreateRoomRequest(null);
        var res = await HidControl.ClientSdk.WebRtcClientExtensions.CreateWebRtcRoomAsync(client, body.Room, ct);
        return Results.Json(res ?? new HidControl.Contracts.WebRtcCreateRoomResponse(false, null, false, null, "create_failed"));
    }
    catch (TaskCanceledException)
    {
        return Results.Json(new HidControl.Contracts.WebRtcCreateRoomResponse(false, null, false, null, "timeout"));
    }
    catch (Exception ex)
    {
        return Results.Json(new HidControl.Contracts.WebRtcCreateRoomResponse(false, null, false, null, ex.Message));
    }
});

app.MapDelete("/api/webrtc/rooms/{room}", async (string room, CancellationToken ct) =>
{
    try
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        if (!string.IsNullOrWhiteSpace(token)) http.DefaultRequestHeaders.Add("X-HID-Token", token);
        var client = new HidControl.ClientSdk.HidControlClient(http, new Uri(serverUrl));
        var res = await HidControl.ClientSdk.WebRtcClientExtensions.DeleteWebRtcRoomAsync(client, room, ct);
        return Results.Json(res ?? new HidControl.Contracts.WebRtcDeleteRoomResponse(false, room, false, "delete_failed"));
    }
    catch (TaskCanceledException)
    {
        return Results.Json(new HidControl.Contracts.WebRtcDeleteRoomResponse(false, room, false, "timeout"));
    }
    catch (Exception ex)
    {
        return Results.Json(new HidControl.Contracts.WebRtcDeleteRoomResponse(false, room, false, ex.Message));
    }
});

// WebRTC "video room" lifecycle (skeleton).
app.MapGet("/api/webrtc/video/rooms", async (CancellationToken ct) =>
{
    try
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        if (!string.IsNullOrWhiteSpace(token)) http.DefaultRequestHeaders.Add("X-HID-Token", token);
        var client = new HidControl.ClientSdk.HidControlClient(http, new Uri(serverUrl));
        var res = await HidControl.ClientSdk.WebRtcClientExtensions.ListWebRtcVideoRoomsAsync(client, ct);
        return Results.Json(res ?? new HidControl.Contracts.WebRtcRoomsResponse(false, Array.Empty<HidControl.Contracts.WebRtcRoomDto>()));
    }
    catch (TaskCanceledException)
    {
        return Results.Json(new HidControl.Contracts.WebRtcRoomsResponse(false, Array.Empty<HidControl.Contracts.WebRtcRoomDto>()));
    }
    catch
    {
        return Results.Json(new HidControl.Contracts.WebRtcRoomsResponse(false, Array.Empty<HidControl.Contracts.WebRtcRoomDto>()));
    }
});

app.MapPost("/api/webrtc/video/rooms", async (HttpRequest req, CancellationToken ct) =>
{
    try
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        if (!string.IsNullOrWhiteSpace(token)) http.DefaultRequestHeaders.Add("X-HID-Token", token);
        var client = new HidControl.ClientSdk.HidControlClient(http, new Uri(serverUrl));
        var body = await req.ReadFromJsonAsync<HidControl.Contracts.WebRtcCreateRoomRequest>(cancellationToken: ct)
            ?? new HidControl.Contracts.WebRtcCreateRoomRequest(null);
        var res = await HidControl.ClientSdk.WebRtcClientExtensions.CreateWebRtcVideoRoomAsync(client, body.Room, ct);
        return Results.Json(res ?? new HidControl.Contracts.WebRtcCreateRoomResponse(false, null, false, null, "create_failed"));
    }
    catch (TaskCanceledException)
    {
        return Results.Json(new HidControl.Contracts.WebRtcCreateRoomResponse(false, null, false, null, "timeout"));
    }
    catch (Exception ex)
    {
        return Results.Json(new HidControl.Contracts.WebRtcCreateRoomResponse(false, null, false, null, ex.Message));
    }
});

app.MapDelete("/api/webrtc/video/rooms/{room}", async (string room, CancellationToken ct) =>
{
    try
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        if (!string.IsNullOrWhiteSpace(token)) http.DefaultRequestHeaders.Add("X-HID-Token", token);
        var client = new HidControl.ClientSdk.HidControlClient(http, new Uri(serverUrl));
        var res = await HidControl.ClientSdk.WebRtcClientExtensions.DeleteWebRtcVideoRoomAsync(client, room, ct);
        return Results.Json(res ?? new HidControl.Contracts.WebRtcDeleteRoomResponse(false, room, false, "delete_failed"));
    }
    catch (TaskCanceledException)
    {
        return Results.Json(new HidControl.Contracts.WebRtcDeleteRoomResponse(false, room, false, "timeout"));
    }
    catch (Exception ex)
    {
        return Results.Json(new HidControl.Contracts.WebRtcDeleteRoomResponse(false, room, false, ex.Message));
    }
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
