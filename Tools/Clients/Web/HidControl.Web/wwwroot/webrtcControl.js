(function () {
  "use strict";

  window.hidbridge = window.hidbridge || {};

  function defaultLogger() { }

  function safeJsonParse(s) {
    try { return JSON.parse(s); } catch { return null; }
  }

  function getDefaultSignalingUrl() {
    const proto = location.protocol === "https:" ? "wss://" : "ws://";
    return proto + location.host + "/ws/webrtc";
  }

  function normalizeIceServers(iceServers) {
    if (Array.isArray(iceServers) && iceServers.length > 0) return iceServers;
    return [{ urls: "stun:stun.l.google.com:19302" }];
  }

  function getPeerConnectionCtor() {
    return window.RTCPeerConnection || window.webkitRTCPeerConnection || window.mozRTCPeerConnection;
  }

  function createClient(opts) {
    opts = opts || {};
    const room = (opts.room || "control").trim();
    const signalingUrl = opts.signalingUrl || getDefaultSignalingUrl();
    const iceServers = normalizeIceServers(opts.iceServers);
    const onLog = typeof opts.onLog === "function" ? opts.onLog : defaultLogger;
    const onStatus = typeof opts.onStatus === "function" ? opts.onStatus : defaultLogger;
    const onMessage = typeof opts.onMessage === "function" ? opts.onMessage : defaultLogger;

    let ws = null;
    let pc = null;
    let dc = null;
    let pendingCandidates = [];
    let seq = 0;

    function log(kind, payload) {
      seq++;
      onLog({ webrtc: kind, seq, payload });
    }

    function setStatus(s) {
      onStatus(s);
      log("status", s);
    }

    async function ensureWsOpen() {
      if (ws && (ws.readyState === WebSocket.OPEN || ws.readyState === WebSocket.CONNECTING)) return;

      ws = new WebSocket(signalingUrl);
      ws.onopen = () => setStatus("signaling: open");
      ws.onclose = () => setStatus("signaling: closed");
      ws.onerror = () => setStatus("signaling: error");
      ws.onmessage = (ev) => {
        const msg = safeJsonParse(ev.data);
        if (!msg || !msg.type) return;

        if (msg.type === "webrtc.hello" || msg.type === "webrtc.joined" || msg.type === "webrtc.peer_joined") {
          log(msg.type, msg);
          return;
        }

        if (msg.type === "webrtc.signal" && msg.data) {
          log("recv", msg);
          handleSignal(msg.data).catch((e) => log("handleSignal_error", String(e)));
        }
      };

      if (ws.readyState === WebSocket.OPEN) return;
      if (ws.readyState !== WebSocket.CONNECTING) throw new Error("signaling_not_open");

      await new Promise((resolve, reject) => {
        const onOpen = () => { cleanup(); resolve(); };
        const onErr = () => { cleanup(); reject(new Error("signaling_error")); };
        const onClose = () => { cleanup(); reject(new Error("signaling_closed")); };
        function cleanup() {
          ws.removeEventListener("open", onOpen);
          ws.removeEventListener("error", onErr);
          ws.removeEventListener("close", onClose);
        }
        ws.addEventListener("open", onOpen);
        ws.addEventListener("error", onErr);
        ws.addEventListener("close", onClose);
      });
    }

    async function wsSend(obj) {
      await ensureWsOpen();
      log("send", obj);
      ws.send(JSON.stringify(obj));
    }

    async function ensurePc() {
      if (pc) return pc;

      const Ctor = getPeerConnectionCtor();
      if (typeof Ctor !== "function") {
        const detail = {
          ok: false,
          error: "webrtc_not_supported",
          rtcpType: typeof window.RTCPeerConnection,
          proto: location.protocol,
          ua: navigator.userAgent
        };
        log("error", detail);
        throw new Error("webrtc_not_supported");
      }

      pc = new Ctor({ iceServers });
      pc.onconnectionstatechange = () => log("pc.connectionState", pc.connectionState);
      pc.oniceconnectionstatechange = () => log("pc.iceConnectionState", pc.iceConnectionState);
      pc.onsignalingstatechange = () => log("pc.signalingState", pc.signalingState);
      pc.onicegatheringstatechange = () => log("pc.iceGatheringState", pc.iceGatheringState);
      pc.onicecandidate = (e) => {
        if (!e.candidate) return;
        const cand = (typeof e.candidate.toJSON === "function") ? e.candidate.toJSON() : e.candidate;
        // Empty-string candidate = end-of-candidates (common in Firefox). Don't forward it as a real candidate.
        if (cand && cand.candidate === "") {
          log("candidate.eoc_local", cand);
          return;
        }
        wsSend({ type: "signal", room, data: { kind: "candidate", candidate: cand } }).catch(() => { });
      };
      pc.ondatachannel = (e) => {
        dc = e.channel;
        wireDc();
      };
      return pc;
    }

    function wireDc() {
      if (!dc) return;
      dc.onopen = () => setStatus("datachannel: open");
      dc.onclose = () => setStatus("datachannel: closed");
      dc.onerror = () => setStatus("datachannel: error");
      dc.onmessage = (e) => onMessage(e.data);
    }

    async function handleSignal(data) {
      await ensurePc();

      if (data.kind === "offer") {
        await pc.setRemoteDescription(data.sdp);
        for (const c of pendingCandidates) {
          try { await pc.addIceCandidate(c); } catch { }
        }
        pendingCandidates = [];
        const answer = await pc.createAnswer();
        await pc.setLocalDescription(answer);
        await wsSend({ type: "signal", room, data: { kind: "answer", sdp: { type: pc.localDescription.type, sdp: pc.localDescription.sdp } } });
        return;
      }

      if (data.kind === "answer") {
        await pc.setRemoteDescription(data.sdp);
        for (const c of pendingCandidates) {
          try { await pc.addIceCandidate(c); } catch { }
        }
        pendingCandidates = [];
        return;
      }

      if (data.kind === "candidate" && data.candidate) {
        // Firefox uses empty-string candidates to signal end-of-candidates. Ignore those.
        if (data.candidate.candidate === "") {
          log("candidate.eoc_recv", data.candidate);
          return;
        }
        if (!pc.remoteDescription || !pc.remoteDescription.type) {
          pendingCandidates.push(data.candidate);
        } else {
          try { await pc.addIceCandidate(data.candidate); } catch { }
        }
      }
    }

    async function join() {
      await ensurePc();
      await wsSend({ type: "join", room });
      setStatus("joined room: " + room);
    }

    async function call() {
      const p = await ensurePc();
      await wsSend({ type: "join", room });

      dc = p.createDataChannel("data");
      wireDc();

      const offer = await p.createOffer();
      await p.setLocalDescription(offer);
      await wsSend({ type: "signal", room, data: { kind: "offer", sdp: { type: p.localDescription.type, sdp: p.localDescription.sdp } } });
      setStatus("calling...");
    }

    function send(text) {
      if (!dc || dc.readyState !== "open") {
        throw new Error("datachannel_not_open");
      }
      dc.send(text);
    }

    function hangup() {
      try { if (ws && ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify({ type: "leave" })); } catch { }
      try { if (dc) dc.close(); } catch { }
      try { if (pc) pc.close(); } catch { }
      try { if (ws) ws.close(); } catch { }
      ws = null; pc = null; dc = null;
      pendingCandidates = [];
      setStatus("disconnected");
    }

    return { join, call, send, hangup };
  }

  window.hidbridge.webrtcControl = {
    createClient
  };
})();

