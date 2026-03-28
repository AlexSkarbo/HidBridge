const runtime = {
  video: null,
  dotNetRef: null,
  peer: null,
  stream: null,
  mode: null,
  running: false,
  muted: true,
  playbackUrl: null,
  whepDeleteUrl: null,
  reconnectTimer: null,
  reconnectAttempts: 0,
  reconnectDelayMs: 900,
  maxReconnectDelayMs: 5000,
  maxReconnectAttempts: 20,
  autoReconnect: true,
  stopRequested: false,
  videoHandlers: null,
};

function isWhepUrl(url) {
  return /\/whep(?:[/?]|$)/i.test(url || "");
}

function normalizeUrl(url) {
  if (!url || !url.trim()) {
    return null;
  }
  return url.trim();
}

function emit(type, payload = {}) {
  const ref = runtime.dotNetRef;
  if (!ref) {
    return;
  }

  ref.invokeMethodAsync("OnPlaybackEvent", {
    type,
    mode: runtime.mode,
    isRunning: runtime.running,
    ...payload,
  }).catch(() => {});
}

function resetVideo(videoElement) {
  if (!videoElement) {
    return;
  }
  try {
    videoElement.pause();
  } catch {}
  try {
    videoElement.srcObject = null;
  } catch {}
  try {
    videoElement.removeAttribute("src");
    videoElement.load();
  } catch {}
}

function clearReconnectTimer() {
  if (runtime.reconnectTimer) {
    clearTimeout(runtime.reconnectTimer);
    runtime.reconnectTimer = null;
  }
}

function applyOptions(options) {
  if (!options) {
    return;
  }

  if (typeof options.autoReconnect === "boolean") {
    runtime.autoReconnect = options.autoReconnect;
  }
  if (Number.isFinite(options.reconnectDelayMs)) {
    runtime.reconnectDelayMs = Math.max(250, Math.trunc(options.reconnectDelayMs));
  }
  if (Number.isFinite(options.maxReconnectDelayMs)) {
    runtime.maxReconnectDelayMs = Math.max(runtime.reconnectDelayMs, Math.trunc(options.maxReconnectDelayMs));
  }
  if (Number.isFinite(options.maxReconnectAttempts)) {
    runtime.maxReconnectAttempts = Math.max(1, Math.trunc(options.maxReconnectAttempts));
  }
}

function shouldReconnect() {
  return (
    !runtime.stopRequested
    && !!runtime.playbackUrl
    && runtime.autoReconnect
  );
}

function scheduleReconnect(reason, error) {
  if (!shouldReconnect() || runtime.reconnectTimer) {
    return;
  }
  if (runtime.reconnectAttempts >= runtime.maxReconnectAttempts) {
    emit("reconnect-exhausted", {
      attempt: runtime.reconnectAttempts,
      error: error || `Reconnect attempts exhausted (${runtime.maxReconnectAttempts}).`,
    });
    return;
  }

  runtime.reconnectAttempts += 1;
  const attempt = runtime.reconnectAttempts;
  const delay = Math.min(
    runtime.maxReconnectDelayMs,
    runtime.reconnectDelayMs * Math.pow(2, Math.max(0, attempt - 1)),
  );
  emit("reconnect-attempt", { attempt, reason, nextDelayMs: delay, error });

  runtime.reconnectTimer = window.setTimeout(async () => {
    runtime.reconnectTimer = null;
    if (!shouldReconnect()) {
      return;
    }

    try {
      await startCore();
      emit("reconnect-success", { attempt });
      runtime.reconnectAttempts = 0;
    } catch (retryError) {
      const message = retryError?.message || "Playback reconnect failed.";
      runtime.running = false;
      emit("playback-error", { error: message });
      scheduleReconnect("retry-failed", message);
    }
  }, delay);
}

