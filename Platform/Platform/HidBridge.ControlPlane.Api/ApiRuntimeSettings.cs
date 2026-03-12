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
}
