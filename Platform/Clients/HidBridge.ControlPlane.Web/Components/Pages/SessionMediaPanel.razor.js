let activePeer = null;
let activeStream = null;
let activeMode = null;

function isWhepUrl(url) {
  return /\/whep(?:[/?]|$)/i.test(url || "");
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

async function startDirectPlayback(videoElement, playbackUrl, muted) {
  resetVideo(videoElement);
  videoElement.muted = !!muted;
  videoElement.src = playbackUrl;
  await videoElement.play();
  activeMode = "direct-url";
  return { ok: true, mode: activeMode };
}

async function startWhepPlayback(videoElement, playbackUrl, muted) {
  const peer = new RTCPeerConnection();
  const stream = new MediaStream();
  peer.addTransceiver("video", { direction: "recvonly" });
  peer.addTransceiver("audio", { direction: "recvonly" });
  peer.ontrack = (event) => {
    for (const track of event.streams?.[0]?.getTracks?.() || [event.track]) {
      if (!stream.getTrackById(track.id)) {
        stream.addTrack(track);
      }
    }
    videoElement.srcObject = stream;
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

  const answerSdp = await response.text();
  await peer.setRemoteDescription({ type: "answer", sdp: answerSdp });

  videoElement.muted = !!muted;
  await videoElement.play().catch(() => {});

  activePeer = peer;
  activeStream = stream;
  activeMode = "whep";
  return { ok: true, mode: activeMode };
}

export async function startPlayback(videoElement, playbackUrl, muted) {
  await stopPlayback(videoElement);

  if (!playbackUrl || !playbackUrl.trim()) {
    return { ok: false, mode: null, error: "Playback URL is not available." };
  }

  const normalized = playbackUrl.trim();
  try {
    if (isWhepUrl(normalized)) {
      return await startWhepPlayback(videoElement, normalized, muted);
    }

    return await startDirectPlayback(videoElement, normalized, muted);
  } catch (error) {
    return {
      ok: false,
      mode: activeMode,
      error: error?.message || "Playback start failed.",
    };
  }
}

export function setMuted(videoElement, muted) {
  if (!videoElement) {
    return;
  }

  videoElement.muted = !!muted;
}

export async function stopPlayback(videoElement) {
  if (activePeer) {
    try {
      activePeer.ontrack = null;
      activePeer.close();
    } catch {}
  }

  if (activeStream) {
    try {
      for (const track of activeStream.getTracks()) {
        track.stop();
      }
    } catch {}
  }

  activePeer = null;
  activeStream = null;
  activeMode = null;
  resetVideo(videoElement);
}
