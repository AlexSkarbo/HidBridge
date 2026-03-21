using HidBridge.SessionOrchestrator;

namespace HidBridge.ControlPlane.Api;

/// <summary>
/// Carries the effective runtime settings that the Control Plane API exposes for diagnostics.
/// </summary>
public sealed class ApiRuntimeSettings
{
    /// <summary>
    /// Gets the resolved data root used by the file persistence provider.
    /// </summary>
    public required string DataRoot { get; init; }

    /// <summary>
    /// Gets the selected persistence provider name.
    /// </summary>
    public required string PersistenceProvider { get; init; }

    /// <summary>
    /// Gets the selected realtime transport provider name.
    /// </summary>
    public required string TransportProvider { get; init; }

    /// <summary>
    /// Gets the SQL schema name used by the SQL persistence provider.
    /// </summary>
    public required string SqlSchema { get; init; }

    /// <summary>
    /// Gets a value indicating whether SQL migrations are applied automatically at startup.
    /// </summary>
    public required bool SqlApplyMigrations { get; init; }

    /// <summary>
    /// Gets the UART port assigned to the local HID bridge connector.
    /// </summary>
    public required string UartPort { get; init; }

    /// <summary>
    /// Gets the UART baud rate assigned to the local HID bridge connector.
    /// </summary>
    public required int UartBaudRate { get; init; }

    /// <summary>
    /// Gets the logical mouse selector value used during HID interface discovery.
    /// </summary>
    public required byte MouseSelector { get; init; }

    /// <summary>
    /// Gets the logical keyboard selector value used during HID interface discovery.
    /// </summary>
    public required byte KeyboardSelector { get; init; }

    /// <summary>
    /// Gets the UART command timeout used for non-inject control operations.
    /// </summary>
    public required int UartCommandTimeoutMs { get; init; }

    /// <summary>
    /// Gets the UART inject timeout used for HID report command acknowledgments.
    /// </summary>
    public required int UartInjectTimeoutMs { get; init; }

    /// <summary>
    /// Gets the number of retries used for UART inject commands.
    /// </summary>
    public required int UartInjectRetries { get; init; }

    /// <summary>
    /// Gets a value indicating whether per-device derived key mode is configured.
    /// </summary>
    public required bool UartUsesMasterSecret { get; init; }

    /// <summary>
    /// Gets a value indicating whether UART health probes avoid opening the serial port.
    /// </summary>
    public required bool UartPassiveHealthMode { get; init; }

    /// <summary>
    /// Gets a value indicating whether UART command execution releases COM handle immediately after each command.
    /// </summary>
    public required bool UartReleasePortAfterExecute { get; init; }

    /// <summary>
    /// Gets a value indicating whether WebRTC transport requires endpoint capability advertisement.
    /// </summary>
    public required bool WebRtcRequireCapability { get; init; }

    /// <summary>
    /// Gets a value indicating whether WebRTC command dispatch bridges through connector execution.
    /// </summary>
    public required bool WebRtcEnableConnectorBridge { get; init; }

    /// <summary>
    /// Gets a value indicating whether WebRTC transport enables direct DCD control-channel bridge prior to signal/relay fallback.
    /// </summary>
    public required bool WebRtcEnableDcdControlBridge { get; init; }

    /// <summary>
    /// Gets a value indicating whether WebRTC readiness policy requires media path readiness.
    /// </summary>
    public required bool WebRtcRequireMediaReady { get; init; }

    /// <summary>
    /// Gets the peer heartbeat staleness threshold (seconds) used by WebRTC relay online-peer detection.
    /// </summary>
    public required int WebRtcPeerStaleAfterSec { get; init; }

    /// <summary>
    /// Gets a value indicating whether transport routing falls back to the default provider after WebRTC transport errors.
    /// </summary>
    public required bool TransportFallbackToDefaultOnWebRtcError { get; init; }

    /// <summary>
    /// Gets the local agent identifier.
    /// </summary>
    public required string AgentId { get; init; }

    /// <summary>
    /// Gets the local endpoint identifier.
    /// </summary>
    public required string EndpointId { get; init; }

    /// <summary>
    /// Gets the maintenance policy currently applied by the session maintenance services.
    /// </summary>
    public required SessionMaintenanceOptions Maintenance { get; init; }

    /// <summary>
    /// Gets the effective policy bootstrap configuration.
    /// </summary>
    public required PolicyBootstrapOptions PolicyBootstrap { get; init; }

    /// <summary>
    /// Gets the active bearer authentication configuration.
    /// </summary>
    public required ApiAuthenticationOptions Authentication { get; init; }

    /// <summary>
    /// Gets the active policy revision lifecycle configuration.
    /// </summary>
    public required PolicyRevisionLifecycleOptions PolicyRevisionLifecycle { get; init; }

    /// <summary>
    /// Gets the active transport SLO diagnostics thresholds.
    /// </summary>
    public required TransportSloDiagnosticsOptions TransportSlo { get; init; }
}
