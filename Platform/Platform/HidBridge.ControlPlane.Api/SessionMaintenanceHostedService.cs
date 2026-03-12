using HidBridge.SessionOrchestrator;
using Microsoft.Extensions.Hosting;

namespace HidBridge.ControlPlane.Api;

/// <summary>
/// Runs periodic reconciliation and retention cleanup for persisted sessions and event streams.
/// </summary>
public sealed class SessionMaintenanceHostedService : BackgroundService
{
    private readonly SessionMaintenanceService _maintenanceService;
    private readonly SessionMaintenanceOptions _options;

    /// <summary>
    /// Initializes the background maintenance loop.
    /// </summary>
    /// <param name="maintenanceService">The maintenance service.</param>
    /// <param name="options">The maintenance configuration.</param>
    public SessionMaintenanceHostedService(SessionMaintenanceService maintenanceService, SessionMaintenanceOptions options)
    {
        _maintenanceService = maintenanceService;
        _options = options;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_options.CleanupInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await _maintenanceService.ReconcileAsync(stoppingToken);
            await _maintenanceService.TrimRetentionAsync(stoppingToken);
        }
    }
}
