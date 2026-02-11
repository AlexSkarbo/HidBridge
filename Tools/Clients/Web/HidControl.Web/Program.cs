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
	    .rtc-qrow {
	      display: flex;
	      flex-direction: column;
	      align-items: stretch;
	      gap: 6px;
	      margin-top: 4px;
	      width: 100%;
	    }
	    .rtc-qgrid {
	      display: grid;
	      grid-template-columns: 1fr 1fr 1fr 1fr 1fr 1.8fr 1.6fr;
	      column-gap: 10px;
	      width: 100%;
	      max-width: 1700px;
	      margin: 0 auto;
	      align-items: center;
	    }
	    .rtc-qgrid > * { min-width: 0; }
      .rtc-qgrid-head label {
        white-space: nowrap;
        display: block;
        text-align: center;
      }
      .rtc-qgrid-body > * {
        width: 100%;
        box-sizing: border-box;
      }
      .rtc-preset-row {
        margin-top: 8px;
        display: flex;
        gap: 8px;
        align-items: center;
        flex-wrap: wrap;
      }
      .rtc-preset-row .muted {
        margin-right: 4px;
      }
      .rtc-quality-badge {
        display: inline-block;
        border-radius: 6px;
        padding: 1px 6px;
        font-weight: 600;
      }
      .rtc-quality-good { background: #e8f7ec; color: #146c2e; }
      .rtc-quality-degraded { background: #fff3cd; color: #7a5800; }
      .rtc-quality-bad { background: #fde2e1; color: #8f1d18; }
      .rtc-kpi-row {
        display: flex;
        flex-wrap: wrap;
        gap: 8px;
        align-items: center;
      }
      .rtc-kpi-item {
        border: 1px solid #d8d8d8;
        border-radius: 999px;
        padding: 3px 8px;
        font-size: 12px;
        line-height: 1.4;
        background: #fafafa;
      }
      .rtc-video-surface {
        position: relative;
        width: min(100%, 960px);
      }
      .rtc-focus-hint {
        position: absolute;
        left: 50%;
        top: 50%;
        transform: translate(-50%, -50%);
        padding: 8px 12px;
        border-radius: 8px;
        background: rgba(0, 0, 0, 0.62);
        color: #fff;
        font-size: 12px;
        pointer-events: none;
        z-index: 21;
        display: none;
      }
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
	    <div id="rtcQualityWrap" style="display:none">
	      <div class="rtc-qrow">
	        <div class="rtc-qgrid rtc-qgrid-head">
	          <label class="muted" for="rtcVideoQuality">Quality</label>
	          <label class="muted" for="rtcVideoImageQuality">Image quality (1..100)</label>
	          <label class="muted" for="rtcVideoBitrateKbps">Bitrate kbps</label>
	          <label class="muted" for="rtcVideoEncoder">Encoder</label>
	          <label class="muted" for="rtcVideoCodec">Codec</label>
	          <label class="muted" for="rtcVideoCaptureInput">Capture input</label>
	          <label class="muted" for="rtcVideoCaptureMode">Capture mode</label>
          </div>
	        <div class="rtc-qgrid rtc-qgrid-body">
	          <select id="rtcVideoQuality" title="Video quality preset">
	            <option value="low">low</option>
	            <option value="low-latency">low-latency</option>
	            <option value="balanced" selected>balanced</option>
	            <option value="high">high</option>
	            <option value="optimal">optimal (1080p)</option>
	          </select>
	          <input id="rtcVideoImageQuality" type="number" min="1" max="100" step="1" value="" placeholder="auto" />
	          <input id="rtcVideoBitrateKbps" type="number" min="200" max="12000" step="50" value="" placeholder="auto" />
	          <select id="rtcVideoEncoder" title="Encoder mode">
	            <option value="cpu">CPU (software)</option>
	          </select>
	          <select id="rtcVideoCodec" title="Video codec">
	            <option value="vp8" selected>VP8</option>
	            <option value="h264">H264</option>
	          </select>
	          <select id="rtcVideoCaptureInput" title="Windows dshow capture input" style="width:100%;">
	            <option value="">(server default)</option>
	          </select>
	          <select id="rtcVideoCaptureMode" title="Capture resolution/fps" style="width:100%;">
	            <option value="">auto (server default)</option>
	          </select>
	        </div>
      <div class="rtc-preset-row">
            <span class="muted">Preset</span>
            <button id="rtcPresetLowLatency" type="button">Low latency</button>
            <button id="rtcPresetBalanced" type="button">Balanced</button>
            <button id="rtcPresetQuality" type="button">Quality</button>
            <button id="rtcAutoTune" type="button" title="Adjust quality/bitrate based on current runtime metrics">Auto tune</button>
            <button id="rtcApplyNow" type="button" title="Restart helper in this room with current settings">Apply now</button>
            <span id="rtcPresetHint" class="muted"></span>
          </div>
	      </div>
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
	    <div class="card" id="rtcVideoPane" style="display:none">
	      <h4>Video Preview</h4>
      <div class="row">
        <div id="rtcVideoSurface" class="rtc-video-surface">
          <video id="rtcRemoteVideo" autoplay playsinline muted tabindex="0" style="width: 100%; aspect-ratio: 16 / 9; background: #111; border: 1px solid #2d2d2d; border-radius: 10px;"></video>
          <div id="rtcFocusHint" class="rtc-focus-hint" aria-hidden="true">Click to focus remote input</div>
        </div>
      </div>
      <div class="row">
        <button id="rtcVideoFullscreen" type="button">Fullscreen</button>
        <span class="muted">Shortcut: <code>Ctrl+Alt+Enter</code></span>
        <button id="rtcVideoFitFill" type="button">Fit</button>
        <button id="rtcVideoInputToggle" type="button">Enable Remote Input</button>
        <span id="rtcVideoInputState" class="muted">remote input: off</span>
      </div>
      <div class="rtc-kpi-row">
        <span class="rtc-kpi-item" id="rtcKpiQuality">quality: n/a</span>
        <span class="rtc-kpi-item" id="rtcKpiStartup">startup: n/a</span>
        <span class="rtc-kpi-item" id="rtcKpiFps">fps: n/a</span>
        <span class="rtc-kpi-item" id="rtcKpiKbps">kbps: n/a</span>
        <span class="rtc-kpi-item" id="rtcKpiAbr">abr: n/a</span>
        <span class="rtc-kpi-item" id="rtcKpiCodec">codec: n/a</span>
        <span class="rtc-kpi-item" id="rtcKpiEncoder">encoder: n/a</span>
        <span class="rtc-kpi-item" id="rtcKpiFallback">fallback: n/a</span>
      </div>
	      <div class="row muted" id="rtcVideoMeta">waiting for remote video track...</div>
	      <div class="row muted" id="rtcVideoStability">video stability: n/a</div>
	      <div class="row muted" id="rtcVideoPerf">video perf: n/a</div>
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
      "ContextMenu": 101,
      "ControlLeft": 224, "ShiftLeft": 225, "AltLeft": 226, "MetaLeft": 227,
      "ControlRight": 228, "ShiftRight": 229, "AltRight": 230, "MetaRight": 231
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
	    const rtcVideoPane = document.getElementById("rtcVideoPane");
	    const rtcVideoSurface = document.getElementById("rtcVideoSurface");
	    const rtcRemoteVideo = document.getElementById("rtcRemoteVideo");
	    const rtcVideoMeta = document.getElementById("rtcVideoMeta");
	    const rtcVideoStability = document.getElementById("rtcVideoStability");
	    const rtcVideoPerf = document.getElementById("rtcVideoPerf");
      const rtcKpiQuality = document.getElementById("rtcKpiQuality");
      const rtcKpiStartup = document.getElementById("rtcKpiStartup");
      const rtcKpiFps = document.getElementById("rtcKpiFps");
      const rtcKpiKbps = document.getElementById("rtcKpiKbps");
      const rtcKpiAbr = document.getElementById("rtcKpiAbr");
      const rtcKpiCodec = document.getElementById("rtcKpiCodec");
      const rtcKpiEncoder = document.getElementById("rtcKpiEncoder");
      const rtcKpiFallback = document.getElementById("rtcKpiFallback");
	    const rtcVideoFullscreen = document.getElementById("rtcVideoFullscreen");
	    const rtcVideoFitFill = document.getElementById("rtcVideoFitFill");
	    const rtcVideoInputToggle = document.getElementById("rtcVideoInputToggle");
	    const rtcVideoInputState = document.getElementById("rtcVideoInputState");
      const rtcFocusHint = document.getElementById("rtcFocusHint");
      const rtcPresetLowLatency = document.getElementById("rtcPresetLowLatency");
      const rtcPresetBalanced = document.getElementById("rtcPresetBalanced");
      const rtcPresetQuality = document.getElementById("rtcPresetQuality");
      const rtcAutoTune = document.getElementById("rtcAutoTune");
      const rtcApplyNow = document.getElementById("rtcApplyNow");
      const rtcPresetHint = document.getElementById("rtcPresetHint");
	    // Timeouts are loaded from HidControlServer config via /api/webrtc/config.
	    // Defaults are intentionally minimal.
	    // Default values are overwritten by /api/webrtc/config on page load. Keep them small for fast fail on LAN.
	    let rtcCfg = { joinTimeoutMs: 250, connectTimeoutMs: 5000 };
	    let rtcRemoteInputEnabled = false;
	    let rtcMoveAccumX = 0;
	    let rtcMoveAccumY = 0;
	    let rtcMoveTimer = null;
	    let rtcLastMouseX = null;
	    let rtcLastMouseY = null;
      let rtcRemoteInputFocused = false;
      const rtcHeldButtons = new Set();
      const rtcPressedCodes = new Set();
      let rtcVideoFitMode = "fit"; // fit=contain, fill=cover
      const rtcVideoRuntime = {
        running: false,
        fallbackUsed: false,
        startupMs: 0,
        codec: "",
        encoder: "",
        startupTimeoutReason: "",
        lastErrorAt: "",
        abrCurrentKbps: 0,
        abrTargetKbps: 0,
        abrMeasuredKbps: 0,
        abrReason: ""
      };
      let rtcConnectInFlight = false;
      let rtcHangupInFlight = false;

      function setRtcMainActionBusy() {
        const connectBtn = document.getElementById("rtcConnect");
        const hangupBtn = document.getElementById("rtcHangup");
        if (connectBtn) connectBtn.disabled = rtcConnectInFlight || rtcHangupInFlight;
        if (hangupBtn) hangupBtn.disabled = rtcConnectInFlight || rtcHangupInFlight;
      }

      function formatLocalDateTime(isoOrDate) {
        if (!isoOrDate) return "";
        const d = isoOrDate instanceof Date ? isoOrDate : new Date(String(isoOrDate));
        if (Number.isNaN(d.getTime())) return "";
        return d.toLocaleString();
      }

      function mouseButtonName(button) {
        if (button === 0) return "left";
        if (button === 1) return "middle";
        if (button === 2) return "right";
        if (button === 3) return "back";
        if (button === 4) return "forward";
        return null;
      }

      function buildKeyboardReportFromPressed() {
        let mods = 0;
        const keys = [];
        for (const code of rtcPressedCodes.values()) {
          const usage = codeToUsage[code];
          if (usage === undefined) continue;
          if (usage >= 224 && usage <= 231) {
            mods |= (1 << (usage - 224));
            continue;
          }
          if (!keys.includes(usage)) keys.push(usage);
          if (keys.length >= 6) break;
        }
        return { mods, keys };
      }

      async function syncKeyboardReport() {
        const rpt = buildKeyboardReportFromPressed();
        await postQuiet("/api/keyboard/report", { mods: rpt.mods, keys: rpt.keys, applyMapping: true });
      }

      async function releaseHeldRemoteInput() {
        try {
          for (const b of Array.from(rtcHeldButtons)) {
            await postQuiet("/api/mouse/button", { button: b, down: false });
          }
          rtcHeldButtons.clear();
          rtcPressedCodes.clear();
          await postQuiet("/api/keyboard/report", { mods: 0, keys: [], applyMapping: true });
        } catch {
          rtcHeldButtons.clear();
          rtcPressedCodes.clear();
        }
      }

      function canHandleRemoteInput() {
        return rtcRemoteInputEnabled && getMode() === "video" && (rtcRemoteInputFocused || document.pointerLockElement === rtcRemoteVideo);
      }

      function updateRemoteInputUiState() {
        if (!rtcVideoInputState) return;
        if (!rtcRemoteInputEnabled) {
          rtcVideoInputState.textContent = "remote input: off";
        } else if (canHandleRemoteInput()) {
          rtcVideoInputState.textContent = "remote input: on (focused)";
        } else {
          rtcVideoInputState.textContent = "remote input: on (click video to focus)";
        }
        if (rtcFocusHint) {
          rtcFocusHint.style.display = (rtcRemoteInputEnabled && !canHandleRemoteInput()) ? "block" : "none";
        }
      }

	    async function setRemoteInputEnabled(v) {
	      rtcRemoteInputEnabled = !!v;
        if (!rtcRemoteInputEnabled) rtcRemoteInputFocused = false;
	      if (rtcVideoInputToggle) {
	        rtcVideoInputToggle.textContent = rtcRemoteInputEnabled ? "Disable Remote Input" : "Enable Remote Input";
	      }
	      if (!rtcRemoteInputEnabled) {
          rtcLastMouseX = null;
          rtcLastMouseY = null;
          if (document.pointerLockElement === rtcRemoteVideo) {
            try { document.exitPointerLock(); } catch {}
          }
          await releaseHeldRemoteInput();
	      }
	      if (rtcRemoteVideo) {
	        rtcRemoteVideo.style.cursor = rtcRemoteInputEnabled ? "crosshair" : "default";
	      }
        updateRemoteInputUiState();
	    }

	    async function postQuiet(path, payload) {
	      try {
	        const r = await fetch(path, {
	          method: "POST",
	          headers: { "Content-Type": "application/json" },
	          body: JSON.stringify(payload || {})
	        });
          return r.ok;
	      } catch {
	        return false;
	      }
	    }

	    function flushMouseMove() {
	      rtcMoveTimer = null;
	      const dx = Math.round(rtcMoveAccumX);
	      const dy = Math.round(rtcMoveAccumY);
	      rtcMoveAccumX = 0;
	      rtcMoveAccumY = 0;
	      if (dx === 0 && dy === 0) return;
	      postQuiet("/api/mouse/move", { dx, dy });
	    }

	    function queueMouseMove(dx, dy) {
	      rtcMoveAccumX += dx;
	      rtcMoveAccumY += dy;
	      if (!rtcMoveTimer) {
	        rtcMoveTimer = setTimeout(flushMouseMove, 25);
	      }
	    }

	    function keyboardChordFromEvent(e) {
	      const usage = codeToUsage[e.code];
	      if (usage === undefined) return null;
	      return {
	        mods: modsFromEvent(e),
	        keys: [usage],
	        holdMs: 35
	      };
	    }
	    const rtcPerf = {
	      connectClickAt: 0,
	      helperReadyAt: 0,
	      callAt: 0,
	      dcOpenAt: 0,
	      trackAt: 0,
	      firstFrameAt: 0,
	      fps: 0,
	      frameWindowStartMs: 0,
	      frameWindowCount: 0,
	      frameCbId: 0,
        statsTimer: null,
        statsLastAtMs: 0,
        statsLastBytes: 0,
        rttMs: 0,
        jitterMs: 0,
        lossPct: 0,
        inboundKbps: 0,
	      qualityState: "n/a",
        runtimeStartupMs: 0
	    };
	    function resetRtcPerf() {
        if (rtcPerf.statsTimer) {
          clearInterval(rtcPerf.statsTimer);
          rtcPerf.statsTimer = null;
        }
	      if (rtcRemoteVideo && typeof rtcRemoteVideo.cancelVideoFrameCallback === "function" && rtcPerf.frameCbId) {
	        try { rtcRemoteVideo.cancelVideoFrameCallback(rtcPerf.frameCbId); } catch {}
	      }
	      rtcPerf.connectClickAt = 0;
	      rtcPerf.helperReadyAt = 0;
	      rtcPerf.callAt = 0;
	      rtcPerf.dcOpenAt = 0;
	      rtcPerf.trackAt = 0;
	      rtcPerf.firstFrameAt = 0;
	      rtcPerf.fps = 0;
	      rtcPerf.frameWindowStartMs = 0;
	      rtcPerf.frameWindowCount = 0;
	      rtcPerf.frameCbId = 0;
        rtcPerf.statsLastAtMs = 0;
        rtcPerf.statsLastBytes = 0;
        rtcPerf.rttMs = 0;
        rtcPerf.jitterMs = 0;
        rtcPerf.lossPct = 0;
	      rtcPerf.inboundKbps = 0;
	      rtcPerf.qualityState = "n/a";
        rtcPerf.runtimeStartupMs = 0;
        rtcVideoRuntime.running = false;
        rtcVideoRuntime.fallbackUsed = false;
        rtcVideoRuntime.startupMs = 0;
        rtcVideoRuntime.codec = "";
        rtcVideoRuntime.encoder = "";
        rtcVideoRuntime.startupTimeoutReason = "";
        rtcVideoRuntime.lastErrorAt = "";
        rtcVideoRuntime.abrCurrentKbps = 0;
        rtcVideoRuntime.abrTargetKbps = 0;
        rtcVideoRuntime.abrMeasuredKbps = 0;
        rtcVideoRuntime.abrReason = "";
	      if (rtcVideoPerf) rtcVideoPerf.textContent = "video perf: n/a";
        updateVideoKpiPanel();
        updatePresetHint();
	    }
	    function updateRtcPerf() {
	      if (!rtcVideoPerf) return;
	      const t0 = rtcPerf.connectClickAt;
	      if (!t0) {
	        rtcVideoPerf.textContent = "video perf: n/a";
	        return;
	      }
	      const parts = [];
	      if (rtcPerf.helperReadyAt > 0) parts.push(`helper ${rtcPerf.helperReadyAt - t0}ms`);
	      if (rtcPerf.dcOpenAt > 0) parts.push(`dc-open ${rtcPerf.dcOpenAt - t0}ms`);
	      if (rtcPerf.trackAt > 0) parts.push(`track ${rtcPerf.trackAt - t0}ms`);
	      if (rtcPerf.firstFrameAt > 0) parts.push(`first-frame ${rtcPerf.firstFrameAt - t0}ms`);
        if (rtcPerf.runtimeStartupMs > 0) parts.push(`startup ${Math.round(rtcPerf.runtimeStartupMs)}ms`);
	      if (rtcPerf.fps > 0) parts.push(`fps ${rtcPerf.fps.toFixed(1)}`);
        if (rtcPerf.inboundKbps > 0) parts.push(`in ${rtcPerf.inboundKbps.toFixed(0)} kbps`);
        if (rtcPerf.rttMs > 0) parts.push(`rtt ${rtcPerf.rttMs.toFixed(0)}ms`);
        if (rtcPerf.jitterMs > 0) parts.push(`jitter ${rtcPerf.jitterMs.toFixed(1)}ms`);
        if (rtcPerf.lossPct > 0) parts.push(`loss ${rtcPerf.lossPct.toFixed(1)}%`);
        const q = String(rtcPerf.qualityState || "n/a");
        const badgeClass = q === "good" ? "rtc-quality-good" : (q === "degraded" ? "rtc-quality-degraded" : (q === "bad" ? "rtc-quality-bad" : ""));
        const badgeHtml = badgeClass
          ? `quality <span class="rtc-quality-badge ${badgeClass}">${q}</span>`
          : `quality ${q}`;
        parts.push(badgeHtml);
        rtcVideoPerf.innerHTML = parts.length > 0 ? ("video perf: " + parts.join(", ")) : "video perf: waiting...";
        updateVideoKpiPanel();
      }
      function classifyVideoQuality(rttMs, jitterMs, lossPct, inboundKbps) {
        if (inboundKbps <= 0) return "bad";
        if (rttMs >= 180 || jitterMs >= 40 || lossPct >= 5) return "bad";
        if (rttMs >= 90 || jitterMs >= 20 || lossPct >= 2) return "degraded";
        return "good";
      }
      async function collectRtcStats() {
        if (!webrtcClient || typeof webrtcClient.getStats !== "function") return;
        let report = null;
        try {
          report = await webrtcClient.getStats();
        } catch {
          return;
        }
        if (!report || typeof report.forEach !== "function") return;
        const nowMs = Date.now();
        let inboundBytes = 0;
        let packetsReceived = 0;
        let packetsLost = 0;
        let jitterSec = 0;
        let rttMs = 0;
        report.forEach((st) => {
          if (!st || typeof st !== "object") return;
          if (st.type === "candidate-pair" && (st.nominated || st.selected)) {
            const rtt = Number(st.currentRoundTripTime || 0);
            if (Number.isFinite(rtt) && rtt > 0) rttMs = Math.max(rttMs, rtt * 1000);
          }
          if (st.type === "inbound-rtp" && st.kind === "video" && !st.isRemote) {
            const b = Number(st.bytesReceived || 0);
            const pr = Number(st.packetsReceived || 0);
            const pl = Number(st.packetsLost || 0);
            const j = Number(st.jitter || 0);
            if (Number.isFinite(b) && b > 0) inboundBytes += b;
            if (Number.isFinite(pr) && pr > 0) packetsReceived += pr;
            if (Number.isFinite(pl) && pl > 0) packetsLost += pl;
            if (Number.isFinite(j) && j > 0) jitterSec = Math.max(jitterSec, j);
          }
        });
        let inboundKbps = 0;
        if (rtcPerf.statsLastAtMs > 0 && inboundBytes >= rtcPerf.statsLastBytes) {
          const dt = (nowMs - rtcPerf.statsLastAtMs) / 1000.0;
          if (dt > 0.2) {
            inboundKbps = ((inboundBytes - rtcPerf.statsLastBytes) * 8.0) / 1000.0 / dt;
          }
        }
        rtcPerf.statsLastAtMs = nowMs;
        rtcPerf.statsLastBytes = inboundBytes;
        const totalPkts = packetsReceived + packetsLost;
        rtcPerf.lossPct = totalPkts > 0 ? (packetsLost * 100.0 / totalPkts) : 0;
        rtcPerf.jitterMs = jitterSec * 1000.0;
        rtcPerf.rttMs = rttMs;
        rtcPerf.inboundKbps = inboundKbps;
        rtcPerf.qualityState = classifyVideoQuality(rtcPerf.rttMs, rtcPerf.jitterMs, rtcPerf.lossPct, rtcPerf.inboundKbps);
        updateRtcPerf();
        updatePresetHint();
      }
      function startRtcStatsProbe() {
        if (rtcPerf.statsTimer) clearInterval(rtcPerf.statsTimer);
        rtcPerf.statsTimer = setInterval(() => { collectRtcStats().catch(() => { }); }, 1000);
      }
	    function startVideoPerfProbe() {
	      if (!rtcRemoteVideo || typeof rtcRemoteVideo.requestVideoFrameCallback !== "function") return;
	      if (rtcPerf.frameCbId && typeof rtcRemoteVideo.cancelVideoFrameCallback === "function") {
	        try { rtcRemoteVideo.cancelVideoFrameCallback(rtcPerf.frameCbId); } catch {}
	      }
	      rtcPerf.frameWindowStartMs = 0;
	      rtcPerf.frameWindowCount = 0;
	      const onFrame = (nowMs) => {
	        if (!rtcPerf.firstFrameAt) {
	          rtcPerf.firstFrameAt = Date.now();
	          updateRtcPerf();
	        }
	        if (!rtcPerf.frameWindowStartMs) {
	          rtcPerf.frameWindowStartMs = nowMs;
	          rtcPerf.frameWindowCount = 0;
	        }
	        rtcPerf.frameWindowCount++;
	        const dt = nowMs - rtcPerf.frameWindowStartMs;
	        if (dt >= 1000) {
	          rtcPerf.fps = (rtcPerf.frameWindowCount * 1000) / dt;
	          rtcPerf.frameWindowStartMs = nowMs;
	          rtcPerf.frameWindowCount = 0;
	          updateRtcPerf();
	        }
	        rtcPerf.frameCbId = rtcRemoteVideo.requestVideoFrameCallback(onFrame);
	      };
	      rtcPerf.frameCbId = rtcRemoteVideo.requestVideoFrameCallback(onFrame);
	    }
	    function setRtcStatus(s) {
	      rtcStatus.textContent = s;
	      if (s === "datachannel: open" && rtcPerf.connectClickAt > 0 && rtcPerf.dcOpenAt === 0) {
	        rtcPerf.dcOpenAt = Date.now();
	        updateRtcPerf();
	      }
	      const open = (s === "datachannel: open");
        if (open) {
          startRtcStatsProbe();
        } else {
          if (rtcPerf.statsTimer) {
            clearInterval(rtcPerf.statsTimer);
            rtcPerf.statsTimer = null;
          }
        }
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
	    function clearRemoteVideo() {
	      if (rtcRemoteVideo) {
	        if (typeof rtcRemoteVideo.cancelVideoFrameCallback === "function" && rtcPerf.frameCbId) {
	          try { rtcRemoteVideo.cancelVideoFrameCallback(rtcPerf.frameCbId); } catch {}
	        }
	        try { rtcRemoteVideo.pause(); } catch {}
	        rtcRemoteVideo.srcObject = null;
	      }
	      if (rtcVideoMeta) {
	        rtcVideoMeta.textContent = "waiting for remote video track...";
	      }
	      if (rtcVideoStability) {
	        rtcVideoStability.textContent = "video stability: n/a";
	      }
	      resetRtcPerf();
	    }
	    function renderVideoStatus(payload) {
	      if (!rtcVideoStability) return;
	      const p = payload || {};
	      const event = String(p.event || "");
        const eventLower = event.toLowerCase();
	      const mode = String(p.mode || "");
	      const detail = String(p.detail || "");
	      const measuredFps = Number(p.measuredFps || 0);
	      const measuredKbps = Number(p.measuredKbps || 0);
	      const targetKbps = Number(p.targetBitrateKbps || p.configuredBitrateKbps || 0);
	      const currentKbps = Number(p.bitrateKbps || p.currentBitrateKbps || 0);
	      const fallbackUsed = !!p.fallbackUsed;
	      const parts = [];
	      if (event) parts.push(event);
	      if (mode) parts.push(`mode=${mode}`);
	      if (measuredFps > 0) parts.push(`fps=${measuredFps.toFixed(1)}`);
	      if (measuredKbps > 0) parts.push(`kbps=${measuredKbps}`);
	      parts.push(`fallback=${fallbackUsed ? "yes" : "no"}`);
	      if (detail) parts.push(detail);
	      rtcVideoStability.textContent = "video stability: " + parts.join(", ");
        let abrTouched = false;
        if (targetKbps > 0) {
          rtcVideoRuntime.abrTargetKbps = targetKbps;
          abrTouched = true;
        }
        if (measuredKbps > 0) {
          rtcVideoRuntime.abrMeasuredKbps = measuredKbps;
          abrTouched = true;
        }
        if (eventLower === "abr_update") {
          if (currentKbps > 0) {
            rtcVideoRuntime.abrCurrentKbps = currentKbps;
            abrTouched = true;
          }
          rtcVideoRuntime.abrReason = detail || String(p.reason || "");
          abrTouched = true;
        }
        if (abrTouched) {
          updateVideoKpiPanel();
        }
	    }
	    function updateRtcModeUi() {
	      const videoMode = getMode() === "video";
	      if (rtcVideoPane) rtcVideoPane.style.display = videoMode ? "block" : "none";
	      const qualityWrap = document.getElementById("rtcQualityWrap");
	      if (qualityWrap) qualityWrap.style.display = videoMode ? "inline-flex" : "none";
	      if (!videoMode) {
	        setRemoteInputEnabled(false);
	        clearRemoteVideo();
	      }
	    }
	    function isVideoRoomId(r) {
	      if (!r) return false;
	      const x = String(r).toLowerCase();
	      return x === "video" || x.startsWith("hb-v-") || x.startsWith("video-");
	    }
	    function getMode() { return (document.getElementById("rtcMode").value || "control"); }
      function applyVideoFitMode() {
        if (!rtcRemoteVideo) return;
        rtcRemoteVideo.style.objectFit = rtcVideoFitMode === "fill" ? "cover" : "contain";
        if (rtcVideoFitFill) rtcVideoFitFill.textContent = rtcVideoFitMode === "fill" ? "Fill" : "Fit";
      }
      function updateVideoKpiPanel() {
        const q = String(rtcPerf.qualityState || "n/a");
        const qCls = q === "good" ? "rtc-quality-good" : (q === "degraded" ? "rtc-quality-degraded" : (q === "bad" ? "rtc-quality-bad" : ""));
        if (rtcKpiQuality) {
          rtcKpiQuality.className = "rtc-kpi-item" + (qCls ? (" " + qCls) : "");
          rtcKpiQuality.textContent = "quality: " + q;
        }
        if (rtcKpiStartup) rtcKpiStartup.textContent = "startup: " + (rtcVideoRuntime.startupMs > 0 ? (Math.round(rtcVideoRuntime.startupMs) + " ms") : "n/a");
        if (rtcKpiFps) rtcKpiFps.textContent = "fps: " + (rtcPerf.fps > 0 ? rtcPerf.fps.toFixed(1) : "n/a");
        if (rtcKpiKbps) rtcKpiKbps.textContent = "kbps: " + (rtcPerf.inboundKbps > 0 ? rtcPerf.inboundKbps.toFixed(0) : "n/a");
        if (rtcKpiAbr) {
          const cur = Number(rtcVideoRuntime.abrCurrentKbps || 0);
          const target = Number(rtcVideoRuntime.abrTargetKbps || 0);
          const measured = Number(rtcVideoRuntime.abrMeasuredKbps || 0);
          const reason = String(rtcVideoRuntime.abrReason || "");
          if (cur > 0 || measured > 0 || target > 0) {
            const val = cur > 0 ? cur : measured;
            const base = target > 0 ? `${val}/${target} kbps` : `${val} kbps`;
            rtcKpiAbr.textContent = "abr: " + base + (reason ? ` (${reason})` : "");
          } else {
            rtcKpiAbr.textContent = "abr: n/a";
          }
        }
        if (rtcKpiCodec) rtcKpiCodec.textContent = "codec: " + (rtcVideoRuntime.codec || getVideoCodec() || "n/a");
        if (rtcKpiEncoder) rtcKpiEncoder.textContent = "encoder: " + (rtcVideoRuntime.encoder || getVideoEncoder() || "n/a");
        if (rtcKpiFallback) rtcKpiFallback.textContent = "fallback: " + (rtcVideoRuntime.fallbackUsed ? "yes" : "no");
      }
      async function toggleVideoFullscreen() {
        if (!rtcRemoteVideo) return;
        try {
          if (document.fullscreenElement === rtcRemoteVideo) {
            await document.exitFullscreen();
          } else {
            await rtcRemoteVideo.requestFullscreen();
          }
        } catch (e) {
          rtcLog({ webrtc: "ui.fullscreen_error", error: String(e) });
        }
      }
      function applyCodecQualityDefaultsOnCodecChange() {
        const codecEl = document.getElementById("rtcVideoCodec");
        const qualityEl = document.getElementById("rtcVideoQuality");
        if (!codecEl || !qualityEl) return;
        const codec = String(codecEl.value || "").toLowerCase();
        const quality = String(qualityEl.value || "").toLowerCase();
        if (codec === "h264" && quality !== "optimal" && quality !== "high") {
          qualityEl.value = "optimal";
          saveRtcVideoPrefs();
          rtcLog({ webrtc: "ui.codec_quality_default", codec: "h264", quality: "optimal" });
          return;
        }
        if (codec === "vp8" && quality === "optimal") {
          qualityEl.value = "balanced";
          saveRtcVideoPrefs();
          rtcLog({ webrtc: "ui.codec_quality_default", codec: "vp8", quality: "balanced" });
        }
      }
      function saveRtcVideoPrefs() {
        try {
          const payload = {
            quality: document.getElementById("rtcVideoQuality")?.value || "balanced",
            imageQuality: document.getElementById("rtcVideoImageQuality")?.value || "",
            bitrateKbps: document.getElementById("rtcVideoBitrateKbps")?.value || "",
            encoder: document.getElementById("rtcVideoEncoder")?.value || "cpu",
            codec: document.getElementById("rtcVideoCodec")?.value || "vp8",
            captureInput: document.getElementById("rtcVideoCaptureInput")?.value || "",
            captureMode: document.getElementById("rtcVideoCaptureMode")?.value || "",
            fitMode: rtcVideoFitMode
          };
          localStorage.setItem("hidbridge_rtc_video_prefs", JSON.stringify(payload));
        } catch {}
      }
      function loadRtcVideoPrefs() {
        try {
          const raw = localStorage.getItem("hidbridge_rtc_video_prefs");
          if (!raw) return null;
          const p = JSON.parse(raw);
          if (!p || typeof p !== "object") return null;
          return p;
        } catch {
          return null;
        }
      }
	    function isLanOrLocalHost(hostname) {
	      const h = String(hostname || "").trim().toLowerCase();
	      if (!h) return false;
	      if (h === "localhost" || h.endsWith(".local")) return true;
	      if (/^\d{1,3}(\.\d{1,3}){3}$/.test(h)) {
	        const p = h.split(".").map(x => parseInt(x, 10));
	        if (p[0] === 127 || p[0] === 10) return true;
	        if (p[0] === 192 && p[1] === 168) return true;
	        if (p[0] === 172 && p[1] >= 16 && p[1] <= 31) return true;
	      }
	      return false;
	    }
	    function applyDefaultVideoQualityForLan() {
	      const el = document.getElementById("rtcVideoQuality");
	      if (!el) return;
	      const host = window.location && window.location.hostname ? window.location.hostname : "";
	      if (isLanOrLocalHost(host)) {
	        // Prefer higher quality by default on LAN/local setups.
	        el.value = "high";
	      }
	    }
	    function getVideoQualityPreset() {
	      const v = (document.getElementById("rtcVideoQuality")?.value || "balanced").trim().toLowerCase();
	      if (v === "low" || v === "low-latency" || v === "balanced" || v === "high" || v === "optimal") return v;
	      return "balanced";
	    }
	    function getVideoEncoder() {
	      const v = (document.getElementById("rtcVideoEncoder")?.value || "cpu").trim().toLowerCase();
	      return v || "cpu";
	    }
	    function getVideoCodec() {
	      const v = (document.getElementById("rtcVideoCodec")?.value || "vp8").trim().toLowerCase();
	      return (v === "h264") ? "h264" : "vp8";
	    }
	    function getVideoBitrateKbps() {
	      const raw = (document.getElementById("rtcVideoBitrateKbps")?.value || "").trim();
	      if (!raw) return null;
	      const n = Number.parseInt(raw, 10);
	      if (!Number.isFinite(n)) return null;
	      return n;
	    }
	    function getVideoImageQuality() {
	      const raw = (document.getElementById("rtcVideoImageQuality")?.value || "").trim();
	      if (!raw) return null;
	      const n = Number.parseInt(raw, 10);
	      if (!Number.isFinite(n)) return null;
	      if (n < 1 || n > 100) return null;
	      return n;
	    }
	    function getSelectedVideoCaptureDevice() {
	      const raw = (document.getElementById("rtcVideoCaptureInput")?.value || "").trim();
	      return raw || null;
	    }
	    function getSelectedVideoCaptureMode() {
	      const raw = (document.getElementById("rtcVideoCaptureMode")?.value || "").trim();
	      return raw || null;
	    }
      function setVideoFieldValue(id, value) {
        const el = document.getElementById(id);
        if (!el) return;
        el.value = value;
      }
      function applyVideoPreset(presetId) {
        const p = String(presetId || "").trim().toLowerCase();
        if (p === "low-latency") {
          setVideoFieldValue("rtcVideoQuality", "low-latency");
          setVideoFieldValue("rtcVideoCodec", "vp8");
          setVideoFieldValue("rtcVideoBitrateKbps", "900");
          setVideoFieldValue("rtcVideoImageQuality", "");
        } else if (p === "balanced") {
          setVideoFieldValue("rtcVideoQuality", "balanced");
          setVideoFieldValue("rtcVideoCodec", "vp8");
          setVideoFieldValue("rtcVideoBitrateKbps", "1200");
          setVideoFieldValue("rtcVideoImageQuality", "");
        } else if (p === "quality") {
          setVideoFieldValue("rtcVideoQuality", "optimal");
          setVideoFieldValue("rtcVideoCodec", "h264");
          setVideoFieldValue("rtcVideoBitrateKbps", "2500");
          setVideoFieldValue("rtcVideoImageQuality", "85");
        } else {
          return;
        }
        saveRtcVideoPrefs();
        rtcLog({ webrtc: "ui.video_preset", preset: p });
      }
      function recommendedPresetFromQualityState(q, startupMs) {
        if (Number.isFinite(startupMs) && startupMs >= 12000) return "low-latency";
        const v = String(q || "").toLowerCase();
        if (v === "bad") return "low-latency";
        if (v === "degraded") return "balanced";
        if (v === "good") return "quality";
        return null;
      }
      function updatePresetHint() {
        if (!rtcPresetHint) return;
        const suggested = recommendedPresetFromQualityState(rtcPerf.qualityState, rtcPerf.runtimeStartupMs || 0);
        rtcPresetHint.textContent = suggested ? (`Suggested: ${suggested}`) : "";
      }
      function autoTuneVideoSettings() {
        if (getMode() !== "video") {
          show({ ok: false, error: "video_mode_required" });
          return;
        }
        const codec = getVideoCodec();
        const currentQuality = getVideoQualityPreset();
        const fps = Number(rtcPerf.fps || 0);
        const kbps = Number(rtcPerf.inboundKbps || 0);
        const startupMs = Number(rtcVideoRuntime.startupMs || rtcPerf.runtimeStartupMs || 0);
        const qualityState = String(rtcPerf.qualityState || "n/a").toLowerCase();

        let currentBitrate = getVideoBitrateKbps();
        if (!Number.isFinite(currentBitrate) || currentBitrate <= 0) {
          if (currentQuality === "optimal") currentBitrate = 2500;
          else if (currentQuality === "high") currentBitrate = 1800;
          else if (currentQuality === "balanced") currentBitrate = 1200;
          else currentBitrate = 900;
        }

        let nextQuality = currentQuality;
        let nextBitrate = currentBitrate;
        let action = "keep";
        let reason = "insufficient_confidence";

        const severe = qualityState === "bad" || (fps > 0 && fps < 20) || (startupMs > 0 && startupMs >= 12000) || (kbps > 0 && kbps < 700);
        const degraded = qualityState === "degraded" || (fps > 0 && fps < 26) || (startupMs > 0 && startupMs >= 8000);
        const good = qualityState === "good" && (fps === 0 || fps >= 28) && (startupMs === 0 || startupMs < 6000);

        if (severe) {
          action = "degrade";
          reason = `severe_quality q=${qualityState} fps=${fps.toFixed(1)} kbps=${kbps.toFixed(0)} startupMs=${Math.round(startupMs)}`;
          nextQuality = "low-latency";
          nextBitrate = Math.max(700, Math.round((currentBitrate * 0.8) / 50) * 50);
        } else if (degraded) {
          action = "stabilize";
          reason = `degraded_quality q=${qualityState} fps=${fps.toFixed(1)} kbps=${kbps.toFixed(0)} startupMs=${Math.round(startupMs)}`;
          nextQuality = "balanced";
          nextBitrate = Math.max(900, Math.min(1800, Math.round(currentBitrate / 50) * 50));
        } else if (good) {
          action = "upgrade";
          reason = `healthy_stream q=${qualityState} fps=${fps.toFixed(1)} kbps=${kbps.toFixed(0)} startupMs=${Math.round(startupMs)}`;
          if (codec === "h264") {
            nextQuality = "optimal";
            nextBitrate = Math.min(4500, Math.max(1800, currentBitrate + 300));
          } else {
            nextQuality = (currentQuality === "low-latency") ? "balanced" : "high";
            nextBitrate = Math.min(2500, currentBitrate + 200);
          }
        }

        if (codec === "vp8" && nextQuality === "optimal") {
          nextQuality = "high";
        }

        setVideoFieldValue("rtcVideoQuality", nextQuality);
        setVideoFieldValue("rtcVideoBitrateKbps", String(nextBitrate));
        saveRtcVideoPrefs();
        updatePresetHint();
        const changed = (nextQuality !== currentQuality) || (nextBitrate !== currentBitrate);
        rtcLog({
          webrtc: "ui.video_auto_tune",
          changed,
          action,
          reason,
          from: { quality: currentQuality, bitrateKbps: currentBitrate, codec },
          to: { quality: nextQuality, bitrateKbps: nextBitrate, codec }
        });
        show({
          ok: true,
          type: "auto_tune",
          changed,
          action,
          reason,
          from: { quality: currentQuality, bitrateKbps: currentBitrate, codec },
          to: { quality: nextQuality, bitrateKbps: nextBitrate, codec },
          hint: "Use Apply now to restart helper with tuned settings."
        });
      }
      function buildVideoRoomRequest(roomOverride) {
        const payload = {
          qualityPreset: getVideoQualityPreset(),
          bitrateKbps: getVideoBitrateKbps(),
          imageQuality: getVideoImageQuality(),
          captureInput: getVideoCaptureInput(),
          encoder: getVideoEncoder(),
          codec: getVideoCodec()
        };
        const room = (roomOverride || "").trim();
        if (room) payload.room = room;
        return payload;
      }
	    function getVideoCaptureInput() {
	      const device = getSelectedVideoCaptureDevice();
	      if (!device) return null;
	      const mode = getSelectedVideoCaptureMode();
	      const args = ["-f", "dshow"];
	      const selectedEncoder = getVideoEncoder();
	      if (mode) {
	        const m = /^(\d+)x(\d+)@(\d+(?:\.\d+)?)(?:\|(.+))?$/.exec(mode);
	        if (m) {
	          const fmt = String(m[4] || "").trim().toLowerCase();
	          if (fmt && fmt !== "unknown") {
	            if (fmt === "mjpeg" || fmt === "h264" || fmt === "hevc" || fmt === "h265" || fmt === "mpeg4") {
	              args.push("-vcodec", fmt);
	            } else {
	              args.push("-pixel_format", fmt);
	            }
	          }
	          args.push("-video_size", `${m[1]}x${m[2]}`);
	          const modeFps = Number.parseFloat(m[3]);
	          let effectiveFps = Number.isFinite(modeFps) ? modeFps : null;
	          if (selectedEncoder === "cpu" && effectiveFps) {
	            // CPU path is more stable with lower ingest FPS on common USB capture cards.
	            effectiveFps = Math.min(effectiveFps, 30);
	          }
	          if (effectiveFps && Number.isFinite(effectiveFps)) {
	            const fpsInt = Math.max(5, Math.min(60, Math.round(effectiveFps)));
	            args.push("-framerate", String(fpsInt));
	          }
	        }
	      }
	      const escaped = device.replace(/"/g, '\\"');
	      args.push("-i", `"video=${escaped}"`);
	      return args.join(" ");
	    }
	    let rtcEncoderRefreshSeq = 0;
	    async function refreshVideoCaptureModes() {
	      const sel = document.getElementById("rtcVideoCaptureMode");
	      if (!sel) return;
	      const prev = (sel.value || "").trim();
	      const device = getSelectedVideoCaptureDevice();
	      sel.innerHTML = "";
	      const defaultOpt = document.createElement("option");
	      defaultOpt.value = "";
	      defaultOpt.textContent = "auto (server default)";
	      sel.appendChild(defaultOpt);
	      if (!device) return;
	      try {
	        const res = await fetch(`/api/video/dshow/modes?device=${encodeURIComponent(device)}`);
	        if (!res.ok) return;
	        const j = await res.json().catch(() => null);
	        const list = (j && j.ok && Array.isArray(j.modes)) ? j.modes : [];
	        for (const mode of list) {
	          const w = Number.parseInt(String((mode && (mode.Width ?? mode.width)) ?? ""), 10);
	          const h = Number.parseInt(String((mode && (mode.Height ?? mode.height)) ?? ""), 10);
	          const fps = Number.parseFloat(String((mode && (mode.MaxFps ?? mode.maxFps)) ?? ""));
	          const fmt = String((mode && (mode.Format ?? mode.format)) ?? "unknown").trim().toLowerCase() || "unknown";
	          if (!Number.isFinite(w) || !Number.isFinite(h) || !Number.isFinite(fps)) continue;
	          const fpsText = Number.isInteger(fps) ? String(Math.trunc(fps)) : String(fps);
	          const value = `${w}x${h}@${fpsText}|${fmt}`;
	          const opt = document.createElement("option");
	          opt.value = value;
	          opt.textContent = `${w}x${h} @ ${fpsText} fps [${fmt}]`;
	          sel.appendChild(opt);
	        }
	      } catch {}
	      if (prev && Array.from(sel.options).some(o => o.value === prev)) {
	        sel.value = prev;
	      }
	      const prefs = loadRtcVideoPrefs();
	      if (prefs && prefs.captureMode && Array.from(sel.options).some(o => o.value === String(prefs.captureMode))) {
	        sel.value = String(prefs.captureMode);
	      }
	      saveRtcVideoPrefs();
	      await refreshVideoEncoders();
	    }
	    async function refreshVideoEncoders() {
	      const seq = ++rtcEncoderRefreshSeq;
	      const sel = document.getElementById("rtcVideoEncoder");
	      if (!sel) return;
	      const prev = (sel.value || "").trim().toLowerCase();
	      sel.innerHTML = "";
	      try {
	        const res = await fetch("/api/video/webrtc/encoders");
	        if (seq !== rtcEncoderRefreshSeq) return;
	        if (res.ok) {
	          const j = await res.json().catch(() => null);
	          if (seq !== rtcEncoderRefreshSeq) return;
	          const list = (j && j.ok && Array.isArray(j.encoders)) ? j.encoders : [];
	          const seen = new Set();
	          for (const enc of list) {
	            const id = String((enc && (enc.id || enc.Id)) || "").trim().toLowerCase();
	            if (!id) continue;
	            if (seen.has(id)) continue;
	            seen.add(id);
	            const label = String((enc && (enc.label || enc.Label)) || id).trim();
	            const opt = document.createElement("option");
	            opt.value = id;
	            opt.textContent = (id === "cpu") ? "CPU (software)" : label;
	            sel.appendChild(opt);
	          }
	        }
	      } catch {}
	      if (seq !== rtcEncoderRefreshSeq) return;
	      if (sel.options.length === 0) {
	        const opt = document.createElement("option");
	        opt.value = "cpu";
	        opt.textContent = "CPU (software)";
	        sel.appendChild(opt);
	      }
	      if (prev && Array.from(sel.options).some(o => o.value.toLowerCase() === prev)) {
	        sel.value = prev;
	      } else {
	        sel.value = sel.options[0].value;
	      }
	      const prefs = loadRtcVideoPrefs();
	      if (prefs && prefs.encoder && Array.from(sel.options).some(o => o.value.toLowerCase() === String(prefs.encoder).toLowerCase())) {
	        sel.value = String(prefs.encoder);
	      }
	      saveRtcVideoPrefs();
	    }
	    async function refreshVideoCaptureDevices() {
	      const sel = document.getElementById("rtcVideoCaptureInput");
	      if (!sel) return;
	      const prev = (sel.value || "").trim();
	      sel.innerHTML = "";
	      const defaultOpt = document.createElement("option");
	      defaultOpt.value = "";
	      defaultOpt.textContent = "(server default)";
	      sel.appendChild(defaultOpt);
	      try {
	        const res = await fetch("/api/video/dshow/devices");
	        if (!res.ok) return;
	        const j = await res.json().catch(() => null);
	        const list = (j && j.ok && Array.isArray(j.devices)) ? j.devices : [];
	        for (const dev of list) {
	          const name = (typeof dev === "string")
	            ? dev.trim()
	            : String((dev && (dev.Name || dev.name || dev.DeviceName || dev.deviceName || dev.Label || dev.label)) || "").trim();
	          if (!name) continue;
	          const alt = (typeof dev === "object" && dev)
	            ? String(dev.AlternativeName || dev.alternativeName || "").trim()
	            : "";
	          const opt = document.createElement("option");
	          opt.value = name;
	          opt.textContent = alt ? `${name} (${alt})` : name;
	          sel.appendChild(opt);
	        }
	      } catch {}
	      if (prev && Array.from(sel.options).some(o => o.value === prev)) {
	        sel.value = prev;
	      }
	      const prefs = loadRtcVideoPrefs();
	      if (prefs && prefs.captureInput && Array.from(sel.options).some(o => o.value === String(prefs.captureInput))) {
	        sel.value = String(prefs.captureInput);
	      }
	      await refreshVideoCaptureModes();
	    }
	    function getRoom() { return (document.getElementById("rtcRoom").value.trim() || "control"); }
	    function setRoom(r) {
	      if (!r) return;
	      document.getElementById("rtcRoom").value = r;
	      document.getElementById("rtcMode").value = isVideoRoomId(r) ? "video" : "control";
	      updateRtcModeUi();
	      resetWebRtcClient();
	      rtcLog({ webrtc: "ui.room_selected", room: r });
	      if (isVideoRoomId(r)) {
	        refreshVideoPeerRuntime(r);
	      }
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
	      clearRemoteVideo();
	      webrtcClient = factory({
	        room: getRoom(),
	        iceServers: getIceServers(),
	        iceTransportPolicy: rtcRelayOnly.checked ? "relay" : "all",
	        joinTimeoutMs: rtcCfg.joinTimeoutMs,
	        receiveVideo: mode === "video",
	        onLog: rtcLog,
	        onStatus: setRtcStatus,
	        onMessage: (data) => {
	          try {
	            const parsed = (typeof data === "string") ? JSON.parse(data) : data;
	            if (parsed && parsed.type === "video.status") {
	              renderVideoStatus(parsed);
	              rtcLog({ webrtc: "video.status", payload: parsed });
	              return;
	            }
	          } catch {}
	          show({ webrtc: "message", data });
	        },
	        onTrack: (ev) => {
	          try {
	            const stream = (ev && ev.streams && ev.streams.length > 0)
	              ? ev.streams[0]
	              : new MediaStream(ev && ev.track ? [ev.track] : []);
	            if (rtcRemoteVideo) {
	              rtcRemoteVideo.srcObject = stream;
	              rtcRemoteVideo.play().catch(() => { });
	            }
	            if (rtcVideoMeta) {
	              const kind = (ev && ev.track && ev.track.kind) ? ev.track.kind : "unknown";
	              rtcVideoMeta.textContent = "remote track: " + kind;
	            }
	            if (ev && ev.track && ev.track.kind === "video" && rtcPerf.connectClickAt > 0 && rtcPerf.trackAt === 0) {
	              rtcPerf.trackAt = Date.now();
	              updateRtcPerf();
	            }
	            startVideoPerfProbe();
	          } catch (e) {
	            rtcLog({ webrtc: "ui.track_error", error: String(e) });
	          }
	        }
	      });
	    }
	    applyDefaultVideoQualityForLan();
	    const rtcLoadedPrefs = loadRtcVideoPrefs();
	    if (rtcLoadedPrefs) {
	      if (document.getElementById("rtcVideoQuality")) document.getElementById("rtcVideoQuality").value = String(rtcLoadedPrefs.quality || document.getElementById("rtcVideoQuality").value);
	      if (document.getElementById("rtcVideoImageQuality")) document.getElementById("rtcVideoImageQuality").value = String(rtcLoadedPrefs.imageQuality || "");
	      if (document.getElementById("rtcVideoBitrateKbps")) document.getElementById("rtcVideoBitrateKbps").value = String(rtcLoadedPrefs.bitrateKbps || "");
	      if (document.getElementById("rtcVideoCodec")) document.getElementById("rtcVideoCodec").value = String(rtcLoadedPrefs.codec || document.getElementById("rtcVideoCodec").value);
	      rtcVideoFitMode = (String(rtcLoadedPrefs.fitMode || "fit").toLowerCase() === "fill") ? "fill" : "fit";
	    }
      applyVideoFitMode();
	    updateRtcModeUi();
	    resetWebRtcClient();
	    setRemoteInputEnabled(false);
	    if (rtcVideoFullscreen) {
	      rtcVideoFullscreen.addEventListener("click", async () => {
          await toggleVideoFullscreen();
        });
	    }
      if (rtcVideoFitFill) {
        rtcVideoFitFill.addEventListener("click", () => {
          rtcVideoFitMode = rtcVideoFitMode === "fit" ? "fill" : "fit";
          applyVideoFitMode();
          saveRtcVideoPrefs();
        });
      }
      if (rtcPresetLowLatency) rtcPresetLowLatency.addEventListener("click", () => applyVideoPreset("low-latency"));
      if (rtcPresetBalanced) rtcPresetBalanced.addEventListener("click", () => applyVideoPreset("balanced"));
      if (rtcPresetQuality) rtcPresetQuality.addEventListener("click", () => applyVideoPreset("quality"));
      if (rtcAutoTune) rtcAutoTune.addEventListener("click", () => autoTuneVideoSettings());
      if (rtcApplyNow) rtcApplyNow.addEventListener("click", async () => {
        try {
          if (getMode() !== "video") {
            show({ ok: false, error: "video_mode_required" });
            return;
          }
          const room = getRoom();
          if (!isVideoRoomId(room)) {
            show({ ok: false, error: "video_room_required", room });
            return;
          }
          const res = await fetch(`/api/webrtc/video/rooms/${encodeURIComponent(room)}/apply`, {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify(buildVideoRoomRequest(room))
          });
          const j = await res.json().catch(() => null);
          rtcLog({ webrtc: "ui.video_apply_now", room, payload: j });
          show(j || { ok: false, error: "apply_failed" });
          await refreshRooms();
          if (j && j.ok) {
            const isActiveRoom = String(getRoom() || "").toLowerCase() === room.toLowerCase();
            const isLive = rtcStatus.textContent === "datachannel: open" || rtcStatus.textContent === "calling...";
            if (isActiveRoom && isLive) {
              resetWebRtcClient();
              clearRemoteVideo();
              setRtcStatus("disconnected");
              await new Promise(r => setTimeout(r, 250));
              document.getElementById("rtcConnect").click();
            }
          }
        } catch (e) {
          rtcLog({ webrtc: "ui.video_apply_now_error", error: String(e) });
          show({ ok: false, error: String(e) });
        }
      });
	    if (rtcVideoInputToggle) {
	      rtcVideoInputToggle.addEventListener("click", async () => { await setRemoteInputEnabled(!rtcRemoteInputEnabled); });
	    }
	    if (rtcRemoteVideo) {
	      rtcRemoteVideo.addEventListener("click", async () => {
          if (!rtcRemoteInputEnabled || getMode() !== "video") return;
          rtcRemoteInputFocused = true;
	        try { rtcRemoteVideo.focus(); } catch {}
          try { if (document.pointerLockElement !== rtcRemoteVideo) await rtcRemoteVideo.requestPointerLock(); } catch {}
          updateRemoteInputUiState();
	      });
	      rtcRemoteVideo.addEventListener("contextmenu", (e) => {
	        if (rtcRemoteInputEnabled) e.preventDefault();
	      });
      rtcRemoteVideo.addEventListener("mousemove", (e) => {
        if (!canHandleRemoteInput()) return;
        const mx = Number.isFinite(e.movementX) ? e.movementX : (rtcLastMouseX == null ? 0 : (e.clientX - rtcLastMouseX));
        const my = Number.isFinite(e.movementY) ? e.movementY : (rtcLastMouseY == null ? 0 : (e.clientY - rtcLastMouseY));
        rtcLastMouseX = e.clientX;
        rtcLastMouseY = e.clientY;
        queueMouseMove(mx, my);
      });
	      rtcRemoteVideo.addEventListener("mouseleave", () => {
	        rtcLastMouseX = null;
	        rtcLastMouseY = null;
	      });
        rtcRemoteVideo.addEventListener("blur", () => {
          if (!rtcRemoteInputEnabled || getMode() !== "video") return;
          if (document.pointerLockElement === rtcRemoteVideo) return;
          rtcRemoteInputFocused = false;
          updateRemoteInputUiState();
          releaseHeldRemoteInput();
	      });
	    }

      document.addEventListener("pointerlockchange", () => {
        rtcRemoteInputFocused = document.pointerLockElement === rtcRemoteVideo;
        updateRemoteInputUiState();
        if (!rtcRemoteInputFocused) releaseHeldRemoteInput();
      });

      window.addEventListener("mousedown", (e) => {
        if (!canHandleRemoteInput()) return;
        const button = mouseButtonName(e.button);
        if (!button) return;
        e.preventDefault();
        rtcHeldButtons.add(button);
        postQuiet("/api/mouse/button", { button, down: true });
      }, { capture: true });

      window.addEventListener("mouseup", (e) => {
        if (!canHandleRemoteInput()) return;
        const button = mouseButtonName(e.button);
        if (!button) return;
        e.preventDefault();
        rtcHeldButtons.delete(button);
        postQuiet("/api/mouse/button", { button, down: false });
      }, { capture: true });

      window.addEventListener("wheel", (e) => {
        if (!canHandleRemoteInput()) return;
        e.preventDefault();
        const direction = Math.sign(e.deltaY);
        if (direction !== 0) postQuiet("/api/mouse/wheel", { delta: direction });
      }, { passive: false, capture: true });

      // Global fullscreen shortcut for video preview.
      window.addEventListener("keydown", async (e) => {
        if (!(e.ctrlKey && e.altKey && e.code === "Enter")) return;
        if (getMode() !== "video") return;
        const tag = String((document.activeElement && document.activeElement.tagName) || "").toLowerCase();
        if (tag === "input" || tag === "textarea" || tag === "select") return;
        e.preventDefault();
        e.stopPropagation();
        await toggleVideoFullscreen();
      }, { capture: true });

      window.addEventListener("keydown", async (e) => {
        if (!canHandleRemoteInput()) return;
        if (!(e.code in codeToUsage)) return;
        if (e.repeat && rtcPressedCodes.has(e.code)) return;
        e.preventDefault();
        e.stopPropagation();
        rtcPressedCodes.add(e.code);
        await syncKeyboardReport();
      }, { capture: true });

      window.addEventListener("keyup", async (e) => {
        if (!canHandleRemoteInput()) return;
        if (!(e.code in codeToUsage)) return;
        e.preventDefault();
        e.stopPropagation();
        rtcPressedCodes.delete(e.code);
        await syncKeyboardReport();
      }, { capture: true });

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
          const activeRoom = getRoom().toLowerCase();
          const statusNow = (rtcStatus && rtcStatus.textContent ? rtcStatus.textContent : "").trim().toLowerCase();
          const activeSession = !!statusNow && !statusNow.startsWith("disconnected") && !statusNow.startsWith("error:");
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
	          const canRestart = isVideo && !!r.hasHelper;
	          const canConnect = status !== "busy" || room.toLowerCase() === getRoom().toLowerCase();
            const canHangup = room.toLowerCase() === activeRoom && activeSession;
            const restartBtn = isVideo
              ? `<button data-act="restart" data-room="${room}" ${canRestart ? "" : "disabled"} title="${canRestart ? "restart helper for this video room" : "helper is not running"}">Restart</button>`
              : "";

	          const tr = document.createElement("tr");
	          tr.innerHTML = `
	            <td style="padding: 6px 4px; font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, 'Liberation Mono', 'Courier New', monospace">${room}</td>
	            <td style="padding: 6px 4px"><code>${kind}</code></td>
	            <td style="padding: 6px 4px">${r.peers}</td>
	            <td style="padding: 6px 4px" class="muted">${tags.join(", ") || "-"}</td>
	            <td style="padding: 6px 4px">${status}</td>
	            <td style="padding: 6px 4px">
	              <button data-act="start" data-room="${room}" ${canStart ? "" : "disabled"} title="${canStart ? "start helper for this room" : (r.hasHelper ? "helper already started" : "room is busy")}">Start</button>
                ${restartBtn}
	              <button data-act="use" data-room="${room}">Use</button>
	              <button data-act="connect" data-room="${room}" ${canConnect ? "" : "disabled"} title="${canConnect ? "connect using this room" : "room is busy"}">Connect</button>
	              <button data-act="hangup" data-room="${room}" ${canHangup ? "" : "disabled"} title="${canHangup ? "disconnect current WebRTC session" : "no active session for this room"}">Hangup</button>
	              <button data-act="delete" data-room="${room}" ${canDelete ? "" : "disabled"} title="${canDelete ? "stop helper for this room" : "default room cannot be deleted"}">Delete</button>
	            </td>
	          `;
	          body.appendChild(tr);
	        }
	      } catch {}
	      if (getMode() === "video") {
	        await refreshVideoPeerRuntime(getRoom());
	      }
	    }

      function classifyVideoError(errText) {
        const e = String(errText || "").toLowerCase();
        if (!e) return "";
        if (e.includes("device already in use") || e.includes("could not run graph") || e.includes("device_busy")) return "device_busy";
        if (e.includes("invalid argument") || e.includes("error parsing options") || e.includes("ffmpeg args")) return "ffmpeg_args_invalid";
        if (e.includes("signaling") || e.includes("timeout") || e.includes("refused")) return "signaling_timeout";
        return "unknown";
      }

	    function renderVideoPeerRuntime(j) {
	      if (!rtcVideoMeta) return;
	      if (!j || !j.ok) {
	        const runtimeError = (j && j.error) ? String(j.error) : "n/a";
          const errAt = formatLocalDateTime(new Date());
	        rtcVideoMeta.textContent = `video runtime: error=${runtimeError}${errAt ? ` last-error-at=${errAt}` : ""}`;
	        if (rtcVideoStability) rtcVideoStability.textContent = "video stability: n/a";
          rtcPerf.runtimeStartupMs = 0;
          rtcVideoRuntime.running = false;
          rtcVideoRuntime.fallbackUsed = false;
          rtcVideoRuntime.startupMs = 0;
          rtcVideoRuntime.codec = "";
          rtcVideoRuntime.encoder = "";
          rtcVideoRuntime.startupTimeoutReason = runtimeError === "n/a" ? "" : runtimeError;
          rtcVideoRuntime.lastErrorAt = runtimeError === "n/a" ? "" : errAt;
          rtcVideoRuntime.abrCurrentKbps = 0;
          rtcVideoRuntime.abrTargetKbps = 0;
          rtcVideoRuntime.abrMeasuredKbps = 0;
          rtcVideoRuntime.abrReason = "";
          updateVideoKpiPanel();
          updatePresetHint();
	        return;
	      }
	      const modeReq = j.sourceModeRequested || "?";
	      const modeAct = j.sourceModeActive || "?";
	      const running = j.running ? "running" : "stopped";
	      const fallback = j.fallbackUsed ? "fallback=yes" : "fallback=no";
        const startupMs = Number(j.startupMs || 0);
        const startupTimeoutReason = String(j.startupTimeoutReason || "").trim();
        const errClass = classifyVideoError(j.lastVideoError);
	      const err = j.lastVideoError ? ` error=${j.lastVideoError}` : "";
        const hasErr = !!(j.lastVideoError || startupTimeoutReason);
        const errAtSrc = j.updatedAtUtc || new Date();
        const errAt = hasErr ? formatLocalDateTime(errAtSrc) : "";
        const errClassText = errClass ? ` class=${errClass}` : "";
        const startupReasonText = startupTimeoutReason ? ` startup-timeout=${startupTimeoutReason}` : "";
	      rtcVideoMeta.textContent = `video runtime: ${running}, mode=${modeAct} (requested=${modeReq}), ${fallback}${startupMs > 0 ? ` startup=${Math.round(startupMs)}ms` : ""}${startupReasonText}${errClassText}${err}${errAt ? ` last-error-at=${errAt}` : ""}`;
        rtcPerf.runtimeStartupMs = startupMs > 0 ? startupMs : 0;
        rtcVideoRuntime.running = !!j.running;
        rtcVideoRuntime.fallbackUsed = !!j.fallbackUsed;
        rtcVideoRuntime.startupMs = startupMs > 0 ? startupMs : 0;
        rtcVideoRuntime.codec = String(j.codec || "").trim().toLowerCase();
        rtcVideoRuntime.encoder = String(j.encoder || "").trim().toLowerCase();
        rtcVideoRuntime.startupTimeoutReason = startupTimeoutReason;
        rtcVideoRuntime.lastErrorAt = errAt;
        rtcVideoRuntime.abrTargetKbps = Number(j.targetBitrateKbps || rtcVideoRuntime.abrTargetKbps || 0);
        rtcVideoRuntime.abrMeasuredKbps = Number(j.measuredKbps || rtcVideoRuntime.abrMeasuredKbps || 0);
        updateVideoKpiPanel();
        updatePresetHint();
	      if (rtcVideoStability) {
	        const parts = [];
	        if (typeof j.measuredFps === "number" && j.measuredFps > 0) parts.push(`fps=${j.measuredFps.toFixed(1)}`);
	        if (typeof j.measuredKbps === "number" && j.measuredKbps > 0) parts.push(`kbps=${j.measuredKbps}`);
	        if (typeof j.targetFps === "number" && j.targetFps > 0) parts.push(`target-fps=${j.targetFps}`);
	        if (typeof j.targetBitrateKbps === "number" && j.targetBitrateKbps > 0) parts.push(`target-kbps=${j.targetBitrateKbps}`);
	        if (typeof j.frames === "number" && j.frames > 0) parts.push(`frames=${j.frames}`);
	        if (typeof j.packets === "number" && j.packets > 0) parts.push(`packets=${j.packets}`);
          if (startupMs > 0) parts.push(`startup-ms=${Math.round(startupMs)}`);
	        parts.push(`fallback=${j.fallbackUsed ? "yes" : "no"}`);
	        rtcVideoStability.textContent = "video stability: " + parts.join(", ");
	      }
	    }

	    async function refreshVideoPeerRuntime(room) {
	      const target = (room || "").trim();
	      if (!isVideoRoomId(target)) return;
	      try {
	        const res = await fetch("/api/webrtc/video/peers/" + encodeURIComponent(target));
	        if (!res.ok) return;
	        const j = await res.json().catch(() => null);
	        renderVideoPeerRuntime(j);
	        rtcLog({ webrtc: "ui.video_runtime", room: target, payload: j });
	      } catch {}
	    }

	    async function getVideoRuntimeState(room) {
	      const target = (room || "").trim();
	      if (!isVideoRoomId(target)) return null;
	      try {
	        const res = await fetch("/api/webrtc/video/peers/" + encodeURIComponent(target), { cache: "no-store" });
	        if (!res.ok) return null;
	        const j = await res.json().catch(() => null);
	        if (!j || !j.ok) return null;
	        return {
	          running: !!j.running,
	          fallbackUsed: !!j.fallbackUsed,
	          sourceModeActive: j.sourceModeActive || null
	        };
	      } catch {
	        return null;
	      }
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
	          const res = await fetch(prefix, {
	            method: "POST",
	            headers: { "Content-Type": "application/json" },
	            body: JSON.stringify(buildVideoRoomRequest(room))
	          });
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
	      if (act === "restart") {
	        try {
	          const prefix = getRoomsApiPrefix(room);
            if (getRoom().toLowerCase() === room.toLowerCase()) {
              resetWebRtcClient();
              clearRemoteVideo();
              setRtcStatus("disconnected");
            }
            const delRes = await fetch(prefix + "/" + encodeURIComponent(room), { method: "DELETE" });
            const delJson = await delRes.json().catch(() => null);
            rtcLog({ webrtc: "ui.room_restart_stop", room, endpoint: prefix, payload: delJson });
            const startRes = await fetch(prefix, {
              method: "POST",
              headers: { "Content-Type": "application/json" },
              body: JSON.stringify(buildVideoRoomRequest(room))
            });
            const startJson = await startRes.json().catch(() => null);
            rtcLog({ webrtc: "ui.room_restart_start", room, endpoint: prefix, payload: startJson });
            show(startJson || { ok: false, error: "restart_failed" });
	          await refreshRooms();
	        } catch (e) {
	          rtcLog({ webrtc: "ui.room_restart_error", room, error: String(e) });
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
	      if (act === "hangup") {
	        if (getRoom().toLowerCase() !== room.toLowerCase()) {
            setRoom(room);
          }
          document.getElementById("rtcHangup").click();
          await refreshRooms();
	        return;
	      }
	      if (act === "delete") {
	        try {
	          const prefix = getRoomsApiPrefix(room);
	          const res = await fetch(prefix + "/" + encodeURIComponent(room), { method: "DELETE" });
	          const j = await res.json().catch(() => null);
	          rtcLog({ webrtc: "ui.room_deleted", room, endpoint: prefix, payload: j });
	          show(j || { ok: false, error: "delete_failed" });
	          if (j && j.ok) {
	            // If we deleted the room currently bound to the client, disconnect immediately so
	            // the room does not reappear from our own active peer session.
	            if (getRoom().toLowerCase() === room.toLowerCase()) {
	              resetWebRtcClient();
	              clearRemoteVideo();
	              setRtcStatus("disconnected");
	              // Keep UX predictable by switching to default room after deletion.
	              setRoom("control");
	            }
	            // Remove stale row immediately; refresh below will reconcile authoritative state.
	            const rowBtn = document.querySelector(`#rtcRoomsBody button[data-act="delete"][data-room="${CSS.escape(room)}"]`);
	            const tr = rowBtn ? rowBtn.closest("tr") : null;
	            if (tr && tr.parentNode) tr.parentNode.removeChild(tr);
	          }
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
	        const body = (mode === "video")
	          ? buildVideoRoomRequest()
	          : {};
	        const res = await fetch(createUrl, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) });
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
	    refreshVideoCaptureDevices();
	    refreshRooms();
      document.getElementById("rtcVideoQuality")?.addEventListener("change", saveRtcVideoPrefs);
      document.getElementById("rtcVideoImageQuality")?.addEventListener("input", saveRtcVideoPrefs);
      document.getElementById("rtcVideoBitrateKbps")?.addEventListener("input", saveRtcVideoPrefs);
      document.getElementById("rtcVideoEncoder")?.addEventListener("change", saveRtcVideoPrefs);
      document.getElementById("rtcVideoCodec")?.addEventListener("change", () => {
        applyCodecQualityDefaultsOnCodecChange();
        saveRtcVideoPrefs();
      });
	    document.getElementById("rtcVideoCaptureInput")?.addEventListener("change", () => {
	      saveRtcVideoPrefs();
	      refreshVideoCaptureModes();
	    });
	    document.getElementById("rtcVideoCaptureMode")?.addEventListener("change", () => {
	      saveRtcVideoPrefs();
	      refreshVideoEncoders();
	    });
	    document.getElementById("rtcMode").addEventListener("change", () => {
	      updateRtcModeUi();
	      resetWebRtcClient();
	      if (getMode() === "video") {
	        refreshVideoCaptureDevices();
	        refreshVideoEncoders();
	      }
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
	        const recoverVideoTimeout = async () => {
	          if (!isVideoRoomId(wanted)) return false;
	          for (let i = 0; i < 8; i++) {
	            const runtime = await getVideoRuntimeState(wanted);
	            if (runtime && runtime.running) {
	              rtcLog({ webrtc: "ui.ensure_helper_timeout_recovered_runtime", room: wanted, attempt: i + 1 });
	              return true;
	            }
	            const rr = await getRoomState(wanted);
	            if (rr && rr.hasHelper) {
	              rtcLog({ webrtc: "ui.ensure_helper_timeout_recovered_roomstate", room: wanted, attempt: i + 1 });
	              return true;
	            }
	            await new Promise(r => setTimeout(r, 500));
	          }
	          return false;
	        };
	        if (isVideoRoomId(wanted)) {
	          const runtime = await getVideoRuntimeState(wanted);
	          if (runtime && runtime.running) {
	            rtcLog({ webrtc: "ui.ensure_helper_runtime", room: wanted, endpoint: prefix, runtime });
	            return true;
	          }
	        }

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
	          const createRes = await fetch(prefix, {
	            method: "POST",
	            headers: { "Content-Type": "application/json" },
	            body: JSON.stringify(buildVideoRoomRequest(wanted))
	          });
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

	        // Fast path for non-default rooms: if helper is already present, avoid POST and continue immediately.
	        try {
	          const listRes = await fetch(prefix);
	          const list = await listRes.json().catch(() => null);
	          if (list && list.ok && Array.isArray(list.rooms)) {
	            const rr = list.rooms.find(x => x && x.room && String(x.room).toLowerCase() === wantedLc);
	            if (rr && rr.hasHelper) {
	              rtcLog({ webrtc: "ui.ensure_helper_existing", room: wanted, endpoint: prefix });
	              return true;
	            }
	          }
	          if (isVideoRoomId(wanted)) {
	            const runtime = await getVideoRuntimeState(wanted);
	            if (runtime && runtime.running) {
	              rtcLog({ webrtc: "ui.ensure_helper_runtime_existing", room: wanted, endpoint: prefix, runtime });
	              return true;
	            }
	          }
	        } catch {}

	        const body = isVideoRoomId(wanted)
	          ? buildVideoRoomRequest(wanted)
	          : { room: wanted };
	        const res = await fetch(prefix, {
	          method: "POST",
	          headers: { "Content-Type": "application/json" },
	          body: JSON.stringify(body)
	        });
	        const j = await res.json().catch(() => null);
	        rtcLog({ webrtc: "ui.ensure_helper", room: wanted, endpoint: prefix, payload: j });
	        if (j && j.ok && j.room && String(j.room).toLowerCase() === wantedLc) return true;
	        if (j && !j.ok && j.error === "timeout" && await recoverVideoTimeout()) return true;
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

	    async function getRoomState(room) {
	      const wanted = (room || "").toLowerCase();
	      const prefix = getRoomsApiPrefix(room);
	      try {
	        const res = await fetch(prefix);
	        if (!res.ok) return null;
	        const j = await res.json().catch(() => null);
	        if (!j || !j.ok || !Array.isArray(j.rooms)) return null;
	        const rr = j.rooms.find(x => x && x.room && String(x.room).toLowerCase() === wanted);
	        if (!rr) return null;
	        return {
	          hasHelper: !!rr.hasHelper,
	          peers: Number.isFinite(rr.peers) ? rr.peers : 0
	        };
	      } catch {
	        return null;
	      }
	    }

	    async function waitForHelperPeer(room, timeoutMs) {
	      const start = Date.now();
	      // Fast path to avoid unnecessary waiting when helper is already in room.
	      const first = await getRoomState(room);
	      if (first && first.hasHelper && first.peers >= 1) return true;
	      if (isVideoRoomId(room)) {
	        const runtime = await getVideoRuntimeState(room);
	        if (runtime && runtime.running) return true;
	      }
	      while (Date.now() - start < timeoutMs) {
	        const rr = await getRoomState(room);
	        if (rr && rr.hasHelper && rr.peers >= 1) return true;
	        if (isVideoRoomId(room)) {
	          const runtime = await getVideoRuntimeState(room);
	          if (runtime && runtime.running) return true;
	        }
	        const elapsed = Date.now() - start;
	        const sleepMs = timeoutMs <= 4000 ? 50 : (elapsed < 3000 ? 100 : 150);
	        await new Promise(r => setTimeout(r, sleepMs));
	      }
	      return false;
	    }

	    function getHelperReadyTimeoutMs(room, helperAlreadyPresent) {
	      const r = (room || "").toLowerCase();
	      const base = Math.max(1000, rtcCfg.connectTimeoutMs || 0);
	      if (helperAlreadyPresent) {
	        // Warm start: helper process is already visible in room list.
	        return Math.min(8000, Math.max(2000, Math.floor(base * 0.8)));
	      }
	      // Cold start: helper may need process spawn/build warm-up.
	      if (r === "video" || r.startsWith("hb-v-") || r.startsWith("video-")) {
	        return Math.min(60000, Math.max(20000, base * 4));
	      }
	      return Math.min(30000, Math.max(10000, base * 2));
	    }

	    document.getElementById("rtcStartHelper").addEventListener("click", async () => {
	      const room = getRoom();
	      const before = await getRoomState(room);
	      const ok = await ensureHelper(room);
	      if (ok) {
	        const ready = await waitForHelperPeer(room, getHelperReadyTimeoutMs(room, !!(before && before.hasHelper)));
	        rtcLog({ webrtc: "ui.helper_ready", room, ready });
	      }
	      await refreshRooms();
	    });

	    document.getElementById("rtcConnect").addEventListener("click", async () => {
        if (rtcConnectInFlight || rtcHangupInFlight) {
          rtcLog({ webrtc: "ui.connect_ignored_busy" });
          return;
        }
        rtcConnectInFlight = true;
        setRtcMainActionBusy();
	      try {
	        resetWebRtcClient();
	        if (!webrtcClient) return;
	        const room = getRoom();
	        const roomBeforeEnsure = await getRoomState(room);
	        rtcPerf.connectClickAt = Date.now();
	        updateRtcPerf();
	        // If the room has no helper yet, start it before calling (gives a consistent UX for generated rooms).
	        const helperOk = await ensureHelper(room);
	        if (!helperOk) return;
	        // Important: signaling is a relay. If we send an offer before the helper joins the room, it will be lost.
	        const helperReadyTimeoutMs = getHelperReadyTimeoutMs(room, !!(roomBeforeEnsure && roomBeforeEnsure.hasHelper));
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
	        rtcPerf.helperReadyAt = Date.now();
	        updateRtcPerf();
	        rtcPerf.callAt = Date.now();
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
	      } finally {
          rtcConnectInFlight = false;
          setRtcMainActionBusy();
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
        if (rtcConnectInFlight || rtcHangupInFlight) {
          rtcLog({ webrtc: "ui.hangup_ignored_busy" });
          return;
        }
        rtcHangupInFlight = true;
        setRtcMainActionBusy();
	      try {
	        if (webrtcClient) webrtcClient.hangup();
	        setRemoteInputEnabled(false);
	        clearRemoteVideo();
	        setRtcStatus("disconnected");
	      } finally {
          rtcHangupInFlight = false;
          setRtcMainActionBusy();
        }
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
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
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

app.MapGet("/api/webrtc/video/peers/{room}", async (string room, CancellationToken ct) =>
{
    try
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        if (!string.IsNullOrWhiteSpace(token)) http.DefaultRequestHeaders.Add("X-HID-Token", token);
        string url = $"{serverUrl.TrimEnd('/')}/status/webrtc/video/peers/{Uri.EscapeDataString(room)}";
        using var resp = await http.GetAsync(url, ct);
        string body = await resp.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(body))
        {
            body = JsonSerializer.Serialize(new { ok = false, room, error = "empty_response" });
        }
        return Results.Content(body, "application/json", statusCode: (int)resp.StatusCode);
    }
    catch (TaskCanceledException)
    {
        return Results.Json(new { ok = false, room, error = "timeout" });
    }
    catch (Exception ex)
    {
        return Results.Json(new { ok = false, room, error = ex.Message });
    }
});

app.MapGet("/api/video/dshow/devices", async (CancellationToken ct) =>
{
    try
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        if (!string.IsNullOrWhiteSpace(token)) http.DefaultRequestHeaders.Add("X-HID-Token", token);
        string url = $"{serverUrl.TrimEnd('/')}/video/dshow/devices";
        using var resp = await http.GetAsync(url, ct);
        string body = await resp.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(body))
        {
            body = "{\"ok\":false,\"devices\":[]}";
        }
        return Results.Content(body, "application/json", statusCode: (int)resp.StatusCode);
    }
    catch (TaskCanceledException)
    {
        return Results.Json(new { ok = false, devices = Array.Empty<string>(), error = "timeout" });
    }
    catch (Exception ex)
    {
        return Results.Json(new { ok = false, devices = Array.Empty<string>(), error = ex.Message });
    }
});

app.MapGet("/api/video/dshow/modes", async (string? device, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(device))
    {
        return Results.Json(new { ok = false, error = "device_required", modes = Array.Empty<object>() });
    }
    try
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        if (!string.IsNullOrWhiteSpace(token)) http.DefaultRequestHeaders.Add("X-HID-Token", token);
        string url = $"{serverUrl.TrimEnd('/')}/video/dshow/modes?device={Uri.EscapeDataString(device.Trim())}";
        using var resp = await http.GetAsync(url, ct);
        string body = await resp.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(body))
        {
            body = "{\"ok\":false,\"modes\":[]}";
        }
        return Results.Content(body, "application/json", statusCode: (int)resp.StatusCode);
    }
    catch (TaskCanceledException)
    {
        return Results.Json(new { ok = false, modes = Array.Empty<object>(), error = "timeout" });
    }
    catch (Exception ex)
    {
        return Results.Json(new { ok = false, modes = Array.Empty<object>(), error = ex.Message });
    }
});

app.MapGet("/api/video/webrtc/encoders", async (CancellationToken ct) =>
{
    try
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        if (!string.IsNullOrWhiteSpace(token)) http.DefaultRequestHeaders.Add("X-HID-Token", token);
        string url = $"{serverUrl.TrimEnd('/')}/video/webrtc/encoders";
        using var resp = await http.GetAsync(url, ct);
        string body = await resp.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(body))
        {
            body = "{\"ok\":false,\"encoders\":[]}";
        }
        return Results.Content(body, "application/json", statusCode: (int)resp.StatusCode);
    }
    catch (TaskCanceledException)
    {
        return Results.Json(new { ok = false, encoders = Array.Empty<object>(), error = "timeout" });
    }
    catch (Exception ex)
    {
        return Results.Json(new { ok = false, encoders = Array.Empty<object>(), error = ex.Message });
    }
});

app.MapPost("/api/webrtc/video/rooms", async (HttpRequest req, CancellationToken ct) =>
{
    HidControl.Contracts.WebRtcCreateVideoRoomRequest body = new(null, null, null, null, null, null, null, null);
    try
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        if (!string.IsNullOrWhiteSpace(token)) http.DefaultRequestHeaders.Add("X-HID-Token", token);
        var client = new HidControl.ClientSdk.HidControlClient(http, new Uri(serverUrl));
        body = await req.ReadFromJsonAsync<HidControl.Contracts.WebRtcCreateVideoRoomRequest>(cancellationToken: ct) ?? body;
        var res = await HidControl.ClientSdk.WebRtcClientExtensions.CreateWebRtcVideoRoomAsync(
            client,
            body.Room,
            body.QualityPreset,
            body.BitrateKbps,
            body.Fps,
            body.ImageQuality,
            body.CaptureInput,
            body.Encoder,
            body.Codec,
            CancellationToken.None);
        return Results.Json(res ?? new HidControl.Contracts.WebRtcCreateRoomResponse(false, null, false, null, "create_failed"));
    }
    catch (TaskCanceledException)
    {
        try
        {
            // Best-effort recovery: helper startup may complete while the proxy request times out.
            using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            if (!string.IsNullOrWhiteSpace(token)) probe.DefaultRequestHeaders.Add("X-HID-Token", token);
            var client = new HidControl.ClientSdk.HidControlClient(probe, new Uri(serverUrl));
            var list = await HidControl.ClientSdk.WebRtcClientExtensions.ListWebRtcVideoRoomsAsync(client, CancellationToken.None);
            var wanted = body.Room;
            if (!string.IsNullOrWhiteSpace(wanted))
            {
                bool exists = list?.Rooms?.Any(r => string.Equals(r.Room, wanted, StringComparison.OrdinalIgnoreCase)) == true;
                if (exists)
                {
                    return Results.Json(new HidControl.Contracts.WebRtcCreateRoomResponse(true, wanted, false, null, null));
                }
            }
            if (!string.IsNullOrWhiteSpace(wanted))
            {
                string statusUrl = $"{serverUrl.TrimEnd('/')}/status/webrtc/video/peers/{Uri.EscapeDataString(wanted)}";
                using var statusResp = await probe.GetAsync(statusUrl, CancellationToken.None);
                if (statusResp.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(await statusResp.Content.ReadAsStringAsync(CancellationToken.None));
                    bool running = doc.RootElement.TryGetProperty("ok", out var okEl) && okEl.GetBoolean()
                        && doc.RootElement.TryGetProperty("running", out var runEl) && runEl.GetBoolean();
                    if (running)
                    {
                        return Results.Json(new HidControl.Contracts.WebRtcCreateRoomResponse(true, wanted, false, null, null));
                    }
                }
            }
        }
        catch
        {
            // fall through to timeout response
        }
        return Results.Json(new HidControl.Contracts.WebRtcCreateRoomResponse(false, null, false, null, "timeout"));
    }
    catch (Exception ex)
    {
        return Results.Json(new HidControl.Contracts.WebRtcCreateRoomResponse(false, null, false, null, ex.Message));
    }
});

