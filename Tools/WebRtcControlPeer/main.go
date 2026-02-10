package main

import (
	"context"
	"encoding/json"
	"flag"
	"fmt"
	"log"
	"net/http"
	"net/url"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"sync"
	"time"

	"github.com/gorilla/websocket"
	"github.com/pion/webrtc/v3"
)

// This tool runs next to HidControlServer and acts as a WebRTC "control peer":
// - joins a signaling room via /ws/webrtc
// - accepts offers from browser clients
// - opens a DataChannel ("data" / any label) and forwards JSON messages to /ws/hid
//
// Goal: get WebRTC control-plane working without adding a WebRTC stack to the .NET server.

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
	Kind string           `json:"kind"`
	SDP  webrtc.SessionDescription `json:"sdp"`
}

type candidatePayload struct {
	Kind      string                  `json:"kind"`
	Candidate webrtc.ICECandidateInit `json:"candidate"`
}

func main() {
	var (
		serverURL = flag.String("server", envOr("HIDBRIDGE_SERVER_URL", "http://127.0.0.1:8080"), "HidControlServer base URL (http://host:port)")
		token     = flag.String("token", envOr("HIDBRIDGE_TOKEN", ""), "Server token (X-HID-Token)")
		room      = flag.String("room", envOr("HIDBRIDGE_WEBRTC_ROOM", "control"), "Signaling room name")
		stun      = flag.String("stun", envOr("HIDBRIDGE_STUN", "stun:stun.l.google.com:19302"), "STUN server URL (stun:host:port)")
	)
	flag.Parse()

	base, err := url.Parse(*serverURL)
	if err != nil {
		log.Fatalf("bad --server: %v", err)
	}

	log.Printf("webrtc control peer starting")
	log.Printf("server=%s room=%s stun=%s", base.String(), *room, *stun)
	cleanupPidFile := registerHelperPidFile()
	defer cleanupPidFile()

	// Long-running loop: reconnect on transport failures instead of exiting the helper process.
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

	hidWS, err := dialWS(ctx, base, "/ws/hid", token)
	if err != nil {
		return fmt.Errorf("dial /ws/hid: %w", err)
	}
	defer hidWS.Close()

	var hidMu sync.Mutex
	sendToHid := func(msg string) (string, error) {
		hidMu.Lock()
		defer hidMu.Unlock()

		if err := hidWS.WriteMessage(websocket.TextMessage, []byte(msg)); err != nil {
			return "", err
		}
		_, resp, err := hidWS.ReadMessage()
		if err != nil {
			return "", err
		}
		return string(resp), nil
	}

	sigWS, err := dialWS(ctx, base, "/ws/webrtc", token)
	if err != nil {
		return fmt.Errorf("dial /ws/webrtc: %w", err)
	}
	defer sigWS.Close()

	if err := sendJSON(sigWS, signalMessage{Type: "join", Room: room}); err != nil {
		return fmt.Errorf("join room: %w", err)
	}

	peer := newPeerState(stun, room, sigWS, sendToHid)
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
	room      string
	sigWS     *websocket.Conn
	sendToHid func(string) (string, error)
	sigMu     sync.Mutex

	pcMu sync.Mutex
	pc   *webrtc.PeerConnection
	// activePeerId is the signaling "from" peer that we're currently paired with.
	// This tool maintains a single PeerConnection; without pairing, multiple browser tabs
	// joining the same room would fight over the session and break existing connections.
	activePeerId string
}

func newPeerState(stun, room string, sigWS *websocket.Conn, sendToHid func(string) (string, error)) *peerState {
	return &peerState{
		room:      room,
		sigWS:     sigWS,
		sendToHid: sendToHid,
		pc:        nil,
		activePeerId: "",
	}
}

