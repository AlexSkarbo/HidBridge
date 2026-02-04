using System.IO;
using System.Text.Json;

namespace HidControlServer.Services;

/// <summary>
/// Loads and persists cached device state.
/// </summary>
internal static class DeviceCacheService
{
    /// <summary>
    /// Loads device cache into application state.
    /// </summary>
    /// <param name="opt">Options.</param>
    /// <param name="state">Application state.</param>
    public static void LoadDevicesCache(Options opt, AppState state)
    {
        if (!File.Exists(opt.DevicesCachePath)) return;
        if (opt.DevicesCacheMaxAgeSeconds <= 0) return;
        DateTime lastWrite = File.GetLastWriteTimeUtc(opt.DevicesCachePath);
        if ((DateTime.UtcNow - lastWrite).TotalSeconds > opt.DevicesCacheMaxAgeSeconds)
        {
            return;
        }
        try
        {
            string json = File.ReadAllText(opt.DevicesCachePath);
            state.CachedDevicesDetailDoc?.Dispose();
            state.CachedDevicesDetailDoc = JsonDocument.Parse(json);
            state.CachedDevicesAt = lastWrite;
        }
        catch
        {
            // ignore cache load errors
        }
    }

    /// <summary>
    /// Saves the latest devices cache snapshot.
    /// </summary>
    /// <param name="opt">Options.</param>
    /// <param name="mapped">Mapped device payload.</param>
    /// <param name="state">Application state.</param>
    public static void SaveDevicesCache(Options opt, object mapped, AppState state)
    {
        string json = JsonSerializer.Serialize(mapped);
        state.CachedDevicesDetailDoc?.Dispose();
        state.CachedDevicesDetailDoc = JsonDocument.Parse(json);
        state.CachedDevicesAt = DateTimeOffset.UtcNow;
        try
        {
            File.WriteAllText(opt.DevicesCachePath, json);
        }
        catch
        {
            // ignore cache write errors
        }
    }
}
