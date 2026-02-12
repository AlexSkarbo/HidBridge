package main

import (
	"context"
	"encoding/json"
	"flag"
	"fmt"
	"log"
	"net"
	"net/http"
	"net/url"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"strconv"
	"strings"
	"sync"
	"time"
	"unicode"

	"github.com/gorilla/websocket"
	"github.com/pion/rtp"
	"github.com/pion/webrtc/v3"
)

// This tool runs next to HidControlServer and acts as a WebRTC "video peer":
// - joins a signaling room via /ws/webrtc
// - accepts offers from browser clients
// - publishes a VP8 video track from ffmpeg (test source by default, capture mode optional)
// - opens/accepts DataChannels:
//   - "control" for input/control messages
//   - "telemetry" for video status/health messages
//   - legacy "data" remains supported for backward compatibility
//
// Later, this helper will publish real media tracks (video).

type signalEnvelope struct {
	Ok   bool            `json:"ok,omitempty"`
	Type string          `json:"type"`
	Room string          `json:"room,omitempty"`
	From string          `json:"from,omitempty"`
	Data json.RawMessage `json:"data,omitempty"`
}

type signalMessage struct {
	Type string          `json:"type"`
	Room string          `json:"room,omitempty"`
	Data json.RawMessage `json:"data,omitempty"`
}

type sdpPayload struct {
	Kind string                    `json:"kind"`
	SDP  webrtc.SessionDescription `json:"sdp"`
}

type candidatePayload struct {
	Kind      string                  `json:"kind"`
	Candidate webrtc.ICECandidateInit `json:"candidate"`
}

func main() {
	log.SetOutput(os.Stdout)

	var (
		serverURL    = flag.String("server", envOr("HIDBRIDGE_SERVER_URL", "http://127.0.0.1:8080"), "HidControlServer base URL (http://host:port)")
		token        = flag.String("token", envOr("HIDBRIDGE_TOKEN", ""), "Server token (X-HID-Token)")
		room         = flag.String("room", envOr("HIDBRIDGE_WEBRTC_ROOM", "video"), "Signaling room name")
		stun         = flag.String("stun", envOr("HIDBRIDGE_STUN", "stun:stun.l.google.com:19302"), "STUN server URL (stun:host:port)")
		sourceMode   = flag.String("source-mode", envOr("HIDBRIDGE_VIDEO_SOURCE_MODE", "testsrc"), "Video source mode: testsrc|capture")
		qualityPreset = flag.String("quality-preset", envOr("HIDBRIDGE_VIDEO_QUALITY_PRESET", "balanced"), "Video quality preset: low|low-latency|balanced|high|optimal")
		imageQuality = flag.Int("image-quality", envOptionalIntInRange("HIDBRIDGE_VIDEO_IMAGE_QUALITY", 1, 100), "Image quality level (1-100, higher is better); 0=auto")
		encoderMode  = flag.String("encoder", envOr("HIDBRIDGE_VIDEO_ENCODER", "auto"), "Encoder mode: auto|cpu|hw|nvenc|amf|qsv|v4l2m2m|vaapi")
		codecMode    = flag.String("codec", envOr("HIDBRIDGE_VIDEO_CODEC", "auto"), "Codec mode: auto|vp8|h264")
		bitrateKbps  = flag.Int("bitrate-kbps", envIntInRange("HIDBRIDGE_VIDEO_BITRATE_KBPS", 1200, 200, 12000), "Target bitrate in kbps")
		fps          = flag.Int("fps", envIntInRange("HIDBRIDGE_VIDEO_FPS", 30, 5, 60), "Target frame rate")
		ffmpegArgs   = flag.String("ffmpeg-args", envOr("HIDBRIDGE_VIDEO_FFMPEG_ARGS", ""), "Optional FFmpeg pipeline args (overrides built-in mode pipeline)")
		captureInput = flag.String("capture-input", envOr("HIDBRIDGE_VIDEO_CAPTURE_INPUT", ""), "Optional capture input args (used in capture mode)")
	)
	flag.Parse()

	base, err := url.Parse(*serverURL)
	if err != nil {
		log.Fatalf("bad --server: %v", err)
	}

	mode := normalizeSourceMode(*sourceMode)
	preset := normalizeQualityPreset(*qualityPreset)
	encoder := normalizeEncoderMode(*encoderMode)
	codec := normalizeCodecMode(*codecMode)
	log.Printf("webrtc video peer starting")
	imageQualityText := "auto"
	if *imageQuality >= 1 && *imageQuality <= 100 {
		imageQualityText = strconv.Itoa(*imageQuality)
	}
	log.Printf("server=%s room=%s stun=%s sourceMode=%s qualityPreset=%s imageQuality=%s encoder=%s codec=%s bitrateKbps=%d fps=%d", base.String(), *room, *stun, mode, preset, imageQualityText, encoder, codec, *bitrateKbps, *fps)
	cleanupPidFile := registerHelperPidFile()
	defer cleanupPidFile()

	// Long-running loop: reconnect on transport failures instead of exiting.
	backoff := 1 * time.Second
	for {
		err := runSession(base, *token, *room, *stun, mode, preset, normalizeImageQuality(*imageQuality), encoder, codec, *bitrateKbps, *fps, *ffmpegArgs, *captureInput)
		if err != nil {
			log.Printf("session ended: %v", err)
		} else {
			log.Printf("session ended")
		}
		time.Sleep(backoff)
		if backoff < 10*time.Second {
			backoff *= 2
		}
	}
}

func runSession(base *url.URL, token string, room string, stun string, sourceMode string, qualityPreset string, imageQuality int, encoderMode string, codecMode string, bitrateKbps int, fps int, ffmpegArgs string, captureInput string) error {
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()

	sigWS, err := dialWS(ctx, base, "/ws/webrtc", token)
	if err != nil {
		return fmt.Errorf("dial /ws/webrtc: %w", err)
	}
	defer sigWS.Close()

	if err := sendJSON(sigWS, signalMessage{Type: "join", Room: room}); err != nil {
		return fmt.Errorf("join room: %w", err)
	}

	peer := newPeerState(stun, room, sourceMode, qualityPreset, imageQuality, encoderMode, codecMode, bitrateKbps, fps, ffmpegArgs, captureInput, sigWS)
	for {
		_, b, err := sigWS.ReadMessage()
		if err != nil {
			return fmt.Errorf("signaling read: %w", err)
		}

		var env signalEnvelope
		if err := json.Unmarshal(b, &env); err != nil {
			continue
		}

		if env.Type == "webrtc.error" {
			return fmt.Errorf("signaling error: %s", strings.TrimSpace(string(env.Data)))
		}
		if env.Type == "webrtc.hello" || env.Type == "webrtc.joined" || env.Type == "webrtc.peer_joined" {
			continue
		}
		if env.Type != "webrtc.signal" {
			continue
		}

		peer.handleSignal(ctx, env.From, env.Data)
	}
}

