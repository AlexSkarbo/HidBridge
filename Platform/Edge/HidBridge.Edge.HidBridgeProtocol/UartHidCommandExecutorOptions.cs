namespace HidBridge.Edge.HidBridgeProtocol;

/// <summary>
/// Configures UART HID command execution for edge-proxy runtime.
/// </summary>
public sealed record UartHidCommandExecutorOptions(
    string PortName,
    int BaudRate,
    string HmacKey,
    string MasterSecret,
    byte MouseInterfaceSelector,
    byte KeyboardInterfaceSelector,
    int CommandTimeoutMs,
    int InjectTimeoutMs,
    int InjectRetries,
    bool ReleasePortAfterExecute);
