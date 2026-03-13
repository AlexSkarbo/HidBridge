namespace HidBridge.Transport.Uart;

/// <summary>
/// Configures the UART bridge client used by the HID connector.
/// </summary>
public sealed record HidBridgeUartClientOptions(
    string PortName,
    int BaudRate,
    string HmacKey,
    string MasterSecret = "",
    byte MouseInterfaceSelector = 0xFF,
    byte KeyboardInterfaceSelector = 0xFE,
    int CommandTimeoutMs = 300,
    int InjectTimeoutMs = 200,
    int InjectRetries = 2);

/// <summary>
/// Captures a lightweight runtime health snapshot of the UART transport.
/// </summary>
public sealed record HidBridgeUartHealth(
    string PortName,
    int BaudRate,
    byte MouseInterfaceSelector,
    byte KeyboardInterfaceSelector,
    byte? ResolvedMouseInterface,
    byte? ResolvedKeyboardInterface,
    int InterfaceCount,
    bool IsConnected,
    DateTimeOffset SampledAt);
