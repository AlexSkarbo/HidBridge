namespace HidBridge.EdgeProxy.Agent;

/// <summary>
/// Selects the low-level command execution path used by the edge proxy worker.
/// </summary>
public enum EdgeProxyCommandExecutorKind
{
    /// <summary>
    /// Uses local websocket control endpoint compatible with exp-022 ACK contract.
    /// </summary>
    ControlWs = 0,

    /// <summary>
    /// Uses direct UART HID bridge transport without exp-022 runtime dependency.
    /// </summary>
    UartHid = 1,
}