type peerState struct {
	room  string
	sigWS *websocket.Conn
	sigMu sync.Mutex

	stun         string
	sourceMode   string
	qualityPreset string
	imageQuality int
	encoderMode  string
	codecMode    string
	bitrateKbps  int
	fps          int
	ffmpegArgs   string
	captureInput string

	pcMu         sync.Mutex
	pc           *webrtc.PeerConnection
	streamCancel context.CancelFunc
	disconnectTimer *time.Timer

	activePeerId string
	dcMu          sync.Mutex
	dcControl     *webrtc.DataChannel
	dcTelemetry   *webrtc.DataChannel
	pendingNotice string
}

func newPeerState(stun, room, sourceMode, qualityPreset string, imageQuality int, encoderMode, codecMode string, bitrateKbps int, fps int, ffmpegArgs, captureInput string, sigWS *websocket.Conn) *peerState {
	return &peerState{
		room:         room,
		sigWS:        sigWS,
		stun:         stun,
		sourceMode:   normalizeSourceMode(sourceMode),
		qualityPreset: normalizeQualityPreset(qualityPreset),
		imageQuality: normalizeImageQuality(imageQuality),
		encoderMode:  normalizeEncoderMode(encoderMode),
		codecMode:    normalizeCodecMode(codecMode),
		bitrateKbps:  clamp(bitrateKbps, 200, 12000),
		fps:          clamp(fps, 5, 60),
		ffmpegArgs:   ffmpegArgs,
		captureInput: captureInput,
		pc:           nil,
		activePeerId: "",
	}
}

func (p *peerState) ensurePC(stun string) (*webrtc.PeerConnection, error) {
	p.pcMu.Lock()
	defer p.pcMu.Unlock()

	if p.pc != nil {
		return p.pc, nil
	}

	cfg := webrtc.Configuration{
		ICEServers: []webrtc.ICEServer{{URLs: []string{stun}}},
	}
	pc, err := webrtc.NewPeerConnection(cfg)
	if err != nil {
		return nil, err
	}

	videoTrack, err := webrtc.NewTrackLocalStaticRTP(
		webrtc.RTPCodecCapability{MimeType: selectedVideoMimeType(p.codecMode, p.encoderMode), ClockRate: 90000},
		"video",
		"hidbridge",
	)
	if err != nil {
		_ = pc.Close()
		return nil, err
	}
	sender, err := pc.AddTrack(videoTrack)
	if err != nil {
		_ = pc.Close()
		return nil, err
	}
	go func() {
		// Keep draining RTCP so sender stays healthy.
		rtcpBuf := make([]byte, 1500)
		for {
			if _, _, readErr := sender.Read(rtcpBuf); readErr != nil {
				return
			}
		}
	}()

	streamCtx, streamCancel := context.WithCancel(context.Background())
	p.streamCancel = streamCancel
	go func() {
		if err := runVideoStream(streamCtx, videoTrack, p.sourceMode, p.qualityPreset, p.imageQuality, p.encoderMode, p.codecMode, p.bitrateKbps, p.fps, p.ffmpegArgs, p.captureInput, p.notifyVideoStatus); err != nil && streamCtx.Err() == nil {
			log.Printf("video stream ended: %v", err)
		}
	}()

	pc.OnICECandidate(func(c *webrtc.ICECandidate) {
		if c == nil {
			return
		}
		init := c.ToJSON()
		if strings.TrimSpace(init.Candidate) == "" {
			return
		}
		payload, _ := json.Marshal(candidatePayload{Kind: "candidate", Candidate: init})
		_ = p.sendSignal(signalMessage{Type: "signal", Room: p.room, Data: payload})
	})

	pc.OnConnectionStateChange(func(s webrtc.PeerConnectionState) {
		log.Printf("pc state: %s", s.String())
		switch s {
		case webrtc.PeerConnectionStateConnected:
			p.cancelDisconnectTeardown()
		case webrtc.PeerConnectionStateDisconnected:
			// Browsers can transiently enter disconnected and recover.
			// Keep PC/stream alive for a grace period to avoid unnecessary full restarts.
			p.scheduleDisconnectTeardown(25 * time.Second)
		case webrtc.PeerConnectionStateFailed, webrtc.PeerConnectionStateClosed:
			p.teardownPeerConnection()
		}
	})

	pc.OnDataChannel(func(dc *webrtc.DataChannel) {
		label := strings.ToLower(strings.TrimSpace(dc.Label()))
		log.Printf("datachannel: %s", label)
		p.dcMu.Lock()
		switch label {
		case "telemetry":
			p.dcTelemetry = dc
		case "control":
			p.dcControl = dc
		default:
			// Legacy single-channel mode.
			p.dcControl = dc
			p.dcTelemetry = dc
		}
		p.dcMu.Unlock()

		dc.OnOpen(func() {
			log.Printf("datachannel open: %s", label)
			p.dcMu.Lock()
			notice := p.pendingNotice
			outTelemetry := p.dcTelemetry
			p.dcMu.Unlock()
			if strings.TrimSpace(notice) != "" && outTelemetry == dc {
				_ = outTelemetry.SendText(notice)
			}
		})
		dc.OnClose(func() {
			log.Printf("datachannel close: %s", label)
			p.dcMu.Lock()
			if p.dcControl == dc {
				p.dcControl = nil
			}
			if p.dcTelemetry == dc {
				p.dcTelemetry = nil
			}
			p.dcMu.Unlock()
		})
		dc.OnMessage(func(m webrtc.DataChannelMessage) {
			if !m.IsString {
				return
			}
			// Keep legacy "data" echo behavior for backward compatibility only.
			// Dedicated "control" channel is intentionally non-echo to avoid
			// unnecessary round-trips under high input rate.
			if label == "data" {
				_ = dc.SendText(string(m.Data))
			}
		})
	})

	p.pc = pc
	return pc, nil
}

func (p *peerState) scheduleDisconnectTeardown(delay time.Duration) {
	p.pcMu.Lock()
	defer p.pcMu.Unlock()

	if p.disconnectTimer != nil {
		p.disconnectTimer.Stop()
	}
	p.disconnectTimer = time.AfterFunc(delay, func() {
		p.pcMu.Lock()
		defer p.pcMu.Unlock()

		if p.pc == nil {
			return
		}
		if p.pc.ConnectionState() != webrtc.PeerConnectionStateDisconnected {
			return
		}
		log.Printf("pc disconnected timeout: forcing teardown")
		p.teardownPeerConnectionLocked()
	})
}

func (p *peerState) cancelDisconnectTeardown() {
	p.pcMu.Lock()
	defer p.pcMu.Unlock()
	if p.disconnectTimer != nil {
		p.disconnectTimer.Stop()
		p.disconnectTimer = nil
	}
}

func (p *peerState) teardownPeerConnection() {
	p.pcMu.Lock()
	defer p.pcMu.Unlock()
	p.teardownPeerConnectionLocked()
}

func (p *peerState) teardownPeerConnectionLocked() {
	if p.disconnectTimer != nil {
		p.disconnectTimer.Stop()
		p.disconnectTimer = nil
	}
	if p.streamCancel != nil {
		p.streamCancel()
		p.streamCancel = nil
	}
	if p.pc != nil {
		_ = p.pc.Close()
	}
	p.pc = nil
	p.activePeerId = ""
}

