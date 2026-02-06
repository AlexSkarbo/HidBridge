module hidbridge/webrtccontrolpeer

go 1.18

require (
	github.com/gorilla/websocket v1.5.3
	// Keep this pinned to a version that still supports Go 1.18 (common on Windows/RPi setups).
	github.com/pion/webrtc/v3 v3.2.34
)