func (p *peerState) ensurePC(ctx context.Context, stun string) (*webrtc.PeerConnection, error) {
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

	pc.OnICECandidate(func(c *webrtc.ICECandidate) {
		if c == nil {
			// end-of-candidates
			return
		}
		init := c.ToJSON()
		// Some browsers send empty-string candidates; ignore.
		if strings.TrimSpace(init.Candidate) == "" {
			return
		}
		payload, _ := json.Marshal(candidatePayload{Kind: "candidate", Candidate: init})
		_ = p.sendSignal(signalMessage{Type: "signal", Room: p.room, Data: payload})
	})

	pc.OnConnectionStateChange(func(s webrtc.PeerConnectionState) {
		log.Printf("pc state: %s", s.String())
		// For MVP: aggressively release the session when the browser disconnects so another browser
		// can connect to the same room without waiting for timeouts.
		if s == webrtc.PeerConnectionStateDisconnected || s == webrtc.PeerConnectionStateFailed || s == webrtc.PeerConnectionStateClosed {
			// Allow new sessions after failure.
			p.pcMu.Lock()
			defer p.pcMu.Unlock()
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
			if m.IsString {
				req := string(m.Data)
				resp, err := p.sendToHid(req)
				if err != nil {
					_ = dc.SendText(`{"ok":false,"error":"hid_forward_failed"}`)
					return
				}
				_ = dc.SendText(resp)
				return
			}
			_ = dc.SendText(`{"ok":false,"error":"binary_not_supported"}`)
		})
	})

	p.pc = pc
	return pc, nil
}

func (p *peerState) handleSignal(ctx context.Context, from string, data json.RawMessage) {
	// Determine if it's offer/answer/candidate.
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
	if p.activePeerId == "" {
		return false
	}
	return p.activePeerId == from
}

// tryAdoptPeer sets activePeerId if it's not set yet.
// Returns true if "from" is now the active peer (either adopted or already active).
func (p *peerState) tryAdoptPeer(from string) bool {
	if strings.TrimSpace(from) == "" {
		// If signaling doesn't provide a "from", pairing can't work. Fail closed to avoid
		// destabilizing an existing session.
		return false
	}

	p.pcMu.Lock()
	defer p.pcMu.Unlock()
	if p.activePeerId == "" {
		p.activePeerId = from
		return true
	}
	return p.activePeerId == from
}

func (p *peerState) onOffer(ctx context.Context, offer webrtc.SessionDescription) {
	stun := envOr("HIDBRIDGE_STUN", "stun:stun.l.google.com:19302")
	pc, err := p.ensurePC(ctx, stun)
	if err != nil {
		log.Printf("ensurePC: %v", err)
		return
	}

	if err := pc.SetRemoteDescription(offer); err != nil {
		log.Printf("setRemoteDescription: %v", err)
		return
	}

	answer, err := pc.CreateAnswer(nil)
	if err != nil {
		log.Printf("createAnswer: %v", err)
		return
	}

	if err := pc.SetLocalDescription(answer); err != nil {
		log.Printf("setLocalDescription: %v", err)
		return
	}

	payload, _ := json.Marshal(sdpPayload{Kind: "answer", SDP: *pc.LocalDescription()})
	if err := p.sendSignal(signalMessage{Type: "signal", Room: p.room, Data: payload}); err != nil {
		log.Printf("send answer: %v", err)
		return
	}
}

func (p *peerState) onCandidate(ctx context.Context, cand webrtc.ICECandidateInit) {
	p.pcMu.Lock()
	pc := p.pc
	p.pcMu.Unlock()
	if pc == nil {
		return
	}
	if err := pc.AddICECandidate(cand); err != nil {
		// Most common when candidates arrive before remote description; ignore for MVP.
		return
	}
}

func dialWS(ctx context.Context, base *url.URL, path string, token string) (*websocket.Conn, error) {
	u := *base
	u.Path = strings.TrimSuffix(u.Path, "/") + path
	if strings.EqualFold(u.Scheme, "https") {
		u.Scheme = "wss"
	} else {
		u.Scheme = "ws"
	}

	h := http.Header{}
	if token != "" {
		h.Set("X-HID-Token", token)
	}

	dialer := websocket.Dialer{HandshakeTimeout: 10 * time.Second}
	ws, resp, err := dialer.DialContext(ctx, u.String(), h)
	if err != nil {
		if resp != nil {
			return nil, fmt.Errorf("%w (http %d)", err, resp.StatusCode)
		}
		return nil, err
	}
	return ws, nil
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

func envOr(name, fallback string) string {
	v := os.Getenv(name)
	if strings.TrimSpace(v) == "" {
		return fallback
	}
	return v
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