func (p *peerState) notifyVideoStatus(event string, mode string, detail string, extra map[string]any) {
	payload := map[string]any{
		"type":          "video.status",
		"event":         event,
		"mode":          mode,
		"encoder":       p.encoderMode,
		"codec":         p.codecMode,
		"qualityPreset": p.qualityPreset,
		"bitrateKbps":   p.bitrateKbps,
		"targetFps":     p.fps,
	}
	if strings.TrimSpace(detail) != "" {
		payload["detail"] = detail
	}
	for k, v := range extra {
		payload[k] = v
	}
	raw, _ := json.Marshal(payload)
	msg := string(raw)

	p.dcMu.Lock()
	p.pendingNotice = msg
	dc := p.dcTelemetry
	if dc == nil {
		// Backward compatibility: older clients may still use single "data"/control channel.
		dc = p.dcControl
	}
	p.dcMu.Unlock()

	if dc != nil {
		_ = dc.SendText(msg)
	}
}

func (p *peerState) handleSignal(ctx context.Context, from string, data json.RawMessage) {
	var kind struct {
		Kind string `json:"kind"`
	}
	if err := json.Unmarshal(data, &kind); err != nil {
		return
	}

	switch kind.Kind {
	case "offer":
		if !p.tryAdoptPeer(from) {
			log.Printf("ignoring offer from %q (active=%q)", from, p.getActivePeerId())
			return
		}
		var offer sdpPayload
		if err := json.Unmarshal(data, &offer); err != nil {
			return
		}
		p.onOffer(ctx, offer.SDP)
	case "candidate":
		if !p.isActivePeer(from) {
			return
		}
		var cand candidatePayload
		if err := json.Unmarshal(data, &cand); err != nil {
			return
		}
		if strings.TrimSpace(cand.Candidate.Candidate) == "" {
			return
		}
		p.onCandidate(ctx, cand.Candidate)
	default:
		// ignore
	}
}

func (p *peerState) getActivePeerId() string {
	p.pcMu.Lock()
	defer p.pcMu.Unlock()
	return p.activePeerId
}

func (p *peerState) isActivePeer(from string) bool {
	p.pcMu.Lock()
	defer p.pcMu.Unlock()
	return p.activePeerId != "" && p.activePeerId == from
}

func (p *peerState) tryAdoptPeer(from string) bool {
	p.pcMu.Lock()
	defer p.pcMu.Unlock()
	if p.activePeerId == "" {
		p.activePeerId = from
		return true
	}
	return p.activePeerId == from
}

func (p *peerState) onOffer(ctx context.Context, offer webrtc.SessionDescription) {
	pc, err := p.ensurePC(p.stun)
	if err != nil {
		log.Printf("ensurePC: %v", err)
		return
	}

	if err := pc.SetRemoteDescription(offer); err != nil {
		log.Printf("SetRemoteDescription: %v", err)
		return
	}

	answer, err := pc.CreateAnswer(nil)
	if err != nil {
		log.Printf("CreateAnswer: %v", err)
		return
	}

	if err := pc.SetLocalDescription(answer); err != nil {
		log.Printf("SetLocalDescription: %v", err)
		return
	}

	payload, _ := json.Marshal(sdpPayload{Kind: "answer", SDP: answer})
	_ = p.sendSignal(signalMessage{Type: "signal", Room: p.room, Data: payload})

	// Give ICE some time to start before returning; we rely on trickle anyway.
	_ = ctx
}

func (p *peerState) onCandidate(ctx context.Context, cand webrtc.ICECandidateInit) {
	pc, err := p.ensurePC(p.stun)
	if err != nil {
		return
	}
	if err := pc.AddICECandidate(cand); err != nil {
		// Some candidates can race; ignore.
		return
	}
	_ = ctx
}

