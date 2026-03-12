namespace HidBridge.ControlPlane.Api;

/// <summary>
/// Runs background retention maintenance for policy revision snapshots.
/// </summary>
public sealed class PolicyRevisionMaintenanceHostedService : BackgroundService
{
    private readonly PolicyRevisionLifecycleService _service;
    private readonly PolicyRevisionLifecycleOptions _options;

    /// <summary>
    /// Creates the background maintenance service.
    /// </summary>
    public PolicyRevisionMaintenanceHostedService(
        PolicyRevisionLifecycleService service,
        PolicyRevisionLifecycleOptions options)
    {
        _service = service;
        _options = options;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _service.TrimAsync(stoppingToken);
        using var timer = new PeriodicTimer(_options.MaintenanceInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await _service.TrimAsync(stoppingToken);
        }
    }
}
