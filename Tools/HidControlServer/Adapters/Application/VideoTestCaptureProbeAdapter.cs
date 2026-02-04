using HidControl.Application.Abstractions;
using HidControl.Application.Models;
using HidControl.Contracts;
using HidControl.UseCases;
using HidControl.UseCases.Video;
using HidControlServer;
using HidControlServer.Services;
using System.Diagnostics;
using System.Linq;

namespace HidControlServer.Adapters.Application;

/// <summary>
/// Server-side implementation of <see cref="IVideoTestCaptureProbe"/> using FFmpeg.
/// </summary>
public sealed class VideoTestCaptureProbeAdapter : IVideoTestCaptureProbe
{
    private readonly Options _opt;
    private readonly VideoSourceStore _store;
    private readonly VideoRuntimeService _runtime;
    private readonly VideoOutputService _output;
    private readonly VideoFfmpegService _ffmpeg;
    private readonly AppState _appState;

    /// <summary>
    /// Creates a new instance.
    /// </summary>
    public VideoTestCaptureProbeAdapter(
        Options opt,
        VideoSourceStore store,
        VideoRuntimeService runtime,
        VideoOutputService output,
        VideoFfmpegService ffmpeg,
        AppState appState)
    {
        _opt = opt;
        _store = store;
        _runtime = runtime;
        _output = output;
        _ffmpeg = ffmpeg;
        _appState = appState;
    }