func runVideoStream(ctx context.Context, track *webrtc.TrackLocalStaticRTP, sourceMode string, qualityPreset string, imageQuality int, encoderMode string, codecMode string, bitrateKbps int, fps int, rawFFmpegArgs string, rawCaptureInput string, onStatus func(event string, mode string, detail string, extra map[string]any)) error {
	conn, err := net.ListenUDP("udp4", &net.UDPAddr{IP: net.ParseIP("127.0.0.1"), Port: 0})
	if err != nil {
		return fmt.Errorf("listen udp: %w", err)
	}
	defer conn.Close()

	ffmpegPath := envOr("HIDBRIDGE_FFMPEG", "ffmpeg")
	port := conn.LocalAddr().(*net.UDPAddr).Port
	outURL := fmt.Sprintf("rtp://127.0.0.1:%d?pkt_size=1200", port)
	modeInUse := normalizeSourceMode(sourceMode)
	customPipeline := strings.TrimSpace(rawFFmpegArgs) != ""
	canFallback := modeInUse == "capture" && !customPipeline
	fallbackUsed := false
	maxRestarts := envIntInRange("HIDBRIDGE_VIDEO_PIPELINE_MAX_RESTARTS", 8, 0, 100)
	restartWindowSec := envIntInRange("HIDBRIDGE_VIDEO_PIPELINE_RESTART_WINDOW_SEC", 60, 5, 600)
	restartBaseDelayMs := envIntInRange("HIDBRIDGE_VIDEO_PIPELINE_RESTART_DELAY_MS", 500, 50, 10000)
	startupPacketTimeoutMs := envIntInRange("HIDBRIDGE_VIDEO_STARTUP_PACKET_TIMEOUT_MS", 15000, 2000, 120000)
	// ABR currently requires pipeline restart for bitrate changes, which can cause
	// visible freezes on live control sessions. Keep it opt-in by default.
	abrEnabled := envBool("HIDBRIDGE_VIDEO_ABR_ENABLED", false)
	abrIntervalSec := envIntInRange("HIDBRIDGE_VIDEO_ABR_INTERVAL_SEC", 6, 2, 30)
	abrMinKbps := envIntInRange("HIDBRIDGE_VIDEO_ABR_MIN_KBPS", 400, 200, 12000)
	abrMaxKbps := envIntInRange("HIDBRIDGE_VIDEO_ABR_MAX_KBPS", 6000, 200, 20000)
	abrConsecutiveRequired := envIntInRange("HIDBRIDGE_VIDEO_ABR_CONSECUTIVE_REQUIRED", 3, 1, 8)
	abrMinChangeIntervalSec := envIntInRange("HIDBRIDGE_VIDEO_ABR_MIN_CHANGE_INTERVAL_SEC", 20, 2, 120)
	configuredBitrateKbps := clamp(bitrateKbps, 200, 12000)
	currentBitrateKbps := configuredBitrateKbps
	lastAbrEvalAt := time.Now()
	pendingReconfigureReason := ""
	abrTrend := ""
	abrTrendCount := 0
	abrLastChangeAt := time.Time{}
	abrEmaKbps := float64(configuredBitrateKbps)
	var restartTimestamps []time.Time
	var cmd *exec.Cmd
	var waitCh chan error
	startedAt := time.Now()
	firstPacketSeen := false

	startFfmpeg := func(mode string) error {
		args := []string{
			"-hide_banner",
			"-loglevel", "warning",
		}
		// Keep synthetic sources real-time; avoid "-re" for live capture devices.
		if normalizeSourceMode(mode) == "testsrc" {
			args = append(args, "-re")
		}
		pipelineArgs, buildErr := buildVideoPipelineArgs(mode, qualityPreset, imageQuality, encoderMode, codecMode, currentBitrateKbps, fps, rawFFmpegArgs, rawCaptureInput)
		if buildErr != nil {
			return buildErr
		}
		args = append(args, pipelineArgs...)
		args = append(args,
			"-f", "rtp",
			"-payload_type", "96",
			outURL,
		)
		log.Printf("video pipeline mode=%s ffmpeg=%s", mode, ffmpegPath)
		log.Printf("video ffmpeg args: %s", strings.Join(args, " "))

		localCmd := exec.CommandContext(ctx, ffmpegPath, args...)
		localCmd.Stdout = os.Stdout
		localCmd.Stderr = os.Stderr
		if startErr := localCmd.Start(); startErr != nil {
			return fmt.Errorf("start ffmpeg (%s): %w", ffmpegPath, startErr)
		}

		cmd = localCmd
		localWaitCh := make(chan error, 1)
		waitCh = localWaitCh
		go func(ch chan error, c *exec.Cmd) { ch <- c.Wait() }(localWaitCh, localCmd)
		startedAt = time.Now()
		firstPacketSeen = false
		if onStatus != nil {
			onStatus("pipeline_started", mode, "", map[string]any{
				"fallbackUsed": fallbackUsed,
				"bitrateKbps":  currentBitrateKbps,
			})
		}
		return nil
	}

	pruneRestarts := func(now time.Time) {
		cutoff := now.Add(-time.Duration(restartWindowSec) * time.Second)
		kept := restartTimestamps[:0]
		for _, ts := range restartTimestamps {
			if ts.After(cutoff) {
				kept = append(kept, ts)
			}
		}
		restartTimestamps = kept
	}

	restartPipeline := func(mode string, reason string, prevErr error) bool {
		now := time.Now()
		pruneRestarts(now)
		if len(restartTimestamps) >= maxRestarts {
			log.Printf("video pipeline restart limit reached mode=%s restarts=%d windowSec=%d reason=%s", mode, len(restartTimestamps), restartWindowSec, reason)
			if onStatus != nil {
				onStatus("restart_limit", mode, reason, map[string]any{
					"fallbackUsed": fallbackUsed,
					"restarts":     len(restartTimestamps),
					"windowSec":    restartWindowSec,
				})
			}
			return false
		}

		restartTimestamps = append(restartTimestamps, now)
		attempt := len(restartTimestamps)
		delayMs := restartBaseDelayMs * (1 << minInt(attempt-1, 4))
		if delayMs > 5000 {
			delayMs = 5000
		}
		log.Printf("video pipeline restarting mode=%s attempt=%d/%d delayMs=%d reason=%s err=%v", mode, attempt, maxRestarts, delayMs, reason, prevErr)
		if onStatus != nil {
			onStatus("recovering", mode, reason, map[string]any{
				"fallbackUsed": fallbackUsed,
				"restarts":     attempt,
				"delayMs":      delayMs,
			})
		}

		select {
		case <-ctx.Done():
			return false
		case <-time.After(time.Duration(delayMs) * time.Millisecond):
		}

		if err := startFfmpeg(mode); err != nil {
			log.Printf("video pipeline restart failed mode=%s: %v", mode, err)
			return false
		}
		return true
	}

		if err := startFfmpeg(modeInUse); err != nil {
			return err
		}
	defer func() {
		if cmd != nil && cmd.Process != nil {
			_ = cmd.Process.Kill()
		}
	}()

	buf := make([]byte, 2048)
	var totalPackets int64
	var totalFrames int64
	var totalBytes int64
	lastReportAt := time.Now()
	lastFrames := int64(0)
	lastBytes := int64(0)
	lastAbrAt := time.Now()
	lastAbrBytes := int64(0)
	for {
		select {
		case <-ctx.Done():
			return nil
		case ffErr := <-waitCh:
			if ctx.Err() != nil {
				return nil
			}
			reconfiguring := pendingReconfigureReason != ""
			if ffErr != nil && !reconfiguring && canFallback && !fallbackUsed && modeInUse == "capture" {
				fallbackUsed = true
				modeInUse = "testsrc"
				log.Printf("capture pipeline failed, switching to fallback source mode=%s: %v", modeInUse, ffErr)
				if onStatus != nil {
					onStatus("fallback", modeInUse, "capture_failed", map[string]any{
						"fallbackUsed": true,
					})
				}
				if err := startFfmpeg(modeInUse); err != nil {
					return fmt.Errorf("fallback start failed: %w", err)
				}
				continue
			}
			reason := "ffmpeg_exit"
			if pendingReconfigureReason != "" {
				reason = pendingReconfigureReason
				pendingReconfigureReason = ""
			}
			if restartPipeline(modeInUse, reason, ffErr) {
				continue
			}
			if ffErr != nil {
				return fmt.Errorf("ffmpeg exited: %w", ffErr)
			}
			return fmt.Errorf("ffmpeg exited")
		default:
		}

		_ = conn.SetReadDeadline(time.Now().Add(1 * time.Second))
		n, _, readErr := conn.ReadFromUDP(buf)
		if readErr != nil {
			if ne, ok := readErr.(net.Error); ok && ne.Timeout() {
				// Fail fast if ffmpeg started but produced no RTP packets during startup window.
				if !firstPacketSeen && time.Since(startedAt) >= time.Duration(startupPacketTimeoutMs)*time.Millisecond {
					startupErr := fmt.Errorf("no video RTP packets within %dms", startupPacketTimeoutMs)
					if onStatus != nil {
						onStatus("startup_timeout", modeInUse, startupErr.Error(), map[string]any{
							"fallbackUsed": fallbackUsed,
							"timeoutMs":    startupPacketTimeoutMs,
						})
					}
					if cmd != nil && cmd.Process != nil {
						_ = cmd.Process.Kill()
					}
					if canFallback && !fallbackUsed && modeInUse == "capture" {
						fallbackUsed = true
						modeInUse = "testsrc"
						log.Printf("capture startup timeout, switching to fallback source mode=%s: %v", modeInUse, startupErr)
						if onStatus != nil {
							onStatus("fallback", modeInUse, "startup_timeout", map[string]any{
								"fallbackUsed": true,
								"timeoutMs":    startupPacketTimeoutMs,
							})
						}
						if err := startFfmpeg(modeInUse); err != nil {
							return fmt.Errorf("fallback start failed: %w", err)
						}
						continue
					}
					if restartPipeline(modeInUse, "startup_timeout", startupErr) {
						continue
					}
					return startupErr
				}
				continue
			}
			if ctx.Err() != nil {
				return nil
			}
			return fmt.Errorf("udp read: %w", readErr)
		}

		var pkt rtp.Packet
		if unmarshalErr := pkt.Unmarshal(buf[:n]); unmarshalErr != nil {
			continue
		}
		firstPacketSeen = true
		totalPackets++
		totalBytes += int64(n)
		if pkt.Marker {
			totalFrames++
		}
		now := time.Now()
		if onStatus != nil && now.Sub(lastReportAt) >= 2*time.Second {
			interval := now.Sub(lastReportAt).Seconds()
			if interval > 0 {
				measuredFps := float64(totalFrames-lastFrames) / interval
				measuredKbps := int(float64((totalBytes-lastBytes)*8) / interval / 1000.0)
				onStatus("stats", modeInUse, "", map[string]any{
					"fallbackUsed": fallbackUsed,
					"measuredFps":  round2(measuredFps),
					"measuredKbps": measuredKbps,
					"frames":       totalFrames,
					"packets":      totalPackets,
				})
			}
			lastReportAt = now
			lastFrames = totalFrames
			lastBytes = totalBytes
		}

		if abrEnabled && now.Sub(lastAbrEvalAt) >= time.Duration(abrIntervalSec)*time.Second {
			lastAbrEvalAt = now
			interval := now.Sub(lastAbrAt).Seconds()
			if interval <= 0 {
				interval = float64(abrIntervalSec)
			}
			measuredKbps := int(float64((totalBytes-lastAbrBytes)*8) / interval / 1000.0)
			lastAbrAt = now
			lastAbrBytes = totalBytes
			// Smooth short bitrate spikes/drops to avoid ABR flapping.
			abrEmaKbps = (abrEmaKbps * 0.75) + (float64(measuredKbps) * 0.25)
			smoothedKbps := int(abrEmaKbps)
			nextBitrate, reason, changed := adaptiveBitrateNext(currentBitrateKbps, configuredBitrateKbps, smoothedKbps, abrMinKbps, abrMaxKbps)
			if !changed {
				abrTrend = ""
				abrTrendCount = 0
				continue
			}

			if reason == abrTrend {
				abrTrendCount++
			} else {
				abrTrend = reason
				abrTrendCount = 1
			}
			if abrTrendCount < abrConsecutiveRequired {
				continue
			}
			if !abrLastChangeAt.IsZero() && now.Sub(abrLastChangeAt) < time.Duration(abrMinChangeIntervalSec)*time.Second {
				continue
			}

			prev := currentBitrateKbps
			currentBitrateKbps = nextBitrate
			abrLastChangeAt = now
			abrTrend = ""
			abrTrendCount = 0
			pendingReconfigureReason = "abr_" + reason
			if onStatus != nil {
				onStatus("abr_update", modeInUse, reason, map[string]any{
					"bitrateKbps":     currentBitrateKbps,
					"bitratePrevKbps": prev,
					"measuredKbps":    measuredKbps,
					"smoothedKbps":    smoothedKbps,
					"fallbackUsed":    fallbackUsed,
				})
			}
			if cmd != nil && cmd.Process != nil {
				_ = cmd.Process.Kill()
			}
		}

		if writeErr := track.WriteRTP(&pkt); writeErr != nil {
			if ctx.Err() != nil {
				return nil
			}
			return fmt.Errorf("track write: %w", writeErr)
		}
	}
}