app.MapPost("/api/webrtc/video/rooms/{room}/apply", async (string room, HttpRequest req, CancellationToken ct) =>
{
    var payload = new HidControl.Contracts.WebRtcCreateVideoRoomRequest(room, null, null, null, null, null, null, null);
    try
    {
        payload = await req.ReadFromJsonAsync<HidControl.Contracts.WebRtcCreateVideoRoomRequest>(cancellationToken: ct) ?? payload;
        string targetRoom = string.IsNullOrWhiteSpace(payload.Room) ? room : payload.Room.Trim();

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        if (!string.IsNullOrWhiteSpace(token)) http.DefaultRequestHeaders.Add("X-HID-Token", token);
        var client = new HidControl.ClientSdk.HidControlClient(http, new Uri(serverUrl));

        HidControl.Contracts.WebRtcDeleteRoomResponse? deleted = null;
        try
        {
            deleted = await HidControl.ClientSdk.WebRtcClientExtensions.DeleteWebRtcVideoRoomAsync(client, targetRoom, ct);
        }
        catch
        {
            // Best effort: create call below will still attempt to reconcile state.
        }

        var created = await HidControl.ClientSdk.WebRtcClientExtensions.CreateWebRtcVideoRoomAsync(
            client,
            targetRoom,
            payload.QualityPreset,
            payload.BitrateKbps,
            payload.Fps,
            payload.ImageQuality,
            payload.CaptureInput,
            payload.Encoder,
            payload.Codec,
            ct);

        if (created is null)
        {
            return Results.Json(new { ok = false, room = targetRoom, error = "apply_failed", deleted });
        }

        return Results.Json(new
        {
            ok = created.Ok,
            room = created.Room ?? targetRoom,
            started = created.Started,
            pid = created.Pid,
            error = created.Error,
            deleted
        });
    }
    catch (TaskCanceledException)
    {
        return Results.Json(new { ok = false, room, error = "timeout" });
    }
    catch (Exception ex)
    {
        return Results.Json(new { ok = false, room, error = ex.Message });
    }
});

