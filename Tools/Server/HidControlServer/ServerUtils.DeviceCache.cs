using HidControlServer.Services;

namespace HidControlServer;

// Device cache persistence for fast startup and UI population.
/// <summary>
/// Provides server utility helpers for devicecache.
/// </summary>
internal static partial class ServerUtils
{
    /// <summary>
    /// Saves devices cache.
    /// </summary>
    /// <param name="opt">The opt.</param>
    /// <param name="mapped">The mapped.</param>
    /// <param name="state">The state.</param>
    public static void SaveDevicesCache(Options opt, object mapped, AppState state)
    {
        DeviceCacheService.SaveDevicesCache(opt, mapped, state);
    }

    /// <summary>
    /// Loads devices cache.
    /// </summary>
    /// <param name="opt">The opt.</param>
    /// <param name="state">The state.</param>
    public static void LoadDevicesCache(Options opt, AppState state)
    {
        DeviceCacheService.LoadDevicesCache(opt, state);
    }
}