func adaptiveBitrateNext(current int, configured int, measured int, minKbps int, maxKbps int) (next int, reason string, changed bool) {
	cur := clamp(current, 200, 12000)
	cfg := clamp(configured, 200, 12000)
	minV := clamp(minKbps, 200, 20000)
	maxV := clamp(maxKbps, minV, 20000)
	cur = clamp(cur, minV, maxV)
	if measured <= 0 {
		return cur, "", false
	}
	minDeltaDown := maxInt(80, int(float64(cur)*0.05))
	minDeltaUp := maxInt(40, int(float64(cur)*0.03))

	// Use conservative hysteresis to avoid bitrate oscillation and ffmpeg restarts.
	downThreshold := int(float64(cur) * 0.65)
	if measured < downThreshold {
		proposed := int(float64(measured) * 1.18)
		proposed = clamp(proposed, minV, maxV)
		if proposed < cur-minDeltaDown {
			return proposed, "down", true
		}
	}

	upThreshold := int(float64(cur) * 1.45)
	if measured > upThreshold && cur < cfg {
		proposed := int(float64(cur) * 1.06)
		proposed = clamp(proposed, minV, minInt(maxV, cfg))
		if proposed > cur+minDeltaUp {
			return proposed, "up", true
		}
	}

	if cur > cfg {
		proposed := clamp(cfg, minV, maxV)
		if proposed < cur-minDeltaDown {
			return proposed, "back_to_target", true
		}
	}

	return cur, "", false
}

func maxInt(a int, b int) int {
	if a > b {
		return a
	}
	return b
}

func buildVideoPipelineArgs(sourceMode string, qualityPreset string, imageQuality int, encoderMode string, codecMode string, bitrateKbps int, fps int, rawFFmpegArgs string, rawCaptureInput string) ([]string, error) {
	customArgs := strings.TrimSpace(rawFFmpegArgs)
	if customArgs != "" {
		parsed, err := splitCommandLine(customArgs)
		if err != nil {
			return nil, fmt.Errorf("parse HIDBRIDGE_VIDEO_FFMPEG_ARGS: %w", err)
		}
		if len(parsed) == 0 {
			return nil, fmt.Errorf("empty HIDBRIDGE_VIDEO_FFMPEG_ARGS")
		}
		if runtime.GOOS == "windows" {
			parsed = normalizeDshowInputArgs(parsed)
		}
		return parsed, nil
	}

	switch normalizeSourceMode(sourceMode) {
	case "capture":
		normalizedEncoder := normalizeEncoderMode(encoderMode)
		pipelineFps := effectiveCaptureFps(fps, normalizedEncoder)
		inputArgs, err := buildCaptureInputArgs(rawCaptureInput, pipelineFps, qualityPreset, normalizedEncoder)
		if err != nil {
			return nil, err
		}
		args := append([]string{}, inputArgs...)
		args = append(args, "-an")
		args = append(args, defaultEncoderArgs(qualityPreset, normalizeImageQuality(imageQuality), normalizedEncoder, normalizeCodecMode(codecMode), bitrateKbps, pipelineFps)...)
		return args, nil
	case "testsrc":
		args := []string{
			"-f", "lavfi",
			"-i", fmt.Sprintf("testsrc=size=1280x720:rate=%d", clamp(fps, 5, 60)),
			"-an",
		}
		args = append(args, defaultEncoderArgs(qualityPreset, normalizeImageQuality(imageQuality), normalizeEncoderMode(encoderMode), normalizeCodecMode(codecMode), bitrateKbps, fps)...)
		return args, nil
	default:
		return nil, fmt.Errorf("unsupported source mode: %s", sourceMode)
	}
}

func buildCaptureInputArgs(raw string, fps int, qualityPreset string, encoderMode string) ([]string, error) {
	fps = effectiveCaptureFps(fps, encoderMode)
	fps = clamp(fps, 5, 60)
	rtbufsize := captureRtbufsizeForPreset(qualityPreset)
	if strings.TrimSpace(raw) != "" {
		parsed, err := splitCommandLine(raw)
		if err != nil {
			return nil, fmt.Errorf("parse HIDBRIDGE_VIDEO_CAPTURE_INPUT: %w", err)
		}
		if len(parsed) == 0 {
			return nil, fmt.Errorf("empty HIDBRIDGE_VIDEO_CAPTURE_INPUT")
		}
		if runtime.GOOS == "windows" {
			parsed = normalizeDshowInputArgs(parsed)
			if usesDshowInput(parsed) {
				parsed = upsertDshowCaptureArg(parsed, "-framerate", strconv.Itoa(fps))
				parsed = upsertDshowCaptureArg(parsed, "-rtbufsize", rtbufsize)
			}
		}
		return parsed, nil
	}

	switch runtime.GOOS {
	case "windows":
		// Default device name matches current HidBridge setups.
		return []string{"-f", "dshow", "-rtbufsize", rtbufsize, "-framerate", strconv.Itoa(fps), "-i", "video=USB3.0 Video"}, nil
	case "linux":
		return []string{"-f", "v4l2", "-framerate", strconv.Itoa(fps), "-video_size", "1280x720", "-i", "/dev/video0"}, nil
	case "darwin":
		return []string{"-f", "avfoundation", "-framerate", strconv.Itoa(fps), "-i", "0:none"}, nil
	default:
		return nil, fmt.Errorf("capture mode is unsupported on %s without HIDBRIDGE_VIDEO_CAPTURE_INPUT", runtime.GOOS)
	}
}

