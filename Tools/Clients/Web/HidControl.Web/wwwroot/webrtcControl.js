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
    // Prefer globalThis since some hosts may not expose WebRTC APIs on `window` consistently.
    const g = (typeof globalThis !== "undefined") ? globalThis : window;
    // eslint-disable-next-line no-undef
    const direct = (typeof RTCPeerConnection !== "undefined") ? RTCPeerConnection : null;
    return direct || g.RTCPeerConnection || g.webkitRTCPeerConnection || g.mozRTCPeerConnection || window.RTCPeerConnection || window.webkitRTCPeerConnection || window.mozRTCPeerConnection;
  }

  function createClient(opts) {
    opts = opts || {};
    const room = (opts.room || "control").trim();
    const signalingUrl = opts.signalingUrl || getDefaultSignalingUrl();
    const iceServers = normalizeIceServers(opts.iceServers);
    const iceTransportPolicy = (opts.iceTransportPolicy === "relay") ? "relay" : "all";
    const joinTimeoutMs = (typeof opts.joinTimeoutMs === "number" && Number.isFinite(opts.joinTimeoutMs)) ? opts.joinTimeoutMs : 2000;
    const onLog = typeof opts.onLog === "function" ? opts.onLog : defaultLogger;
    const onStatus = typeof opts.onStatus === "function" ? opts.onStatus : defaultLogger;
    const onMessage = typeof opts.onMessage === "function" ? opts.onMessage : defaultLogger;

    let ws = null;
    let pc = null;
    let dc = null;
    let pendingCandidates = [];
    let remotePeerId = null;
    let localCandidateCount = 0;
    let lastIceGatheringState = null;
    let lastConnectionState = null;
    let lastJoinedPeers = null;
    let joined = false;
    let joinWaiter = null; // { resolve, reject, timerId }
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

        if (msg.type === "webrtc.error") {
          log(msg.type, msg);
          if (joinWaiter) {
            joinWaiter.reject(new Error(msg.error || "webrtc_error"));
            clearTimeout(joinWaiter.timerId);
            joinWaiter = null;
          }
          setStatus("error: " + (msg.error || "unknown"));
          return;
        }

        if (msg.type === "webrtc.hello" || msg.type === "webrtc.joined" || msg.type === "webrtc.peer_joined") {
          log(msg.type, msg);
          if (msg.type === "webrtc.joined" && msg.room === room) {
            joined = true;
            if (typeof msg.peers === "number") lastJoinedPeers = msg.peers;
            if (joinWaiter) {
              joinWaiter.resolve(true);
              clearTimeout(joinWaiter.timerId);
              joinWaiter = null;
            }
          }
          return;
        }

        if (msg.type === "webrtc.signal" && msg.data) {
          log("recv", msg);
          handleSignal(msg.from, msg.data).catch((e) => log("handleSignal_error", String(e)));
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
          rtcpTypeGlobal: (typeof globalThis !== "undefined") ? typeof globalThis.RTCPeerConnection : null,
          isSecureContext: (typeof isSecureContext !== "undefined") ? isSecureContext : null,
          proto: location.protocol,
          ua: navigator.userAgent
        };
        log("error", detail);
        throw new Error("webrtc_not_supported");
      }

      pc = new Ctor({ iceServers, iceTransportPolicy });
      pc.onconnectionstatechange = () => {
        lastConnectionState = pc.connectionState;
        log("pc.connectionState", pc.connectionState);
      };
      pc.oniceconnectionstatechange = () => log("pc.iceConnectionState", pc.iceConnectionState);
      pc.onsignalingstatechange = () => log("pc.signalingState", pc.signalingState);
      pc.onicegatheringstatechange = () => {
        lastIceGatheringState = pc.iceGatheringState;
        log("pc.iceGatheringState", pc.iceGatheringState);
        if (pc.iceGatheringState === "complete" && localCandidateCount === 0) {
          const payload = {
            hint: "Browser produced 0 ICE candidates. Check WebRTC/privacy/enterprise policies (e.g. WebRTC IP handling policy / disable UDP), or try another browser.",
            iceServers,
            iceTransportPolicy
          };
          log("warn.no_local_candidates", payload);
          // Surface this as a terminal status for UIs to fail fast instead of waiting for a timeout.
          setStatus("no_local_candidates");
        }
      };
      pc.onicecandidate = (e) => {
        if (!e.candidate) return;
        const cand = (typeof e.candidate.toJSON === "function") ? e.candidate.toJSON() : e.candidate;
        // Empty-string candidate = end-of-candidates (common in Firefox). Don't forward it as a real candidate.
        if (cand && cand.candidate === "") {
          log("candidate.eoc_local", cand);
          return;
        }
        localCandidateCount++;
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

    async function handleSignal(from, data) {
      await ensurePc();

      // If we already "paired" with a remote peer, ignore all other senders in the same room.
      if (remotePeerId && from && from !== remotePeerId) {
        log("signal_ignored_other_peer", { from, expected: remotePeerId, kind: data.kind });
        return;
      }

      if (data.kind === "offer") {
        // If we're the caller, ignore offers (another peer may be calling in the same room).
        if (pc.signalingState !== "stable") {
          log("offer_ignored_wrong_state", { state: pc.signalingState, from });
          return;
        }
        if (!remotePeerId && from) remotePeerId = from;
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
        // Accept answer only if we're actually waiting for it.
        if (pc.signalingState !== "have-local-offer") {
          log("answer_ignored_wrong_state", { state: pc.signalingState, from });
          return;
        }
        if (!remotePeerId && from) remotePeerId = from;
        await pc.setRemoteDescription(data.sdp);
        for (const c of pendingCandidates) {
          try { await pc.addIceCandidate(c); } catch { }
        }
        pendingCandidates = [];
        return;
      }

      if (data.kind === "candidate" && data.candidate) {
        if (!remotePeerId && from) remotePeerId = from;
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
      if (joined) return;
      if (joinWaiter) {
        await joinWaiter.promise;
        return;
      }
      joinWaiter = {};
      joinWaiter.promise = new Promise((resolve, reject) => {
        joinWaiter.resolve = resolve;
        joinWaiter.reject = reject;
      });
      // Fail fast if server doesn't respond (or WS closes) so UI doesn't sit in "calling..." forever.
      joinWaiter.timerId = setTimeout(() => {
        if (!joinWaiter) return;
        joinWaiter.reject(new Error("join_timeout"));
        joinWaiter = null;
      }, Math.max(250, joinTimeoutMs | 0));
      await wsSend({ type: "join", room });
      setStatus("joined room: " + room);
      await joinWaiter.promise;
    }

    async function call() {
      const p = await ensurePc();
      await join();

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

    function getDebug() {
      return {
        room,
        signalingUrl,
        iceTransportPolicy,
        localCandidateCount,
        lastIceGatheringState,
        lastConnectionState,
        lastJoinedPeers,
        dcState: dc ? dc.readyState : null
      };
    }

    function hangup() {
      try { if (ws && ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify({ type: "leave" })); } catch { }
      try { if (dc) dc.close(); } catch { }
      try { if (pc) pc.close(); } catch { }
      try { if (ws) ws.close(); } catch { }
      ws = null; pc = null; dc = null;
      pendingCandidates = [];
      remotePeerId = null;
      localCandidateCount = 0;
      lastIceGatheringState = null;
      lastConnectionState = null;
      lastJoinedPeers = null;
      joined = false;
      if (joinWaiter) {
        try { joinWaiter.reject(new Error("disconnected")); } catch { }
        clearTimeout(joinWaiter.timerId);
        joinWaiter = null;
      }
      setStatus("disconnected");
    }

    return { join, call, send, hangup, getDebug };
  }

  window.hidbridge.webrtcControl = {
    createClient
  };
})();