function attachVideoHandlers(videoElement) {
  detachVideoHandlers();
  if (!videoElement) {
    return;
  }

  const handlers = {
    loadedmetadata: () => emit("loadedmetadata"),
    play: () => emit("play"),
    playing: () => emit("playing"),
    waiting: () => emit("waiting"),
    stalled: () => {
      emit("stalled");
      scheduleReconnect("video-stalled");
    },
    ended: () => {
      emit("ended");
      scheduleReconnect("video-ended");
    },
    error: () => {
      const message = "Media element reported playback error.";
      emit("playback-error", { error: message });
      scheduleReconnect("video-error", message);
    },
  };

  videoElement.addEventListener("loadedmetadata", handlers.loadedmetadata);
  videoElement.addEventListener("play", handlers.play);
  videoElement.addEventListener("playing", handlers.playing);
  videoElement.addEventListener("waiting", handlers.waiting);
  videoElement.addEventListener("stalled", handlers.stalled);
  videoElement.addEventListener("ended", handlers.ended);
  videoElement.addEventListener("error", handlers.error);

  runtime.videoHandlers = handlers;
}

function detachVideoHandlers() {
  if (!runtime.video || !runtime.videoHandlers) {
    runtime.videoHandlers = null;
    return;
  }

  const { loadedmetadata, play, playing, waiting, stalled, ended, error } = runtime.videoHandlers;
  try {
    runtime.video.removeEventListener("loadedmetadata", loadedmetadata);
    runtime.video.removeEventListener("play", play);
    runtime.video.removeEventListener("playing", playing);
    runtime.video.removeEventListener("waiting", waiting);
    runtime.video.removeEventListener("stalled", stalled);
    runtime.video.removeEventListener("ended", ended);
    runtime.video.removeEventListener("error", error);
  } catch {}

  runtime.videoHandlers = null;
}

function waitForIceGatheringComplete(peer, timeoutMs = 2500) {
  return new Promise((resolve) => {
    if (!peer || peer.iceGatheringState === "complete") {
      resolve();
      return;
    }

    let settled = false;
    const done = () => {
      if (settled) {
        return;
      }
      settled = true;
      try {
        peer.removeEventListener("icegatheringstatechange", onStateChange);
      } catch {}
      clearTimeout(timeoutId);
      resolve();
    };

    const onStateChange = () => {
      if (peer.iceGatheringState === "complete") {
        done();
      }
    };

    const timeoutId = window.setTimeout(done, timeoutMs);
    peer.addEventListener("icegatheringstatechange", onStateChange);
  });
}

async function closeWhepSessionIfAny() {
  const deleteUrl = runtime.whepDeleteUrl;
  runtime.whepDeleteUrl = null;
  if (!deleteUrl) {
    return;
  }

  try {
    await fetch(deleteUrl, { method: "DELETE" });
  } catch {}
}

async function teardownActivePlayback(resetVideoElement = true) {
  clearReconnectTimer();

  if (runtime.peer) {
    try {
      runtime.peer.ontrack = null;
      runtime.peer.onconnectionstatechange = null;
      runtime.peer.oniceconnectionstatechange = null;
      runtime.peer.close();
    } catch {}
  }

  if (runtime.stream) {
    try {
      for (const track of runtime.stream.getTracks()) {
        track.stop();
      }
    } catch {}
  }

  runtime.peer = null;
  runtime.stream = null;
  runtime.mode = null;
  runtime.running = false;
  await closeWhepSessionIfAny();

  if (resetVideoElement) {
    resetVideo(runtime.video);
  }
}

async function startDirectPlayback(playbackUrl) {
  if (!runtime.video) {
    throw new Error("Video element is unavailable.");
  }

  resetVideo(runtime.video);
  runtime.video.muted = !!runtime.muted;
  runtime.video.src = playbackUrl;
  await runtime.video.play();

  runtime.mode = "direct-url";
  runtime.running = true;
}