func captureRtbufsizeForPreset(qualityPreset string) string {
	switch normalizeQualityPreset(qualityPreset) {
	case "low-latency":
		return "64M"
	case "low":
		return "96M"
	case "optimal":
		return "256M"
	default: // balanced, high
		return "192M"
	}
}

func usesDshowInput(args []string) bool {
	for i := 0; i+1 < len(args); i++ {
		if strings.EqualFold(args[i], "-f") && strings.EqualFold(args[i+1], "dshow") {
			return true
		}
	}
	return false
}

// ensureDshowCaptureArg inserts "-key value" before "-i ..." when using dshow
// if the key is not already present.
func ensureDshowCaptureArg(args []string, key string, value string) []string {
	for i := 0; i < len(args); i++ {
		if strings.EqualFold(args[i], key) {
			return args
		}
	}

	insertAt := len(args)
	for i := 0; i < len(args); i++ {
		if strings.EqualFold(args[i], "-i") {
			insertAt = i
			break
		}
	}

	out := make([]string, 0, len(args)+2)
	out = append(out, args[:insertAt]...)
	out = append(out, key, value)
	out = append(out, args[insertAt:]...)
	return out
}

// upsertDshowCaptureArg updates an existing "-key value" pair for dshow input,
// or inserts one before "-i ..." if missing.
func upsertDshowCaptureArg(args []string, key string, value string) []string {
	for i := 0; i+1 < len(args); i++ {
		if strings.EqualFold(args[i], key) {
			out := append([]string{}, args...)
			out[i+1] = value
			return out
		}
	}
	return ensureDshowCaptureArg(args, key, value)
}

func normalizeDshowInputArgs(args []string) []string {
	if len(args) < 4 {
		return args
	}

	usesDshow := false
	for i := 0; i+1 < len(args); i++ {
		if strings.EqualFold(args[i], "-f") && strings.EqualFold(args[i+1], "dshow") {
			usesDshow = true
			break
		}
	}
	if !usesDshow {
		return args
	}

	out := make([]string, 0, len(args))
	for i := 0; i < len(args); i++ {
		if strings.EqualFold(args[i], "-i") && i+1 < len(args) {
			inputVal := args[i+1]
			out = append(out, args[i])

			// Repair unquoted Windows dshow input like:
			// -i video=USB3.0 Video
			// which would otherwise be tokenized as ["video=USB3.0", "Video"].
			if strings.HasPrefix(strings.ToLower(inputVal), "video=") {
				j := i + 2
				for j < len(args) {
					next := args[j]
					if strings.HasPrefix(next, "-") {
						break
					}
					// Stop before another key=value (e.g. audio=...).
					if strings.Contains(next, "=") {
						break
					}
					inputVal += " " + next
					j++
				}
				inputVal = normalizeDshowDeviceSelector(inputVal)
				out = append(out, inputVal)
				i = j - 1
				continue
			}

			out = append(out, inputVal)
			i++
			continue
		}

		out = append(out, args[i])
	}

	return out
}

func normalizeDshowDeviceSelector(inputVal string) string {
	lc := strings.ToLower(inputVal)
	if !strings.HasPrefix(lc, "video=") {
		return inputVal
	}

	val := strings.TrimSpace(inputVal[len("video="):])
	if val == "" {
		return inputVal
	}

	// Some launch paths can pass escaped wrappers (e.g. \"USB3.0 Video\").
	// Normalize to raw device name because exec.Command passes argv directly.
	val = strings.ReplaceAll(val, `\"`, `"`)
	val = strings.ReplaceAll(val, `\'`, `'`)
	for len(val) >= 2 {
		if strings.HasPrefix(val, "\"") && strings.HasSuffix(val, "\"") {
			val = strings.TrimSpace(strings.TrimPrefix(strings.TrimSuffix(val, "\""), "\""))
			continue
		}
		if strings.HasPrefix(val, "'") && strings.HasSuffix(val, "'") {
			val = strings.TrimSpace(strings.TrimPrefix(strings.TrimSuffix(val, "'"), "'"))
			continue
		}
		break
	}

	return "video=" + val
}

func defaultEncoderArgs(preset string, imageQuality int, encoderMode string, codecMode string, bitrateKbps int, fps int) []string {
	enc := normalizeEncoderMode(encoderMode)
	codec := normalizeCodecMode(codecMode)
	iq := normalizeImageQuality(imageQuality)
	fps = clamp(fps, 5, 60)
	adjustedBitrate := applyImageQualityToBitrate(bitrateKbps, iq)
	if codec == "auto" {
		switch enc {
		case "nvenc", "amf", "qsv", "v4l2m2m", "vaapi":
			codec = "h264"
		default:
			codec = "vp8"
		}
	}

	if codec == "h264" {
		return defaultH264ByEncoderArgs(preset, iq, enc, adjustedBitrate, fps)
	}
	return defaultVp8EncoderArgs(preset, iq, adjustedBitrate, fps)
}

func defaultH264ByEncoderArgs(preset string, imageQuality int, encoderMode string, bitrateKbps int, fps int) []string {
	switch normalizeEncoderMode(encoderMode) {
	case "nvenc":
		args := defaultH264EncoderArgs("h264_nvenc", "yuv420p", preset, bitrateKbps, fps)
		return append(args, "-tune", "ll")
	case "amf":
		args := defaultH264EncoderArgs("h264_amf", "nv12", preset, bitrateKbps, fps)
		return append(args, "-usage", "lowlatency")
	case "qsv":
		args := defaultH264EncoderArgs("h264_qsv", "nv12", preset, bitrateKbps, fps)
		return append([]string{"-look_ahead", "0"}, args...)
	case "v4l2m2m":
		return defaultH264EncoderArgs("h264_v4l2m2m", "yuv420p", preset, bitrateKbps, fps)
	case "vaapi":
		// Works only when host ffmpeg + driver expose VAAPI; otherwise probe/API should hide it.
		gop, maxrate, bufsize := qualityRateControl(preset, clamp(bitrateKbps, 200, 12000), fps)
		return []string{
			"-vaapi_device", "/dev/dri/renderD128",
			"-vf", "format=nv12,hwupload",
			"-c:v", "h264_vaapi",
			"-g", strconv.Itoa(gop),
			"-b:v", fmt.Sprintf("%dk", clamp(bitrateKbps, 200, 12000)),
			"-maxrate", fmt.Sprintf("%dk", maxrate),
			"-bufsize", fmt.Sprintf("%dk", bufsize),
		}
	case "hw":
		// Legacy generic HW mode keeps behavior stable by using CPU H.264.
		return defaultH264CpuArgs(preset, imageQuality, bitrateKbps, fps)
	case "cpu", "auto":
		return defaultH264CpuArgs(preset, imageQuality, bitrateKbps, fps)
	default:
		return defaultH264CpuArgs(preset, imageQuality, bitrateKbps, fps)
	}
}

