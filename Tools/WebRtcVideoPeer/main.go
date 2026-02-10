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
// - opens/accepts a DataChannel and echoes text messages back (debug/control)
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
		qualityPreset = flag.String("quality-preset", envOr("HIDBRIDGE_VIDEO_QUALITY_PRESET", "balanced"), "Video quality preset: low|balanced|high")
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
	log.Printf("webrtc video peer starting")
	log.Printf("server=%s room=%s stun=%s sourceMode=%s qualityPreset=%s bitrateKbps=%d fps=%d", base.String(), *room, *stun, mode, preset, *bitrateKbps, *fps)
	cleanupPidFile := registerHelperPidFile()
	defer cleanupPidFile()

	// Long-running loop: reconnect on transport failures instead of exiting.
	backoff := 1 * time.Second
	for {
		err := runSession(base, *token, *room, *stun, mode, preset, *bitrateKbps, *fps, *ffmpegArgs, *captureInput)
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

func runSession(base *url.URL, token string, room string, stun string, sourceMode string, qualityPreset string, bitrateKbps int, fps int, ffmpegArgs string, captureInput string) error {
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

	peer := newPeerState(stun, room, sourceMode, qualityPreset, bitrateKbps, fps, ffmpegArgs, captureInput, sigWS)
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
	bitrateKbps  int
	fps          int
	ffmpegArgs   string
	captureInput string

	pcMu         sync.Mutex
	pc           *webrtc.PeerConnection
	streamCancel context.CancelFunc

	activePeerId string
	dcMu         sync.Mutex
	dc           *webrtc.DataChannel
	pendingNotice string
}

func newPeerState(stun, room, sourceMode, qualityPreset string, bitrateKbps int, fps int, ffmpegArgs, captureInput string, sigWS *websocket.Conn) *peerState {
	return &peerState{
		room:         room,
		sigWS:        sigWS,
		stun:         stun,
		sourceMode:   normalizeSourceMode(sourceMode),
		qualityPreset: normalizeQualityPreset(qualityPreset),
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
		webrtc.RTPCodecCapability{MimeType: webrtc.MimeTypeVP8, ClockRate: 90000},
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
		if err := runVideoStream(streamCtx, videoTrack, p.sourceMode, p.qualityPreset, p.bitrateKbps, p.fps, p.ffmpegArgs, p.captureInput, p.notifyVideoStatus); err != nil && streamCtx.Err() == nil {
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
		if s == webrtc.PeerConnectionStateDisconnected || s == webrtc.PeerConnectionStateFailed || s == webrtc.PeerConnectionStateClosed {
			p.pcMu.Lock()
			defer p.pcMu.Unlock()
			if p.streamCancel != nil {
				p.streamCancel()
				p.streamCancel = nil
			}
			p.pc = nil
			p.activePeerId = ""
		}
	})

	pc.OnDataChannel(func(dc *webrtc.DataChannel) {
		label := dc.Label()
		log.Printf("datachannel: %s", label)
		p.dcMu.Lock()
		p.dc = dc
		p.dcMu.Unlock()

		dc.OnOpen(func() {
			log.Printf("datachannel open: %s", label)
			p.dcMu.Lock()
			notice := p.pendingNotice
			p.dcMu.Unlock()
			if strings.TrimSpace(notice) != "" {
				_ = dc.SendText(notice)
			}
		})
		dc.OnClose(func() {
			log.Printf("datachannel close: %s", label)
			p.dcMu.Lock()
			p.dc = nil
			p.dcMu.Unlock()
		})
		dc.OnMessage(func(m webrtc.DataChannelMessage) {
			if !m.IsString {
				_ = dc.SendText(`{"ok":false,"error":"binary_not_supported"}`)
				return
			}
			// Echo back. This is intentionally minimal until we add video tracks.
			_ = dc.SendText(string(m.Data))
		})
	})

	p.pc = pc
	return pc, nil
}

func (p *peerState) notifyVideoStatus(event string, mode string, detail string) {
	payload := map[string]string{
		"type":  "video.status",
		"event": event,
		"mode":  mode,
	}
	if strings.TrimSpace(detail) != "" {
		payload["detail"] = detail
	}
	raw, _ := json.Marshal(payload)
	msg := string(raw)

	p.dcMu.Lock()
	p.pendingNotice = msg
	dc := p.dc
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

func runVideoStream(ctx context.Context, track *webrtc.TrackLocalStaticRTP, sourceMode string, qualityPreset string, bitrateKbps int, fps int, rawFFmpegArgs string, rawCaptureInput string, onStatus func(event string, mode string, detail string)) error {
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
	var cmd *exec.Cmd
	var waitCh chan error

	startFfmpeg := func(mode string) error {
		args := []string{
			"-hide_banner",
			"-loglevel", "warning",
			"-re",
		}
		pipelineArgs, buildErr := buildVideoPipelineArgs(mode, qualityPreset, bitrateKbps, fps, rawFFmpegArgs, rawCaptureInput)
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
		waitCh = make(chan error, 1)
		go func() { waitCh <- localCmd.Wait() }()
		return nil
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
	for {
		select {
		case <-ctx.Done():
			return nil
		case ffErr := <-waitCh:
			if ctx.Err() != nil {
				return nil
			}
			if ffErr != nil && canFallback && !fallbackUsed && modeInUse == "capture" {
				fallbackUsed = true
				modeInUse = "testsrc"
				log.Printf("capture pipeline failed, switching to fallback source mode=%s: %v", modeInUse, ffErr)
				if onStatus != nil {
					onStatus("fallback", modeInUse, "capture_failed")
				}
				if err := startFfmpeg(modeInUse); err != nil {
					return fmt.Errorf("fallback start failed: %w", err)
				}
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

		if writeErr := track.WriteRTP(&pkt); writeErr != nil {
			if ctx.Err() != nil {
				return nil
			}
			return fmt.Errorf("track write: %w", writeErr)
		}
	}
}

func buildVideoPipelineArgs(sourceMode string, qualityPreset string, bitrateKbps int, fps int, rawFFmpegArgs string, rawCaptureInput string) ([]string, error) {
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
		inputArgs, err := buildCaptureInputArgs(rawCaptureInput, fps)
		if err != nil {
			return nil, err
		}
		args := append([]string{}, inputArgs...)
		args = append(args, "-an")
		args = append(args, defaultVp8EncoderArgs(qualityPreset, bitrateKbps)...)
		return args, nil
	case "testsrc":
		args := []string{
			"-f", "lavfi",
			"-i", fmt.Sprintf("testsrc=size=1280x720:rate=%d", clamp(fps, 5, 60)),
			"-an",
		}
		args = append(args, defaultVp8EncoderArgs(qualityPreset, bitrateKbps)...)
		return args, nil
	default:
		return nil, fmt.Errorf("unsupported source mode: %s", sourceMode)
	}
}

func buildCaptureInputArgs(raw string, fps int) ([]string, error) {
	fps = clamp(fps, 5, 60)
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
				parsed = ensureDshowCaptureArg(parsed, "-framerate", strconv.Itoa(fps))
				parsed = ensureDshowCaptureArg(parsed, "-rtbufsize", "256M")
			}
		}
		return parsed, nil
	}

	switch runtime.GOOS {
	case "windows":
		// Default device name matches current HidBridge setups.
		return []string{"-f", "dshow", "-rtbufsize", "256M", "-framerate", strconv.Itoa(fps), "-i", "video=USB3.0 Video"}, nil
	case "linux":
		return []string{"-f", "v4l2", "-framerate", strconv.Itoa(fps), "-video_size", "1280x720", "-i", "/dev/video0"}, nil
	case "darwin":
		return []string{"-f", "avfoundation", "-framerate", strconv.Itoa(fps), "-i", "0:none"}, nil
	default:
		return nil, fmt.Errorf("capture mode is unsupported on %s without HIDBRIDGE_VIDEO_CAPTURE_INPUT", runtime.GOOS)
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

func defaultVp8EncoderArgs(preset string, bitrateKbps int) []string {
	bitrate := clamp(bitrateKbps, 200, 12000)
	maxrate := int(float64(bitrate) * 1.2)
	bufsize := bitrate * 2

	switch normalizeQualityPreset(preset) {
	case "low":
		return []string{
			"-c:v", "libvpx",
			"-deadline", "realtime",
			"-cpu-used", "10",
			"-g", "60",
			"-b:v", fmt.Sprintf("%dk", bitrate),
			"-maxrate", fmt.Sprintf("%dk", maxrate),
			"-bufsize", fmt.Sprintf("%dk", bufsize),
			"-pix_fmt", "yuv420p",
		}
	case "high":
		return []string{
			"-c:v", "libvpx",
			"-deadline", "realtime",
			"-cpu-used", "6",
			"-g", "60",
			"-b:v", fmt.Sprintf("%dk", bitrate),
			"-maxrate", fmt.Sprintf("%dk", maxrate),
			"-bufsize", fmt.Sprintf("%dk", bufsize),
			"-pix_fmt", "yuv420p",
		}
	default:
		return []string{
			"-c:v", "libvpx",
			"-deadline", "realtime",
			"-cpu-used", "8",
			"-g", "60",
			"-b:v", fmt.Sprintf("%dk", bitrate),
			"-maxrate", fmt.Sprintf("%dk", maxrate),
			"-bufsize", fmt.Sprintf("%dk", bufsize),
			"-pix_fmt", "yuv420p",
		}
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
	case "high":
		return "high"
	default:
		return "balanced"
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
