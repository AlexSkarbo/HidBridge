namespace HidBridge.SessionOrchestrator;

/// <summary>
/// Defines lease, recovery, and retention settings for session maintenance workflows.
/// </summary>
public sealed class SessionMaintenanceOptions
{
    /// <summary>
    /// Gets or sets the active session lease duration.
    /// </summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets the active controller lease duration.
    /// </summary>
    public TimeSpan ControlLeaseDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets the minimum active-controller lease duration accepted from request payloads.
    /// </summary>
    public TimeSpan MinControlLeaseDuration { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets or sets the maximum active-controller lease duration accepted from request payloads.
    /// </summary>
    public TimeSpan MaxControlLeaseDuration { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Gets or sets the grace period allowed while a session stays in recovering state.
    /// </summary>
    public TimeSpan RecoveryGracePeriod { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Gets or sets the cadence of the background maintenance loop.
    /// </summary>
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets the audit retention period.
    /// </summary>
    public TimeSpan AuditRetention { get; set; } = TimeSpan.FromDays(30);

    /// <summary>
    /// Gets or sets the telemetry retention period.
    /// </summary>
    public TimeSpan TelemetryRetention { get; set; } = TimeSpan.FromDays(7);
}
