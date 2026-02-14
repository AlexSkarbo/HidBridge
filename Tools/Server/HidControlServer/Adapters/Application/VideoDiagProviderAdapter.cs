using System.Management;
using HidControl.Application.Abstractions;
using HidControlServer.Services;
using HidControl.UseCases.Video;

namespace HidControlServer.Adapters.Application;

/// <summary>
/// Builds video diagnostics payload from runtime state.
/// </summary>
public sealed class VideoDiagProviderAdapter : IVideoDiagProvider
{
    private readonly Options _options;
    private readonly VideoSourceStore _store;
    private readonly VideoRuntimeService _runtime;
    private readonly AppState _appState;

    /// <summary>
    /// Executes VideoDiagProviderAdapter.
    /// </summary>
    /// <param name="options">Options.</param>
    /// <param name="store">Source store.</param>
    /// <param name="runtime">Runtime service.</param>
    /// <param name="appState">Application state.</param>
    public VideoDiagProviderAdapter(Options options, VideoSourceStore store, VideoRuntimeService runtime, AppState appState)
    {
        _options = options;
        _store = store;
        _runtime = runtime;
        _appState = appState;
    }

    /// <summary>
    /// Builds diagnostics payload.
    /// </summary>
    /// <returns>Diagnostics payload.</returns>
    public object Get()
    {
        string os = OperatingSystem.IsWindows() ? "windows" :
            OperatingSystem.IsLinux() ? "linux" :
            OperatingSystem.IsMacOS() ? "macos" : "unknown";
        bool lockFree = _appState.VideoCaptureLock.Wait(0);
        bool lockHeld = !lockFree;
        if (lockFree)
        {
            _appState.VideoCaptureLock.Release();
        }

        List<object>? wmiDevices = null;
        string? wmiError = null;
        IReadOnlyList<VideoDshowDevice>? dshowDevices = null;
        string? dshowError = null;
        if (OperatingSystem.IsWindows())
        {
            try
            {
                wmiDevices = new List<object>();
                using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_PnPEntity WHERE PNPClass='Image' OR PNPClass='Camera' OR Service='usbvideo'");
                foreach (ManagementObject obj in searcher.Get())
                {
                    string? name = obj["Name"] as string;
                    string? deviceId = obj["DeviceID"] as string;
                    string? pnpDeviceId = obj["PNPDeviceID"] as string;
                    string? classGuid = obj["ClassGuid"] as string;
                    string? service = obj["Service"] as string;
                    string? status = obj["Status"] as string;
                    int? errorCode = obj["ConfigManagerErrorCode"] as int?;
                    wmiDevices.Add(new
                    {
                        name,
                        deviceId,
                        pnpDeviceId,
                        classGuid,
                        service,
                        status,
                        errorCode,
                        url = deviceId is null ? null : "win:" + deviceId
                    });
                }
            }
            catch (Exception ex)
            {
                wmiError = ex.Message;
            }

            try
            {
                dshowDevices = VideoModeService.ListDshowDevices(_options.FfmpegPath);
            }
            catch (Exception ex)
            {
                dshowError = ex.Message;
            }
        }

        var ffmpegProcs = _appState.FfmpegProcesses.ToArray().Select(kvp => new
        {
            id = kvp.Key,
            pid = kvp.Value.Id,
            status = kvp.Value.HasExited ? "exited" : "running",
            exitCode = kvp.Value.HasExited ? kvp.Value.ExitCode : (int?)null
        }).ToList();
        var ffmpegStates = _appState.FfmpegStates.ToArray().Select(kvp => new
        {
            id = kvp.Key,
            lastStartAt = kvp.Value.LastStartAt,
            lastExitAt = kvp.Value.LastExitAt,
            lastExitCode = kvp.Value.LastExitCode,
            restartCount = kvp.Value.RestartCount,
            logPath = kvp.Value.LogPath,
            args = kvp.Value.Args,
            captureLockHeld = kvp.Value.CaptureLockHeld,
            captureLockAt = kvp.Value.CaptureLockAt
        }).ToList();

