# HidBridge.Edge.HidBridgeProtocol

Device/protocol adapter library for edge runtimes.

Current scope:

- exp-022 compatible control-websocket ACK parser (`ControlWsAckParser`)
- websocket-backed command executor (`ControlWsCommandExecutor`)
- direct UART HID command executor (`UartHidCommandExecutor`)
- shared HID action normalization/dispatch lives in `HidBridge.Transport.Uart/HidBridgeUartCommandDispatcher`

This project is the incremental migration target for low-level exp-022 protocol logic.
