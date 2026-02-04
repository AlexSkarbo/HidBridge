using System.Diagnostics;
using System.Linq;
using System.Threading.Channels;
using HidControlServer.Services;

namespace HidControlServer;

/// <summary>
/// Runs the FFmpeg capture loop for a video source and exposes the latest frame.
/// </summary>
public sealed class VideoCaptureWorker : IAsyncDisposable
{
    private readonly object _lock = new();
    private readonly Options _opt;
    private readonly VideoSourceConfig _source;
    private readonly VideoSourceStore _store;
    private readonly AppState _appState;
    private readonly HidControl.UseCases.Video.VideoOutputService _outputService;
    private readonly HidControl.UseCases.Video.VideoFfmpegService _ffmpegService;
    private readonly List<Channel<byte[]>> _subscribers = new();
    private Process? _proc;
    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private bool _isRunning;
    private int? _fps;
    private int? _width;
    private int? _height;
    private int? _quality;
    private bool _passthrough;

    /// <summary>
    /// Creates a capture worker bound to a source.
    /// </summary>
    /// <param name="opt">The opt.</param>
    /// <param name="source">The source.</param>
    /// <param name="store">The store.</param>
    /// <param name="appState">The appState.</param>
    /// <param name="outputService">The outputService.</param>
    /// <param name="ffmpegService">The ffmpegService.</param>
    public VideoCaptureWorker(Options opt, VideoSourceConfig source, VideoSourceStore store, AppState appState, HidControl.UseCases.Video.VideoOutputService outputService, HidControl.UseCases.Video.VideoFfmpegService ffmpegService)
    {
        _opt = opt;
        _source = source;
        _store = store;
        _appState = appState;
        _outputService = outputService;
        _ffmpegService = ffmpegService;
    }

    public byte[]? LatestFrame { get; private set; }
    public DateTimeOffset? LatestFrameAt { get; private set; }
    public string? LastError { get; private set; }
    public bool IsRunning => _isRunning;
    public string? CurrentInput { get; private set; }
    public bool CurrentInputIsRtsp { get; private set; }

    /// <summary>
    /// Starts the capture loop with optional overrides for MJPEG encoding.
    /// </summary>
    /// <param name="fps">The fps.</param>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    /// <param name="quality">The quality.</param>
    /// <param name="passthrough">The passthrough.</param>
    /// <param name="ct">The ct.</param>
    /// <returns>Result.</returns>
    public async Task StartAsync(int? fps, int? width, int? height, int? quality, bool? passthrough, CancellationToken ct)
    {
        lock (_lock)
        {
            if (_isRunning)
            {
                return;
            }
            _fps = fps;
            _width = width;
            _height = height;
            _quality = quality;
            _passthrough = passthrough == true;
            _isRunning = true;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _runTask = Task.Run(() => RunAsync(_cts.Token));
        }
        await Task.CompletedTask;
    }

