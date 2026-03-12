namespace HidBridge.ControlPlane.Api;

/// <summary>
/// Defines retention and maintenance settings for policy revision snapshots.
/// </summary>
public sealed class PolicyRevisionLifecycleOptions
{
    /// <summary>
    /// Gets or sets the cadence of the maintenance loop.
    /// </summary>
    public TimeSpan MaintenanceInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Gets or sets how long policy revisions are retained before becoming prune candidates.
    /// </summary>
    public TimeSpan Retention { get; set; } = TimeSpan.FromDays(90);

    /// <summary>
    /// Gets or sets the minimum number of revisions retained per logical entity.
    /// </summary>
    public int MaxRevisionsPerEntity { get; set; } = 20;
}