async function startWhepPlayback(playbackUrl) {
  if (!runtime.video) {
    throw new Error("Video element is unavailable.");
  }

  const peer = new RTCPeerConnection();
  const stream = new MediaStream();
  peer.addTransceiver("video", { direction: "recvonly" });
  peer.addTransceiver("audio", { direction: "recvonly" });

  peer.ontrack = (event) => {
    const candidateTracks = event.streams?.[0]?.getTracks?.() || [event.track];
    for (const track of candidateTracks) {
      if (track && !stream.getTrackById(track.id)) {
        stream.addTrack(track);
      }
    }
    runtime.video.srcObject = stream;
    emit("track", {
      trackCount: stream.getTracks().length,
      trackKind: event.track?.kind || null,
    });
  };

  peer.onconnectionstatechange = () => {
    emit("connection-state", { connectionState: peer.connectionState });
    if (peer.connectionState === "failed" || peer.connectionState === "disconnected") {
      runtime.running = false;
      scheduleReconnect("peer-connection-state");
    }
  };

  peer.oniceconnectionstatechange = () => {
    emit("ice-state", { iceConnectionState: peer.iceConnectionState });
    if (peer.iceConnectionState === "failed" || peer.iceConnectionState === "disconnected") {
      runtime.running = false;
      scheduleReconnect("ice-connection-state");
    }
  };

  const offer = await peer.createOffer();
  await peer.setLocalDescription(offer);
  await waitForIceGatheringComplete(peer);

  const response = await fetch(playbackUrl, {
    method: "POST",
    headers: {
      "Content-Type": "application/sdp",
      Accept: "application/sdp",
    },
    body: peer.localDescription?.sdp || offer.sdp || "",
  });

  if (!response.ok) {
    peer.close();
    throw new Error(`WHEP negotiation failed (${response.status}).`);
  }

  const locationHeader = response.headers.get("location") || response.headers.get("Location");
  if (locationHeader) {
    runtime.whepDeleteUrl = new URL(locationHeader, playbackUrl).toString();
  }

  const answerSdp = await response.text();
  await peer.setRemoteDescription({ type: "answer", sdp: answerSdp });

  runtime.video.muted = !!runtime.muted;
  await runtime.video.play().catch(() => {});

  runtime.peer = peer;
  runtime.stream = stream;
  runtime.mode = "whep";
  runtime.running = true;
}

async function startCore() {
  if (!runtime.playbackUrl) {
    throw new Error("Playback URL is not available.");
  }

  await teardownActivePlayback(true);

  if (isWhepUrl(runtime.playbackUrl)) {
    await startWhepPlayback(runtime.playbackUrl);
    return;
  }

  await startDirectPlayback(runtime.playbackUrl);
}

export async function startPlayback(videoElement, playbackUrl, muted, dotNetRef, options) {
  const normalized = normalizeUrl(playbackUrl);
  if (!normalized) {
    return { ok: false, mode: null, error: "Playback URL is not available." };
  }

  runtime.video = videoElement || null;
  runtime.dotNetRef = dotNetRef || null;
  runtime.playbackUrl = normalized;
  runtime.muted = !!muted;
  runtime.stopRequested = false;
  runtime.reconnectAttempts = 0;
  applyOptions(options);
  attachVideoHandlers(runtime.video);

  try {
    await startCore();
    emit("started");
    return {
      ok: true,
      mode: runtime.mode,
      connectionState: runtime.peer?.connectionState || null,
      iceConnectionState: runtime.peer?.iceConnectionState || null,
    };
  } catch (error) {
    const message = error?.message || "Playback start failed.";
    runtime.running = false;
    emit("playback-error", { error: message });
    scheduleReconnect("start-failed", message);
    return {
      ok: false,
      mode: runtime.mode,
      error: message,
      connectionState: runtime.peer?.connectionState || null,
      iceConnectionState: runtime.peer?.iceConnectionState || null,
    };
  }
}

export function setMuted(videoElement, muted) {
  runtime.muted = !!muted;
  const target = videoElement || runtime.video;
  if (!target) {
    return;
  }
  target.muted = runtime.muted;
}

export function setPlaybackOptions(videoElement, options) {
  if (videoElement) {
    runtime.video = videoElement;
  }
  applyOptions(options);
}

export async function stopPlayback(videoElement) {
  runtime.stopRequested = true;
  if (videoElement) {
    runtime.video = videoElement;
  }
  detachVideoHandlers();
  await teardownActivePlayback(true);
  runtime.reconnectAttempts = 0;
  runtime.playbackUrl = null;
  emit("stopped");
}