app.MapDelete("/api/webrtc/video/rooms/{room}", async (string room, CancellationToken ct) =>
{
    try
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        if (!string.IsNullOrWhiteSpace(token)) http.DefaultRequestHeaders.Add("X-HID-Token", token);
        var client = new HidControl.ClientSdk.HidControlClient(http, new Uri(serverUrl));
        var res = await HidControl.ClientSdk.WebRtcClientExtensions.DeleteWebRtcVideoRoomAsync(client, room, ct);
        return Results.Json(res ?? new HidControl.Contracts.WebRtcDeleteRoomResponse(false, room, false, "delete_failed"));
    }
    catch (TaskCanceledException)
    {
        try
        {
            using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            if (!string.IsNullOrWhiteSpace(token)) probe.DefaultRequestHeaders.Add("X-HID-Token", token);
            var client = new HidControl.ClientSdk.HidControlClient(probe, new Uri(serverUrl));
            var list = await HidControl.ClientSdk.WebRtcClientExtensions.ListWebRtcVideoRoomsAsync(client, ct);
            bool stillExists = list?.Rooms?.Any(r => string.Equals(r.Room, room, StringComparison.OrdinalIgnoreCase)) == true;
            if (!stillExists)
            {
                return Results.Json(new HidControl.Contracts.WebRtcDeleteRoomResponse(true, room, true, null));
            }
        }
        catch
        {
            // fall through to timeout response
        }
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

app.MapPost("/api/keyboard/report", async (KeyboardReportApiRequest req, CancellationToken ct) =>
{
    byte mods = req.Mods ?? 0;
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
    string resp = await SendWsOnceAsync(baseUri, token, (ws, c) =>
        ws.SendKeyboardReportAsync(mods, keys.Select(x => (byte)x).ToArray(), applyMapping: req.ApplyMapping ?? true, itfSel: req.ItfSel, id: Guid.NewGuid().ToString("N"), ct: c), ct);
    return Results.Text(resp, "application/json");
});

app.MapPost("/api/keyboard/down", async (KeyboardPressApiRequest req, CancellationToken ct) =>
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
        ws.SendKeyboardDownAsync(usage, mods: mods, itfSel: req.ItfSel, id: Guid.NewGuid().ToString("N"), ct: c), ct);

    return Results.Text(resp, "application/json");
});