        var sourcesList = _store.GetAll();
        string? lockOwnerSourceId = null;
        string? lockOwnerType = null;
        int? lockOwnerPid = null;
        string? lockOwnerArgs = null;
        string? lockOwnerInput = null;
        foreach (var src in sourcesList)
        {
            _appState.VideoCaptureWorkers.TryGetValue(src.Id, out var worker);
            bool workerRunning = worker is not null && worker.IsRunning;
            if (workerRunning && !worker!.CurrentInputIsRtsp)
            {
                lockOwnerSourceId = src.Id;
                lockOwnerType = "worker";
                lockOwnerInput = worker.CurrentInput;
                break;
            }
            _appState.FfmpegProcesses.TryGetValue(src.Id, out var proc);
            bool procRunning = proc is not null && !proc.HasExited;
            string? ffmpegInput = VideoInputService.BuildFfmpegInput(src);
            bool ffmpegIsRtsp = ffmpegInput is not null &&
                ffmpegInput.Contains("rtsp://", StringComparison.OrdinalIgnoreCase);
            if (procRunning && !ffmpegIsRtsp)
            {
                lockOwnerSourceId = src.Id;
                lockOwnerType = "ffmpeg";
                lockOwnerPid = proc!.Id;
                if (_appState.FfmpegStates.TryGetValue(src.Id, out var st))
                {
                    lockOwnerArgs = st.Args;
                }
                break;
            }
        }
        var sources = sourcesList.Select(src =>
        {
            _appState.VideoCaptureWorkers.TryGetValue(src.Id, out var worker);
            _appState.FfmpegProcesses.TryGetValue(src.Id, out var proc);
            bool procRunning = proc is not null && !proc.HasExited;
            _appState.FlvStreamHubs.TryGetValue(src.Id, out var flvHub);
            var flvStats = flvHub?.GetStats();
            DateTimeOffset? flvLastTagAt = flvStats?.lastTagAtUtc;
            double? flvStalledMs = null;
            bool flvStalled = false;
            if (procRunning && flvLastTagAt.HasValue && flvLastTagAt.Value != default)
            {
                flvStalledMs = (DateTimeOffset.UtcNow - flvLastTagAt.Value).TotalMilliseconds;
                flvStalled = flvStalledMs > 4000;
            }
            if (flvStalled)
            {
                var last = _appState.FlvStallLogAt.GetOrAdd(src.Id, _ => DateTimeOffset.MinValue);
                if (DateTimeOffset.UtcNow - last > TimeSpan.FromSeconds(30))
                {
                    _appState.FlvStallLogAt[src.Id] = DateTimeOffset.UtcNow;
                    ServerEventLog.Log("flv", "stalled", new
                    {
                        id = src.Id,
                        stalledMs = flvStalledMs,
                        flvStats?.hasHeader,
                        flvStats?.hasVideoConfig,
                        flvStats?.hasKeyframe,
                        flvStats?.tagsIn,
                        flvStats?.tagsPublished,
                        flvStats?.lastTagAtUtc
                    });
                }
            }
            bool manualStop = _runtime.IsManualStop(src.Id);
            bool workerRunning = worker is not null && worker.IsRunning;
            DateTimeOffset? frameAt = worker?.LatestFrameAt;
            double? frameAgeMs = frameAt is null ? null : (DateTimeOffset.UtcNow - frameAt.Value).TotalMilliseconds;
            string? ffmpegInput = VideoInputService.BuildFfmpegInput(src);
            bool inputOsMismatch = string.IsNullOrWhiteSpace(ffmpegInput);
            bool ffmpegIsRtsp = ffmpegInput is not null &&
                ffmpegInput.Contains("rtsp://", StringComparison.OrdinalIgnoreCase);
            string hlsIndexPath = Path.Combine(_options.VideoHlsDir, src.Id, "index.m3u8");
            bool hlsIndexExists = File.Exists(hlsIndexPath);
            DateTimeOffset? hlsIndexLastWrite = null;
            long? hlsIndexSize = null;
            if (hlsIndexExists)
            {
                var info = new FileInfo(hlsIndexPath);
                hlsIndexLastWrite = info.LastWriteTimeUtc;
                hlsIndexSize = info.Length;
            }
            string? hlsDir = Path.GetDirectoryName(hlsIndexPath);
            bool hlsDirExists = hlsDir is not null && Directory.Exists(hlsDir);
            int hlsSegmentCount = 0;
            DateTimeOffset? hlsSegmentLastWrite = null;
            string? hlsSegmentLatest = null;
            if (hlsDirExists && hlsDir is not null)
            {
                var segs = Directory.EnumerateFiles(hlsDir, "seg_*.m4s")
                    .Concat(Directory.EnumerateFiles(hlsDir, "seg_*.ts"))
                    .Select(p => new FileInfo(p))
                    .OrderByDescending(fi => fi.LastWriteTimeUtc)
                    .ToList();
                hlsSegmentCount = segs.Count;
                if (segs.Count > 0)
                {
                    hlsSegmentLatest = segs[0].Name;
                    hlsSegmentLastWrite = segs[0].LastWriteTimeUtc;
                }
            }
            string? conflictReason = null;
            string? conflictHolderType = null;
            string? conflictHolderSourceId = null;
            int? conflictHolderPid = null;
            string? conflictHolderArgs = null;
            string? conflictHolderInput = null;
            if (inputOsMismatch)
            {
                conflictReason = "os_mismatch";
            }
            else if (workerRunning && worker is not null && !worker.CurrentInputIsRtsp)
            {
                conflictReason = "worker_running";
                conflictHolderType = "worker";
                conflictHolderSourceId = src.Id;
                conflictHolderInput = worker.CurrentInput;
            }
            else if (procRunning && !ffmpegIsRtsp)
            {
                conflictReason = "ffmpeg_running_device";
                conflictHolderType = "ffmpeg";
                conflictHolderSourceId = src.Id;
                conflictHolderPid = proc?.Id;
                if (_appState.FfmpegStates.TryGetValue(src.Id, out var st))
                {
                    conflictHolderArgs = st.Args;
                }
            }
            else if (lockHeld && !ffmpegIsRtsp)
            {
                conflictReason = "capture_busy";
                conflictHolderType = lockOwnerType;
                conflictHolderSourceId = lockOwnerSourceId;
                conflictHolderPid = lockOwnerPid;
                conflictHolderArgs = lockOwnerArgs;
                conflictHolderInput = lockOwnerInput;
            }
            string status = "idle";
            string? statusDetail = null;
            if (inputOsMismatch)
            {
                status = "os_mismatch";
            }
            else if (!string.IsNullOrWhiteSpace(worker?.LastError))
            {
                status = "error";
                statusDetail = worker?.LastError;
            }
            else if (procRunning || workerRunning)
            {
                status = "running";
                if (procRunning && !hlsIndexExists)
                {
                    status = "running_no_hls";
                    statusDetail = "hls_not_writing";
                }
            }
            else if (conflictReason is not null)
            {
                status = "conflict";
                statusDetail = conflictReason;
            }
            return new
            {
                id = src.Id,
                name = src.Name,
                kind = src.Kind,
                url = src.Url,
                enabled = src.Enabled,
                ffmpegInputOverride = src.FfmpegInputOverride,
                ffmpegInput,
                hlsIndexPath,
                hlsIndexExists,
                hlsIndexLastWrite,
                hlsIndexSize,
                hlsDir,
                hlsDirExists,
                hlsSegmentCount,
                hlsSegmentLatest,
                hlsSegmentLastWrite,
                inputOsMismatch,
                manualStop,
                status,
                statusDetail,
                conflictReason,
                conflictHolder = conflictHolderType is null ? null : new
                {
                    type = conflictHolderType,
                    sourceId = conflictHolderSourceId,
                    pid = conflictHolderPid,
                    args = conflictHolderArgs,
                    input = conflictHolderInput
                },
                workerRunning,
                workerLastError = worker?.LastError,
                workerLatestFrameAt = frameAt,
                workerLatestFrameAgeMs = frameAgeMs,
                workerInput = worker?.CurrentInput,
                workerInputIsRtsp = worker?.CurrentInputIsRtsp,
                ffmpegRunning = procRunning,
                flvHub = flvStats,
                flvStalled,
                flvStalledMs
            };
        }).ToList();
        var orphanFfmpeg = FfmpegProcessManager.GetFfmpegOrphanInfos(_options, sourcesList, _appState.FfmpegProcesses);

        return new
        {
            ok = true,
            os,
            ffmpegPath = _options.FfmpegPath,
            captureLockHeld = lockHeld,
            videoKillOrphanFfmpeg = _options.VideoKillOrphanFfmpeg,
            videoSecondaryCaptureEnabled = _options.VideoSecondaryCaptureEnabled,
            videoHlsDir = _options.VideoHlsDir,
            videoLogDir = _options.VideoLogDir,
            ffmpegProcesses = ffmpegProcs,
            ffmpegStates,
            orphanFfmpeg,
            wmiDevices,
            wmiError,
            dshowDevices,
            dshowError,
            sources
        };
    }
}
