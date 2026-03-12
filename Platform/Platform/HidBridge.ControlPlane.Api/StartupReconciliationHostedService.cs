using HidBridge.SessionOrchestrator;
using Microsoft.Extensions.Hosting;

namespace HidBridge.ControlPlane.Api;

/// <summary>
/// Performs one startup reconciliation pass before the periodic maintenance loop settles in.
/// </summary>
public sealed class StartupReconciliationHostedService : IHostedService
{
    private readonly SessionMaintenanceService _maintenanceService;

    /// <summary>
    /// Initializes the startup reconciliation service.
    /// </summary>
    /// <param name="maintenanceService">The maintenance service.</param>
    public StartupReconciliationHostedService(SessionMaintenanceService maintenanceService)
    {
        _maintenanceService = maintenanceService;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _maintenanceService.ReconcileAsync(cancellationToken);
        await _maintenanceService.TrimRetentionAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