app.MapPost("/api/keyboard/up", async (KeyboardPressApiRequest req, CancellationToken ct) =>
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
        ws.SendKeyboardUpAsync(usage, mods: mods, itfSel: req.ItfSel, id: Guid.NewGuid().ToString("N"), ct: c), ct);

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

app.MapPost("/api/mouse/button", async (MouseButtonStateApiRequest req, CancellationToken ct) =>
{
    string button = string.IsNullOrWhiteSpace(req.Button) ? "left" : req.Button!;
    bool down = req.Down ?? true;
    Uri baseUri = new Uri(serverUrl);
    string resp = await SendWsOnceAsync(baseUri, token, (ws, c) =>
        ws.SendMouseButtonAsync(button, down, itfSel: req.ItfSel, id: Guid.NewGuid().ToString("N"), ct: c), ct);
    return Results.Text(resp, "application/json");
});

app.MapPost("/api/mouse/wheel", async (MouseWheelApiRequest req, CancellationToken ct) =>
{
    int delta = req.Delta ?? 0;
    Uri baseUri = new Uri(serverUrl);
    Uri wsUri = ToWsUri(baseUri, "/ws/hid");

    await using var ws = new HidControlWsClient();
    if (!string.IsNullOrWhiteSpace(token))
    {
        ws.SetRequestHeader("X-HID-Token", token!);
    }

    await ws.ConnectAsync(wsUri, ct);
    var payload = new
    {
        type = "mouse.wheel",
        id = Guid.NewGuid().ToString("N"),
        delta,
        itfSel = req.ItfSel
    };
    await ws.SendJsonAsync(payload, null, ct);
    string resp = await ws.ReceiveTextOnceAsync(ct) ?? "{\"ok\":false,\"error\":\"no_response\"}";
    await ws.CloseAsync(ct);
    return Results.Text(resp, "application/json");
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
internal sealed record KeyboardReportApiRequest(byte? Mods, int[]? Keys, byte? ItfSel = null, bool? ApplyMapping = null);
internal sealed record MouseMoveApiRequest(int? Dx, int? Dy, byte? ItfSel = null);
internal sealed record MouseClickApiRequest(string? Button, byte? ItfSel = null);
internal sealed record MouseButtonStateApiRequest(string? Button, bool? Down, byte? ItfSel = null);
internal sealed record MouseWheelApiRequest(int? Delta, byte? ItfSel = null);