func defaultH264CpuArgs(preset string, imageQuality int, bitrateKbps int, fps int) []string {
	bitrate := clamp(bitrateKbps, 200, 12000)
	gop, maxrate, bufsize := qualityRateControl(preset, bitrate, fps)
	keyintMin := gop
	xPreset := "veryfast"
	crf := imageQualityToX264Crf(imageQuality)
	switch normalizeQualityPreset(preset) {
	case "low":
		xPreset = "superfast"
	case "low-latency":
		xPreset = "ultrafast"
	case "high":
		xPreset = "faster"
	case "optimal":
		xPreset = "fast"
	}
	args := []string{
		"-c:v", "libx264",
		"-preset", xPreset,
		"-tune", "zerolatency",
		"-profile:v", "main",
		"-vf", "format=yuv420p",
		"-g", strconv.Itoa(gop),
		"-bf", "0",
		"-keyint_min", strconv.Itoa(keyintMin),
		"-sc_threshold", "0",
		"-b:v", fmt.Sprintf("%dk", bitrate),
		"-maxrate", fmt.Sprintf("%dk", maxrate),
		"-bufsize", fmt.Sprintf("%dk", bufsize),
		// Improve loss recovery on unstable links:
		// - repeat SPS/PPS in keyframes
		// - keep NAL units below MTU-ish size to reduce burst-loss impact
		"-x264-params", "repeat-headers=1:slice-max-size=1100",
	}
	if crf >= 0 {
		args = append(args, "-crf", strconv.Itoa(crf))
	}
	return args
}

func defaultVp8EncoderArgs(preset string, imageQuality int, bitrateKbps int, fps int) []string {
	bitrate := clamp(bitrateKbps, 200, 12000)
	gop, maxrate, bufsize := qualityRateControl(preset, bitrate, fps)
	crf := imageQualityToVp8Crf(imageQuality)

	appendCrf := func(args []string) []string {
		if crf >= 0 {
			return append(args, "-crf", strconv.Itoa(crf))
		}
		return args
	}

	switch normalizeQualityPreset(preset) {
	case "low":
		args := []string{
			"-c:v", "libvpx",
			"-deadline", "realtime",
			"-cpu-used", "10",
			"-vf", "format=yuv420p",
			"-g", strconv.Itoa(gop),
			"-b:v", fmt.Sprintf("%dk", bitrate),
			"-maxrate", fmt.Sprintf("%dk", maxrate),
			"-bufsize", fmt.Sprintf("%dk", bufsize),
		}
		return appendCrf(args)
	case "low-latency":
		args := []string{
			"-c:v", "libvpx",
			"-deadline", "realtime",
			"-cpu-used", "12",
			"-lag-in-frames", "0",
			"-error-resilient", "1",
			"-auto-alt-ref", "0",
			"-vf", "format=yuv420p",
			"-g", strconv.Itoa(gop),
			"-b:v", fmt.Sprintf("%dk", bitrate),
			"-maxrate", fmt.Sprintf("%dk", maxrate),
			"-bufsize", fmt.Sprintf("%dk", bufsize),
		}
		return appendCrf(args)
	case "high":
		args := []string{
			"-c:v", "libvpx",
			"-deadline", "realtime",
			"-cpu-used", "6",
			"-vf", "format=yuv420p",
			"-g", strconv.Itoa(gop),
			"-b:v", fmt.Sprintf("%dk", bitrate),
			"-maxrate", fmt.Sprintf("%dk", maxrate),
			"-bufsize", fmt.Sprintf("%dk", bufsize),
		}
		return appendCrf(args)
	case "optimal":
		args := []string{
			"-c:v", "libvpx",
			"-deadline", "realtime",
			"-cpu-used", "4",
			"-vf", "format=yuv420p",
			"-g", strconv.Itoa(gop),
			"-b:v", fmt.Sprintf("%dk", bitrate),
			"-maxrate", fmt.Sprintf("%dk", maxrate),
			"-bufsize", fmt.Sprintf("%dk", bufsize),
		}
		return appendCrf(args)
	default:
		args := []string{
			"-c:v", "libvpx",
			"-deadline", "realtime",
			"-cpu-used", "8",
			"-vf", "format=yuv420p",
			"-g", strconv.Itoa(gop),
			"-b:v", fmt.Sprintf("%dk", bitrate),
			"-maxrate", fmt.Sprintf("%dk", maxrate),
			"-bufsize", fmt.Sprintf("%dk", bufsize),
		}
		return appendCrf(args)
	}
}

func defaultH264EncoderArgs(encoder string, pixFmt string, preset string, bitrateKbps int, fps int) []string {
	bitrate := clamp(bitrateKbps, 200, 12000)
	gop, maxrate, bufsize := qualityRateControl(preset, bitrate, fps)
	return []string{
		"-c:v", encoder,
		"-g", strconv.Itoa(gop),
		"-bf", "0",
		"-b:v", fmt.Sprintf("%dk", bitrate),
		"-maxrate", fmt.Sprintf("%dk", maxrate),
		"-bufsize", fmt.Sprintf("%dk", bufsize),
		"-pix_fmt", pixFmt,
	}
}

// qualityRateControl centralizes preset tuning so low and low-latency can be
// intentionally different across all encoder backends.
func qualityRateControl(preset string, bitrate int, fps int) (gop int, maxrate int, bufsize int) {
	b := clamp(bitrate, 200, 12000)
	f := clamp(fps, 5, 60)
	switch normalizeQualityPreset(preset) {
	case "low":
		return clamp(f*3, 30, 180), int(float64(b) * 1.15), b * 3
	case "low-latency":
		return clamp(f, 15, 60), int(float64(b) * 1.10), b * 2
	case "high":
		// Shorter GOP improves visual recovery after packet loss.
		return clamp((f*3)/2, 20, 90), int(float64(b) * 1.2), b * 2
	case "optimal":
		// Keep "optimal" quality but prefer robustness on live control links.
		return clamp(f, 20, 60), int(float64(b) * 1.08), b * 2
	default: // balanced
		return clamp((f*3)/2, 20, 90), int(float64(b) * 1.2), b * 2
	}
}

func effectiveCaptureFps(fps int, encoderMode string) int {
	target := clamp(fps, 5, 60)
	// CPU capture defaults to 30 fps for stability on common 1080p cards.
	if normalizeEncoderMode(encoderMode) == "cpu" && target > 30 {
		return 30
	}
	return target
}

func applyImageQualityToBitrate(bitrateKbps int, imageQuality int) int {
	bitrate := clamp(bitrateKbps, 200, 12000)
	q := normalizeImageQuality(imageQuality)
	if q == 0 {
		return bitrate
	}

	// Keep image quality influence gentle to avoid aggressive bandwidth swings.
	// 1..100 maps to 0.85x..1.15x bitrate.
	scale := 0.85 + (0.30 * float64(q-1) / 99.0)
	scaled := int(float64(bitrate) * scale)
	return clamp(scaled, 200, 12000)
}