    /// <summary>
    /// Subscribes to JPEG frame updates (drop-oldest channel).
    /// </summary>
    /// <param name="ct">The ct.</param>
    /// <returns>Result.</returns>
    public ChannelReader<byte[]> Subscribe(CancellationToken ct)
    {
        var channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(1)
        {
            SingleWriter = true,
            SingleReader = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });
        lock (_lock)
        {
            _subscribers.Add(channel);
            if (LatestFrame is not null)
            {
                channel.Writer.TryWrite(LatestFrame);
            }
        }
        ct.Register(() =>
        {
            lock (_lock)
            {
                _subscribers.Remove(channel);
                channel.Writer.TryComplete();
            }
        });
        return channel.Reader;
    }

    /// <summary>
    /// Waits for the next available frame or returns the latest one on timeout.
    /// </summary>
    /// <param name="timeout">The timeout.</param>
    /// <param name="ct">The ct.</param>
    /// <returns>Result.</returns>
    public async Task<byte[]?> WaitForFrameAsync(TimeSpan timeout, CancellationToken ct)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        while (!ct.IsCancellationRequested && DateTimeOffset.UtcNow < deadline)
        {
            byte[]? frame = LatestFrame;
            if (frame is not null)
            {
                return frame;
            }
            await Task.Delay(50, ct);
        }
        return LatestFrame;
    }

    /// <summary>
    /// Executes RunAsync.
    /// </summary>
    /// <param name="ct">The ct.</param>
    /// <returns>Result.</returns>
    private async Task RunAsync(CancellationToken ct)
    {
        if (!MjpegCaptureService.TryBuildCaptureArgs(
            _opt,
            _source,
            _appState,
            _fps,
            _width,
            _height,
            _quality,
            _passthrough,
            out string? input,
            out string args,
            out bool needsLock,
            out string? buildError))
        {
            LastError = buildError ?? "no input";
            _isRunning = false;
            return;
        }
        CurrentInput = input;
        CurrentInputIsRtsp = input!.Contains("rtsp://", StringComparison.OrdinalIgnoreCase);
        if (!ExecutableService.TryValidateExecutablePath(_opt.FfmpegPath, out string ffmpegError))
        {
            LastError = $"ffmpeg {ffmpegError}";
            _isRunning = false;
            return;
        }
        List<string> stopped = new();
        if (needsLock)
        {
            int lockTimeout = Math.Clamp(_opt.VideoCaptureLockTimeoutMs, 100, 30_000);
            bool locked = await _appState.VideoCaptureLock.WaitAsync(lockTimeout, ct);
            if (!locked)
            {
                LastError = "capture busy";
                _isRunning = false;
                return;
            }
        }
        try
        {
            if (_opt.VideoCaptureAutoStopFfmpeg && needsLock)
            {
                ServerEventLog.Log(
                    "ffmpeg",
                    "auto_stop_capture_lock",
                    new
                    {
                        source = _source.Id,
                        needsLock,
                        autoStop = _opt.VideoCaptureAutoStopFfmpeg,
                        reason = "mjpeg_capture_start"
                    });
                stopped = FfmpegProcessManager.StopFfmpegProcesses(_appState);
                if (stopped.Count > 0)
                {
                    ServerEventLog.Log("ffmpeg", "auto_stop_capture_lock:stopped", new { stopped, source = _source.Id });
                }
            }

            var psi = new ProcessStartInfo
            {
                FileName = _opt.FfmpegPath,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            _proc = Process.Start(psi);
            if (_proc is null)
            {
                LastError = "ffmpeg start failed";
                _isRunning = false;
                return;
            }

            Task<string> stderrTask = _proc.StandardError.ReadToEndAsync();
            var stream = _proc.StandardOutput.BaseStream;
            int maxFrameBytes = Math.Clamp(_opt.VideoMjpegMaxFrameBytes, 256 * 1024, 16 * 1024 * 1024);
            await MjpegCaptureService.PumpMjpegFramesAsync(_opt, stream, jpeg =>
            {
                if (jpeg.Length > maxFrameBytes)
                {
                    return;
                }
                LatestFrame = jpeg;
                LatestFrameAt = DateTimeOffset.UtcNow;
                Broadcast(jpeg);
            }, ct);

            string stderr = string.Empty;
            try { stderr = await stderrTask; } catch { }
            if (!string.IsNullOrWhiteSpace(stderr))
            {
                LastError = stderr;
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
        finally
        {
            try { if (_proc is not null && !_proc.HasExited) _proc.Kill(true); } catch { }
            _proc = null;
            if (_opt.VideoCaptureAutoStopFfmpeg && needsLock && stopped.Count > 0)
            {
                var restartSources = _store.GetAll()
                    .Where(s => s.Enabled && stopped.Contains(s.Id) && !_appState.IsManualStop(s.Id))
                    .ToList();
                var outputState = _outputService.Get();
                ServerEventLog.Log(
                    "ffmpeg",
                    "auto_stop_capture_lock:restart",
                    new
                    {
                        source = _source.Id,
                        restartCount = restartSources.Count,
                        stopped,
                        outputMode = HidControl.UseCases.Video.VideoOutputService.ResolveMode(outputState)
                    });
                _ffmpegService.StartForSources(restartSources, outputState, restart: true, force: true);
            }
            if (needsLock)
            {
                _appState.VideoCaptureLock.Release();
            }
            _isRunning = false;
        }
    }

    /// <summary>
    /// Executes Broadcast.
    /// </summary>
    /// <param name="jpeg">The jpeg.</param>
    private void Broadcast(byte[] jpeg)
    {
        Channel<byte[]>[] subscribers;
        lock (_lock)
        {
            subscribers = _subscribers.ToArray();
        }
        foreach (Channel<byte[]> sub in subscribers)
        {
            sub.Writer.TryWrite(jpeg);
        }
    }

    /// <summary>
    /// Stops capture and releases FFmpeg process resources.
    /// </summary>
    /// <returns>Result.</returns>
    public async ValueTask DisposeAsync()
    {
        try
        {
            _cts?.Cancel();
        }
        catch { }
        if (_runTask is not null)
        {
            try { await _runTask; } catch { }
        }
        try { if (_proc is not null && !_proc.HasExited) _proc.Kill(true); } catch { }
        _cts?.Dispose();
    }
}
