using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using HidControlServer;

namespace HidControlServer.Services;

/// <summary>
/// Diagnostics and background maintenance helpers.
/// </summary>
internal static class DeviceDiagnostics
{
    /// <summary>
    /// Checks whether an exception looks like an injection failure.
    /// </summary>
    /// <param name="ex">Exception.</param>
    /// <returns>True if injection failed.</returns>
    public static bool IsInjectFailed(Exception ex)
    {
        return ex is TimeoutException ||
               ex is OperationCanceledException ||
               ex is TaskCanceledException ||
               ex.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
               ex.Message.Contains("inject_failed", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Tries to resolve the repository version from assembly metadata.
    /// </summary>
    /// <returns>Version string, if available.</returns>
    public static string? FindRepoVersion()
    {
        try
        {
            var attr = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            return attr?.InformationalVersion;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Background loop to refresh interface list when auto-refresh is enabled.
    /// </summary>
    /// <param name="opt">Options.</param>
    /// <param name="uart">UART client.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task AutoRefreshDevicesLoop(Options opt, HidUartClient uart, CancellationToken ct)
    {
        if (opt.DevicesAutoRefreshMs <= 0) return;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await uart.RequestInterfaceListAsync(opt.DevicesAutoRefreshMs, ct);
            }
            catch { }
            try
            {
                await Task.Delay(opt.DevicesAutoRefreshMs, ct);
            }
            catch { }
        }
    }
}
