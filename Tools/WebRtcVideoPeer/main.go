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
	"strings"
	"sync"
	"time"

	"github.com/gorilla/websocket"
	"github.com/pion/rtp"
	"github.com/pion/webrtc/v3"
)

// This tool runs next to HidControlServer and acts as a WebRTC "video peer":
// - joins a signaling room via /ws/webrtc
// - accepts offers from browser clients
// - publishes a synthetic VP8 video track (ffmpeg test source -> RTP -> Pion track)
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
		serverURL = flag.String("server", envOr("HIDBRIDGE_SERVER_URL", "http://127.0.0.1:8080"), "HidControlServer base URL (http://host:port)")
		token     = flag.String("token", envOr("HIDBRIDGE_TOKEN", ""), "Server token (X-HID-Token)")
		room      = flag.String("room", envOr("HIDBRIDGE_WEBRTC_ROOM", "video"), "Signaling room name")
		stun      = flag.String("stun", envOr("HIDBRIDGE_STUN", "stun:stun.l.google.com:19302"), "STUN server URL (stun:host:port)")
	)
	flag.Parse()

	base, err := url.Parse(*serverURL)
	if err != nil {
		log.Fatalf("bad --server: %v", err)
	}

	log.Printf("webrtc video peer starting")
	log.Printf("server=%s room=%s stun=%s", base.String(), *room, *stun)

	// Long-running loop: reconnect on transport failures instead of exiting.
	backoff := 1 * time.Second
	for {
		err := runSession(base, *token, *room, *stun)
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

func runSession(base *url.URL, token string, room string, stun string) error {
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

	peer := newPeerState(stun, room, sigWS)
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

	pcMu         sync.Mutex
	pc           *webrtc.PeerConnection
	streamCancel context.CancelFunc

	activePeerId string
}

func newPeerState(stun, room string, sigWS *websocket.Conn) *peerState {
	_ = stun
	return &peerState{
		room:         room,
		sigWS:        sigWS,
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
		if err := runSyntheticVideoStream(streamCtx, videoTrack); err != nil && streamCtx.Err() == nil {
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

		dc.OnOpen(func() { log.Printf("datachannel open: %s", label) })
		dc.OnClose(func() { log.Printf("datachannel close: %s", label) })
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
	pc, err := p.ensurePC(envOr("HIDBRIDGE_STUN", "stun:stun.l.google.com:19302"))
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
	pc, err := p.ensurePC(envOr("HIDBRIDGE_STUN", "stun:stun.l.google.com:19302"))
	if err != nil {
		return
	}
	if err := pc.AddICECandidate(cand); err != nil {
		// Some candidates can race; ignore.
		return
	}
	_ = ctx
}

func runSyntheticVideoStream(ctx context.Context, track *webrtc.TrackLocalStaticRTP) error {
	conn, err := net.ListenUDP("udp4", &net.UDPAddr{IP: net.ParseIP("127.0.0.1"), Port: 0})
	if err != nil {
		return fmt.Errorf("listen udp: %w", err)
	}
	defer conn.Close()

	ffmpegPath := envOr("HIDBRIDGE_FFMPEG", "ffmpeg")
	port := conn.LocalAddr().(*net.UDPAddr).Port
	outURL := fmt.Sprintf("rtp://127.0.0.1:%d?pkt_size=1200", port)

	args := []string{
		"-hide_banner",
		"-loglevel", "warning",
		"-re",
		"-f", "lavfi",
		"-i", "testsrc=size=1280x720:rate=30",
		"-an",
		"-c:v", "libvpx",
		"-deadline", "realtime",
		"-cpu-used", "8",
		"-g", "60",
		"-b:v", "1200k",
		"-pix_fmt", "yuv420p",
		"-f", "rtp",
		"-payload_type", "96",
		outURL,
	}

	cmd := exec.CommandContext(ctx, ffmpegPath, args...)
	cmd.Stdout = os.Stdout
	cmd.Stderr = os.Stderr
	if err := cmd.Start(); err != nil {
		return fmt.Errorf("start ffmpeg (%s): %w", ffmpegPath, err)
	}

	waitCh := make(chan error, 1)
	go func() { waitCh <- cmd.Wait() }()
	defer func() {
		if cmd.Process != nil {
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