    /// <inheritdoc />
    public async Task<VideoTestCaptureResult> RunAsync(string? id, int? timeoutSec, CancellationToken ct)
    {
        var sources = _store.GetAll();
        VideoSourceConfig? src = null;
        if (!string.IsNullOrWhiteSpace(id))
        {
            src = sources.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            src = sources.FirstOrDefault(s => s.Enabled && string.Equals(s.Kind, "lavfi", StringComparison.OrdinalIgnoreCase))
                ?? sources.FirstOrDefault(s => s.Enabled && string.Equals(s.Kind, "test", StringComparison.OrdinalIgnoreCase))
                ?? sources.FirstOrDefault(s => s.Enabled);
        }
        if (src is null)
        {
            return new VideoTestCaptureResult(false, null, null, null, null, null, null, null, "source_not_found");
        }

        if (_appState.VideoCaptureWorkers.TryGetValue(src.Id, out var runningWorker) &&
            runningWorker is not null &&
            runningWorker.IsRunning)
        {
            return new VideoTestCaptureResult(true, src.Id, "worker_running", runningWorker.LatestFrameAt, null, null, null, null, null);
        }

        if (_appState.FfmpegProcesses.TryGetValue(src.Id, out var runningProc) &&
            runningProc is not null &&
            !runningProc.HasExited)
        {
            return new VideoTestCaptureResult(true, src.Id, "ffmpeg_running", null, runningProc.Id, null, null, null, null);
        }

        string? input = VideoInputService.BuildFfmpegInput(src);
        if (string.IsNullOrWhiteSpace(input))
        {
            string raw = src.FfmpegInputOverride ?? src.Url ?? string.Empty;
            if (OperatingSystem.IsWindows() && raw.Contains("/dev/video", StringComparison.OrdinalIgnoreCase))
            {
                return new VideoTestCaptureResult(false, src.Id, null, null, null, null, raw, null, "linux_source_on_windows");
            }
            if (OperatingSystem.IsLinux() && raw.Contains("win:", StringComparison.OrdinalIgnoreCase))
            {
                return new VideoTestCaptureResult(false, src.Id, null, null, null, null, raw, null, "windows_source_on_linux");
            }
            if (OperatingSystem.IsMacOS() && raw.Contains("win:", StringComparison.OrdinalIgnoreCase))
            {
                return new VideoTestCaptureResult(false, src.Id, null, null, null, null, raw, null, "windows_source_on_macos");
            }
            return new VideoTestCaptureResult(false, src.Id, null, null, null, null, null, null, "no_input");
        }

        if (!ExecutableService.TryValidateExecutablePath(_opt.FfmpegPath, out string ffmpegError))
        {
            return new VideoTestCaptureResult(false, src.Id, null, null, null, null, null, null, $"ffmpeg {ffmpegError}");
        }

        if (OperatingSystem.IsWindows() && input.Contains("v4l2", StringComparison.OrdinalIgnoreCase))
        {
            return new VideoTestCaptureResult(false, src.Id, null, null, null, null, input, null, "linux_source_on_windows");
        }
        if (OperatingSystem.IsLinux() && input.Contains("dshow", StringComparison.OrdinalIgnoreCase))
        {
            return new VideoTestCaptureResult(false, src.Id, null, null, null, null, input, null, "windows_source_on_linux");
        }
        if (OperatingSystem.IsMacOS() && input.Contains("dshow", StringComparison.OrdinalIgnoreCase))
        {
            return new VideoTestCaptureResult(false, src.Id, null, null, null, null, input, null, "windows_source_on_macos");
        }

        int timeout = Math.Clamp(timeoutSec ?? 5, 1, 20);
        string args = string.Empty;
        string? altInput = null;
        bool altTried = false;
        if (OperatingSystem.IsWindows() && input.Contains("dshow", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var dshow = VideoModeService.ListDshowDevices(_opt.FfmpegPath);
                var match = dshow.FirstOrDefault(d => string.Equals(d.Name, src.Name, StringComparison.OrdinalIgnoreCase));
                if (match is not null && !string.IsNullOrWhiteSpace(match.AlternativeName))
                {
                    string candidate = $"-f dshow -i video=\"{match.AlternativeName}\"";
                    if (!string.Equals(candidate, input, StringComparison.OrdinalIgnoreCase))
                    {
                        altInput = candidate;
                    }
                }
            }
            catch { }
        }

        int lockTimeout = Math.Clamp(_opt.VideoCaptureLockTimeoutMs, 100, 30_000);
        bool locked;
        try
        {
            locked = await _appState.VideoCaptureLock.WaitAsync(lockTimeout, ct);
        }
        catch (OperationCanceledException)
        {
            return new VideoTestCaptureResult(false, src.Id, null, null, null, null, null, null, "cancelled");
        }
        if (!locked)
        {
            return new VideoTestCaptureResult(false, src.Id, null, null, null, null, null, null, "capture_busy");
        }

        var stopped = new List<string>();
        var stoppedWorkers = new List<string>();
        string stderr = string.Empty;
        int? exitCode = null;
        bool timedOut = false;
        bool ok = false;
        string? error = null;

        try
        {
            if (_opt.VideoKillOrphanFfmpeg)
            {
                _appState.FfmpegProcesses.TryGetValue(src.Id, out var tracked);
                _ = FfmpegProcessManager.KillFfmpegOrphansForSource(_opt, src, tracked);
            }

            int attempts = Math.Max(1, _opt.VideoCaptureRetryCount + 1);
            int delayMs = Math.Clamp(_opt.VideoCaptureRetryDelayMs, 50, 5000);

            if (_opt.VideoCaptureAutoStopFfmpeg)
            {
                stoppedWorkers = (await _runtime.StopCaptureWorkersAsync(src.Id, CancellationToken.None)).ToList();
            }
            if (_opt.VideoCaptureAutoStopFfmpeg)
            {
                ServerEventLog.Log("ffmpeg", "auto_stop_capture_diag", new { source = src.Id, reason = "capture_diag" });
                stopped = _runtime.StopFfmpegProcesses().ToList();
                if (stopped.Count > 0)
                {
                    ServerEventLog.Log("ffmpeg", "auto_stop_capture_diag:stopped", new { source = src.Id, stopped });
                }
            }

            for (int attempt = 0; attempt < attempts; attempt++)
            {
                try
                {
                    string currentInput = (!altTried || string.IsNullOrWhiteSpace(altInput)) ? input : altInput;
                    args = $"{currentInput} -t 1 -f null -";
                    var psi = new ProcessStartInfo
                    {
                        FileName = _opt.FfmpegPath,
                        Arguments = args,
                        RedirectStandardError = true,
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using var proc = Process.Start(psi);
                    if (proc is null)
                    {
                        error = "ffmpeg_start_failed";
                        break;
                    }
                    var readTask = proc.StandardError.ReadToEndAsync();
                    if (!proc.WaitForExit(timeout * 1000))
                    {
                        timedOut = true;
                        try { proc.Kill(true); } catch { }
                    }
                    else
                    {
                        exitCode = proc.ExitCode;
                    }
                    stderr = await readTask;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    break;
                }

                if (timedOut)
                {
                    error = "timeout";
                    break;
                }

                ok = exitCode == 0;
                if (ok)
                {
                    if (!string.IsNullOrWhiteSpace(altInput) && altTried)
                    {
                        src = src with { FfmpegInputOverride = altInput };
                        _store.Upsert(src);
                    }
                    break;
                }
                if (!altTried && !string.IsNullOrWhiteSpace(altInput) && StreamDiagService.IsDeviceBusy(stderr))
                {
                    altTried = true;
                    attempt--;
                    continue;
                }
                if (StreamDiagService.IsDeviceBusy(stderr) && attempt + 1 < attempts)
                {
                    await Task.Delay(delayMs, ct);
                    continue;
                }
                error = exitCode is null ? "failed" : $"exit_{exitCode}";
                break;
            }
        }
        finally
        {
            if (_opt.VideoCaptureAutoStopFfmpeg && stopped.Count > 0)
            {
                var restartSources = _store.GetAll()
                    .Where(s => s.Enabled && stopped.Contains(s.Id) && !_runtime.IsManualStop(s.Id))
                    .ToList();
                var outputState = _output.Get();
                _ffmpeg.StartForSources(restartSources, outputState, restart: true, force: true);
            }
            if (_opt.VideoCaptureAutoStopFfmpeg && stoppedWorkers.Count > 0)
            {
                foreach (string workerId in stoppedWorkers)
                {
                    var source = _store.GetAll().FirstOrDefault(s => string.Equals(s.Id, workerId, StringComparison.OrdinalIgnoreCase));
                    if (source is null) continue;
                    _appState.VideoCaptureWorkers.TryAdd(workerId, new VideoCaptureWorker(_opt, source, _store, _appState, _output, _ffmpeg));
                }
            }
            _appState.VideoCaptureLock.Release();
        }

        string[] lines = stderr.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        string tail = string.Join("\n", lines.Skip(Math.Max(0, lines.Length - 20)));

        return new VideoTestCaptureResult(
            ok,
            src.Id,
            null,
            null,
            null,
            exitCode,
            args,
            tail,
            ok ? null : error);
    }
}
