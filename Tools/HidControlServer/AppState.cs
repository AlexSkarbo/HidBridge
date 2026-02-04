using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace HidControlServer;

/// <summary>
/// Holds application state.
/// </summary>
public sealed class AppState
{
    /// <summary>
    /// Holds application state.
    /// </summary>
    /// <param name="CachedAt">CachedAt.</param>
    /// <param name="DeviceName">DeviceName.</param>
    /// <param name="Modes">Modes.</param>
    /// <param name="SupportsMjpeg">SupportsMjpeg.</param>
    internal sealed record VideoModesCacheEntry(DateTimeOffset CachedAt, string DeviceName, IReadOnlyList<HidControl.Contracts.VideoMode> Modes, bool SupportsMjpeg);

    public ConcurrentDictionary<string, Process> FfmpegProcesses { get; } = new();
    public ConcurrentDictionary<string, FfmpegProcState> FfmpegStates { get; } = new();
    public ConcurrentDictionary<string, VideoCaptureWorker> VideoCaptureWorkers { get; } = new();
    public ConcurrentDictionary<string, VideoFrameHub> VideoFrameHubs { get; } = new();
    public ConcurrentDictionary<string, FlvStreamHub> FlvStreamHubs { get; } = new();
    public ConcurrentDictionary<string, DateTimeOffset> FlvStallLogAt { get; } = new();
    // When true for a source, automatic ffmpeg starts are blocked until a manual start.
    public ConcurrentDictionary<string, bool> VideoManualStop { get; } = new();
    public SemaphoreSlim VideoCaptureLock { get; } = new(1, 1);
    internal ConcurrentDictionary<string, VideoModesCacheEntry> VideoModesCache { get; } = new();
    public bool VideoOutputHlsEnabled { get; set; } = true;
    public bool VideoOutputMjpegEnabled { get; set; } = true;
    public bool VideoOutputFlvEnabled { get; set; }
    public bool VideoOutputMjpegPassthrough { get; set; }
    public int VideoOutputMjpegFps { get; set; } = 30;
    public string VideoOutputMjpegSize { get; set; } = string.Empty;
    public JsonDocument? CachedDevicesDetailDoc { get; set; }
    public DateTimeOffset? CachedDevicesAt { get; set; }
    public string SerialConfigured { get; init; } = string.Empty;
    public string SerialResolved { get; set; } = string.Empty;
    public bool SerialAutoEnabled { get; init; }
    public string SerialAutoStatus { get; set; } = string.Empty;
    public string? SerialMatch { get; init; }
    public string? ConfigPath { get; set; }

    /// <summary>
    /// Checks whether manual stop.
    /// </summary>
    /// <param name="id">The id.</param>
    /// <returns>Result.</returns>
    public bool IsManualStop(string id) =>
        VideoManualStop.TryGetValue(id, out var stopped) && stopped;

    /// <summary>
    /// Sets manual stop.
    /// </summary>
    /// <param name="id">The id.</param>
    /// <param name="stopped">The stopped.</param>
    public void SetManualStop(string id, bool stopped) =>
        VideoManualStop[id] = stopped;
}