func imageQualityToVp8Crf(imageQuality int) int {
	q := normalizeImageQuality(imageQuality)
	if q == 0 {
		return -1
	}
	// VP8/libvpx CRF: lower is better. Keep conservative range for realtime.
	return int(50.0 - (float64(q-1) * 40.0 / 99.0)) // ~50..10
}

func imageQualityToX264Crf(imageQuality int) int {
	q := normalizeImageQuality(imageQuality)
	if q == 0 {
		return -1
	}
	// x264 CRF: lower is better.
	return int(35.0 - (float64(q-1) * 17.0 / 99.0)) // ~35..18
}

func normalizeImageQuality(v int) int {
	if v < 1 || v > 100 {
		return 0
	}
	return v
}

func envOptionalIntInRange(name string, min int, max int) int {
	raw := strings.TrimSpace(os.Getenv(name))
	if raw == "" {
		return 0
	}
	n, err := strconv.Atoi(raw)
	if err != nil {
		return 0
	}
	if n < min || n > max {
		return 0
	}
	return n
}

func envBool(name string, defaultVal bool) bool {
	raw := strings.TrimSpace(strings.ToLower(os.Getenv(name)))
	if raw == "" {
		return defaultVal
	}
	switch raw {
	case "1", "true", "yes", "on":
		return true
	case "0", "false", "no", "off":
		return false
	default:
		return defaultVal
	}
}

func envIntInRange(name string, defaultVal int, min int, max int) int {
	raw := strings.TrimSpace(os.Getenv(name))
	if raw == "" {
		return clamp(defaultVal, min, max)
	}
	n, err := strconv.Atoi(raw)
	if err != nil {
		return clamp(defaultVal, min, max)
	}
	return clamp(n, min, max)
}

func clamp(v int, min int, max int) int {
	if v < min {
		return min
	}
	if v > max {
		return max
	}
	return v
}

func minInt(a int, b int) int {
	if a < b {
		return a
	}
	return b
}

func round2(v float64) float64 {
	if v < 0 {
		return 0
	}
	return float64(int(v*100+0.5)) / 100
}

func normalizeSourceMode(sourceMode string) string {
	switch strings.ToLower(strings.TrimSpace(sourceMode)) {
	case "", "testsrc":
		return "testsrc"
	case "capture":
		return "capture"
	default:
		return strings.ToLower(strings.TrimSpace(sourceMode))
	}
}

func normalizeQualityPreset(qualityPreset string) string {
	switch strings.ToLower(strings.TrimSpace(qualityPreset)) {
	case "low":
		return "low"
	case "low-latency":
		return "low-latency"
	case "optimal":
		return "optimal"
	case "high":
		return "high"
	default:
		return "balanced"
	}
}

func normalizeEncoderMode(encoderMode string) string {
	switch strings.ToLower(strings.TrimSpace(encoderMode)) {
	case "", "auto":
		return "auto"
	case "cpu":
		return "cpu"
	case "hw":
		return "hw"
	case "nvenc":
		return "nvenc"
	case "amf":
		return "amf"
	case "qsv":
		return "qsv"
	case "v4l2m2m":
		return "v4l2m2m"
	case "vaapi":
		return "vaapi"
	default:
		return "auto"
	}
}

func normalizeCodecMode(codecMode string) string {
	switch strings.ToLower(strings.TrimSpace(codecMode)) {
	case "", "auto":
		return "auto"
	case "vp8":
		return "vp8"
	case "h264":
		return "h264"
	default:
		return "auto"
	}
}

func selectedVideoMimeType(codecMode string, encoderMode string) string {
	switch normalizeCodecMode(codecMode) {
	case "vp8":
		return webrtc.MimeTypeVP8
	case "h264":
		return webrtc.MimeTypeH264
	}

	switch normalizeEncoderMode(encoderMode) {
	case "nvenc", "amf", "qsv", "v4l2m2m", "vaapi":
		return webrtc.MimeTypeH264
	default:
		return webrtc.MimeTypeVP8
	}
}

func splitCommandLine(raw string) ([]string, error) {
	var out []string
	var cur strings.Builder
	var quote rune
	escape := false

	flush := func() {
		if cur.Len() == 0 {
			return
		}
		out = append(out, cur.String())
		cur.Reset()
	}

	for _, r := range raw {
		if escape {
			cur.WriteRune(r)
			escape = false
			continue
		}
		if quote != 0 {
			if r == '\\' {
				escape = true
				continue
			}
			if r == quote {
				quote = 0
				continue
			}
			cur.WriteRune(r)
			continue
		}

		if unicode.IsSpace(r) {
			flush()
			continue
		}
		if r == '"' || r == '\'' {
			quote = r
			continue
		}
		if r == '\\' {
			escape = true
			continue
		}
		cur.WriteRune(r)
	}

	if escape {
		cur.WriteRune('\\')
	}
	if quote != 0 {
		return nil, fmt.Errorf("unterminated quote in command line")
	}
	flush()
	return out, nil
}

func dialWS(ctx context.Context, base *url.URL, path string, token string) (*websocket.Conn, error) {
	wsURL := *base
	if wsURL.Scheme == "https" {
		wsURL.Scheme = "wss"
	} else {
		wsURL.Scheme = "ws"
	}
	wsURL.Path = path
	wsURL.RawQuery = ""

	h := http.Header{}
	if strings.TrimSpace(token) != "" {
		h.Set("X-HID-Token", token)
	}

	d := websocket.Dialer{
		HandshakeTimeout: 10 * time.Second,
	}

	c, _, err := d.DialContext(ctx, wsURL.String(), h)
	return c, err
}

func sendJSON(ws *websocket.Conn, v any) error {
	b, err := json.Marshal(v)
	if err != nil {
		return err
	}
	return ws.WriteMessage(websocket.TextMessage, b)
}

func (p *peerState) sendSignal(v any) error {
	p.sigMu.Lock()
	defer p.sigMu.Unlock()
	return sendJSON(p.sigWS, v)
}

func envOr(key, def string) string {
	if v := strings.TrimSpace(os.Getenv(key)); v != "" {
		return v
	}
	return def
}

func registerHelperPidFile() func() {
	pidFile := strings.TrimSpace(os.Getenv("HIDBRIDGE_HELPER_PIDFILE"))
	if pidFile == "" {
		return func() {}
	}

	dir := filepath.Dir(pidFile)
	if dir != "" && dir != "." {
		if err := os.MkdirAll(dir, 0o755); err != nil {
			log.Printf("pidfile mkdir failed: %v", err)
			return func() {}
		}
	}

	pid := os.Getpid()
	if err := os.WriteFile(pidFile, []byte(strconv.Itoa(pid)), 0o644); err != nil {
		log.Printf("pidfile write failed: %v", err)
		return func() {}
	}
	log.Printf("pidfile: %s pid=%d", pidFile, pid)

	return func() {
		if err := os.Remove(pidFile); err != nil && !os.IsNotExist(err) {
			log.Printf("pidfile remove failed: %v", err)
		}
	}
}
