using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace HidBridge.EdgeProxy.Agent.Media;

/// <summary>
/// ffmpeg + DataChannelDotNet media runtime engine (process-orchestration scaffold).
/// </summary>
internal sealed class FfmpegDataChannelDotNetMediaRuntimeEngine : IEdgeMediaRuntimeEngine
{
    private readonly object _gate = new();
    private readonly EdgeProxyOptions _options;
    private readonly ILogger _logger;
    private EdgeMediaRuntimeSnapshot _snapshot = new(
        IsRunning: false,
        State: "NotStarted",
        ReportedAtUtc: DateTimeOffset.UtcNow);
    private Process? _ffmpegProcess;
    private DateTimeOffset? _ffmpegStartedAtUtc;
    private string? _lastFfmpegLine;
    private IReadOnlyDictionary<string, object?>? _lastTelemetryMetrics;
    private bool _stopRequested;

    /// <summary>
    /// Creates ffmpeg+dcd runtime engine.
    /// </summary>
    public FfmpegDataChannelDotNetMediaRuntimeEngine(
        EdgeProxyOptions options,
        ILogger logger)
    {
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(EdgeMediaRuntimeContext context, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _stopRequested = false;
        }

        var executable = _options.FfmpegExecutablePath;
        if (string.IsNullOrWhiteSpace(executable))
        {
            SetSnapshot(
                isRunning: false,
                state: "NoExecutableConfigured",
                failureReason: "FfmpegExecutablePath is not configured.",
                metrics: new Dictionary<string, object?>
                {
                    ["engine"] = "ffmpeg-dcd",
                    ["streamId"] = context.StreamId,
                    ["source"] = context.Source,
                    ["whepUrl"] = _options.MediaWhepUrl,
                });

            _logger.LogWarning(
                "MediaEngine=ffmpeg-dcd is selected but FfmpegExecutablePath is empty. Worker continues with probe-driven media readiness.");
            return Task.CompletedTask;
        }

        var argsTemplate = _options.FfmpegArgumentsTemplate;
        var argsSource = "ConfiguredTemplate";
        if (string.IsNullOrWhiteSpace(argsTemplate) &&
            !string.IsNullOrWhiteSpace(_options.MediaWhipUrl))
        {
            argsTemplate = BuildDefaultWhipArgumentsTemplate(_options.MediaWhipBearerToken);
            argsSource = "DefaultWhipTemplate";
        }
        if (string.IsNullOrWhiteSpace(argsTemplate))
        {
            SetSnapshot(
                isRunning: false,
                state: "NoArgumentsConfigured",
                failureReason: "FfmpegArgumentsTemplate is not configured.",
                metrics: new Dictionary<string, object?>
                {
                    ["engine"] = "ffmpeg-dcd",
                    ["executable"] = executable,
                    ["whipUrl"] = _options.MediaWhipUrl,
                    ["whepUrl"] = _options.MediaWhepUrl,
                    ["argsSource"] = argsSource,
                });
            _logger.LogWarning(
                "MediaEngine=ffmpeg-dcd is selected but FfmpegArgumentsTemplate is empty. Worker continues with probe-driven media readiness.");
            return Task.CompletedTask;
        }

        try
        {
            var args = BuildArguments(argsTemplate, context);
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            var process = new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = true,
            };
            process.OutputDataReceived += HandleProcessOutput;
            process.ErrorDataReceived += HandleProcessOutput;

            if (!process.Start())
            {
                SetSnapshot(
                    isRunning: false,
                    state: "StartFailed",
                    failureReason: "Process.Start() returned false.",
                    metrics: new Dictionary<string, object?>
                    {
                        ["engine"] = "ffmpeg-dcd",
                        ["executable"] = executable,
                        ["whipUrl"] = _options.MediaWhipUrl,
                        ["whepUrl"] = _options.MediaWhepUrl,
                    });
                return Task.CompletedTask;
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            lock (_gate)
            {
                _ffmpegProcess = process;
                _ffmpegStartedAtUtc = DateTimeOffset.UtcNow;
            }

            SetSnapshot(
                isRunning: true,
                state: "Running",
                failureReason: null,
                metrics: new Dictionary<string, object?>
                {
                    ["engine"] = "ffmpeg-dcd",
                    ["pid"] = process.Id,
                    ["executable"] = executable,
                    ["whipUrl"] = _options.MediaWhipUrl,
                    ["whepUrl"] = _options.MediaWhepUrl,
                    ["argsSource"] = argsSource,
                });

            _ = MonitorExitAsync(process);
            _logger.LogInformation("Started ffmpeg media runtime process. PID={Pid}", process.Id);
        }
        catch (Exception ex)
        {
            SetSnapshot(
                isRunning: false,
                state: "StartFailed",
                failureReason: ex.Message,
                metrics: new Dictionary<string, object?>
                {
                    ["engine"] = "ffmpeg-dcd",
                    ["executable"] = executable,
                    ["whipUrl"] = _options.MediaWhipUrl,
                    ["whepUrl"] = _options.MediaWhepUrl,
                });
            _logger.LogWarning(ex, "Failed to start ffmpeg media runtime process.");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Process? process;
        lock (_gate)
        {
            _stopRequested = true;
            process = _ffmpegProcess;
            _ffmpegProcess = null;
            _ffmpegStartedAtUtc = null;
        }

        if (process is null)
        {
            SetSnapshot(
                isRunning: false,
                state: "Stopped",
                failureReason: null);
            return;
        }

        try
        {
            using (process)
            {
                if (!process.HasExited)
                {
                    try
                    {
                        process.CloseMainWindow();
                    }
                    catch
                    {
                    }

                    var exited = await WaitForExitAsync(process, _options.FfmpegStopTimeoutMs, cancellationToken);
                    if (!exited && !process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        await WaitForExitAsync(process, 2000, cancellationToken);
                    }
                }
            }

            SetSnapshot(
                isRunning: false,
                state: "Stopped",
                failureReason: null);
        }
        catch (Exception ex)
        {
            SetSnapshot(
                isRunning: false,
                state: "StopFailed",
                failureReason: ex.Message);
            _logger.LogWarning(ex, "Failed while stopping ffmpeg media runtime process.");
        }
    }

    /// <inheritdoc />
    public Task<EdgeMediaRuntimeSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (_snapshot.IsRunning && _ffmpegStartedAtUtc.HasValue)
            {
                var metrics = new Dictionary<string, object?>(
                    _snapshot.Metrics ?? new Dictionary<string, object?>(),
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["uptimeSec"] = Math.Max(0, (DateTimeOffset.UtcNow - _ffmpegStartedAtUtc.Value).TotalSeconds),
                };
                if (!string.IsNullOrWhiteSpace(_lastFfmpegLine))
                {
                    metrics["lastProcessLine"] = _lastFfmpegLine;
                }
                if (_lastTelemetryMetrics is not null)
                {
                    foreach (var pair in _lastTelemetryMetrics)
                    {
                        metrics[pair.Key] = pair.Value;
                    }
                }

                return Task.FromResult(_snapshot with
                {
                    ReportedAtUtc = DateTimeOffset.UtcNow,
                    Metrics = metrics,
                });
            }

            return Task.FromResult(_snapshot);
        }
    }

    /// <summary>
    /// Monitors child process completion and projects exit status into runtime snapshot.
    /// </summary>
    private async Task MonitorExitAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
        }
        catch
        {
            return;
        }

        bool stopRequested;
        lock (_gate)
        {
            stopRequested = _stopRequested;
            if (ReferenceEquals(_ffmpegProcess, process))
            {
                _ffmpegProcess = null;
                _ffmpegStartedAtUtc = null;
            }
        }

        if (stopRequested)
        {
            return;
        }

        SetSnapshot(
            isRunning: false,
            state: "ExitedUnexpectedly",
            failureReason: $"ffmpeg process exited with code {process.ExitCode}.",
            metrics: new Dictionary<string, object?>
            {
                ["engine"] = "ffmpeg-dcd",
                ["exitCode"] = process.ExitCode,
                ["lastProcessLine"] = _lastFfmpegLine,
            });
    }

    /// <summary>
    /// Stores latest process output line for diagnostics.
    /// </summary>
    private void HandleProcessOutput(object sender, DataReceivedEventArgs args)
    {
        if (!string.IsNullOrWhiteSpace(args.Data))
        {
            lock (_gate)
            {
                _lastFfmpegLine = args.Data;
                if (TryParseProgressMetrics(args.Data, out var progressMetrics))
                {
                    _lastTelemetryMetrics = progressMetrics;
                }
            }
        }
    }

    /// <summary>
    /// Parses ffmpeg progress lines into stable telemetry metrics.
    /// </summary>
    internal static bool TryParseProgressMetrics(
        string line,
        out IReadOnlyDictionary<string, object?> metrics)
    {
        metrics = new Dictionary<string, object?>();
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        static string? TryReadToken(string source, string key)
        {
            var match = Regex.Match(
                source,
                $@"\b{Regex.Escape(key)}\s*=\s*(?<value>\S+)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return match.Success ? match.Groups["value"].Value : null;
        }

        static bool TryParseDouble(string value, out double result) =>
            double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result);

        static bool TryParseInt(string value, out int result) =>
            int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);

        var parsed = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var any = false;

        if (TryReadToken(line, "frame") is { } frameToken && TryParseInt(frameToken, out var frame))
        {
            parsed["ffmpegFrame"] = frame;
            any = true;
        }

        if (TryReadToken(line, "fps") is { } fpsToken && TryParseDouble(fpsToken, out var fps))
        {
            parsed["ffmpegFps"] = fps;
            any = true;
        }

        if (TryReadToken(line, "speed") is { } speedToken)
        {
            speedToken = speedToken.TrimEnd('x', 'X');
            if (TryParseDouble(speedToken, out var speed))
            {
                parsed["ffmpegSpeed"] = speed;
                any = true;
            }
        }

        if (TryReadToken(line, "drop") is { } dropToken && TryParseInt(dropToken, out var drop))
        {
            parsed["ffmpegDropFrames"] = drop;
            any = true;
        }

        if (TryReadToken(line, "dup") is { } dupToken && TryParseInt(dupToken, out var dup))
        {
            parsed["ffmpegDupFrames"] = dup;
            any = true;
        }

        if (TryReadToken(line, "time") is { } timeToken &&
            TimeSpan.TryParse(timeToken, CultureInfo.InvariantCulture, out var time))
        {
            parsed["ffmpegTimeSec"] = Math.Max(0, time.TotalSeconds);
            any = true;
        }

        if (TryReadToken(line, "bitrate") is { } bitrateToken &&
            TryParseBitrateKbps(bitrateToken, out var bitrateKbps))
        {
            parsed["ffmpegBitrateKbps"] = bitrateKbps;
            any = true;
        }

        metrics = parsed;
        return any;
    }

    /// <summary>
    /// Parses ffmpeg bitrate token (<c>1024.0kbits/s</c>, <c>1.2Mbits/s</c>, <c>N/A</c>) into kbps.
    /// </summary>
    private static bool TryParseBitrateKbps(string token, out double valueKbps)
    {
        valueKbps = 0d;
        if (string.IsNullOrWhiteSpace(token) ||
            string.Equals(token, "N/A", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        token = token.Trim();
        var match = Regex.Match(
            token,
            @"^(?<value>[0-9]+(?:\.[0-9]+)?)(?<unit>[kKmMgG]?)(?:bits/s)?$",
            RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return false;
        }

        if (!double.TryParse(
                match.Groups["value"].Value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var parsed))
        {
            return false;
        }

        var unit = match.Groups["unit"].Value.ToUpperInvariant();
        var multiplier = unit switch
        {
            "G" => 1_000_000d,
            "M" => 1_000d,
            "K" => 1d,
            _ => 0.001d,
        };

        valueKbps = parsed * multiplier;
        return true;
    }

    /// <summary>
    /// Replaces argument placeholders with runtime session values.
    /// </summary>
    private string BuildArguments(string template, EdgeMediaRuntimeContext context)
    {
        return template
            .Replace("{sessionId}", context.SessionId, StringComparison.OrdinalIgnoreCase)
            .Replace("{peerId}", context.PeerId, StringComparison.OrdinalIgnoreCase)
            .Replace("{endpointId}", context.EndpointId, StringComparison.OrdinalIgnoreCase)
            .Replace("{streamId}", context.StreamId, StringComparison.OrdinalIgnoreCase)
            .Replace("{source}", context.Source, StringComparison.OrdinalIgnoreCase)
            .Replace("{baseUrl}", _options.BaseUrl, StringComparison.OrdinalIgnoreCase)
            .Replace("{whipUrl}", _options.MediaWhipUrl, StringComparison.OrdinalIgnoreCase)
            .Replace("{whepUrl}", _options.MediaWhepUrl, StringComparison.OrdinalIgnoreCase)
            .Replace("{whipBearerToken}", _options.MediaWhipBearerToken, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Builds default ffmpeg WHIP publish arguments used when template is not explicitly configured.
    /// </summary>
    private static string BuildDefaultWhipArgumentsTemplate(string? whipBearerToken)
    {
        var headerPrefix = string.IsNullOrWhiteSpace(whipBearerToken)
            ? string.Empty
            : "-headers \"Authorization: Bearer {{whipBearerToken}}\\r\\n\" ";
        return $"{headerPrefix}-re -f lavfi -i testsrc2=size=1280x720:rate=30 -f lavfi -i anullsrc=channel_layout=stereo:sample_rate=48000 -shortest -c:v libx264 -preset veryfast -tune zerolatency -pix_fmt yuv420p -g 30 -keyint_min 30 -c:a aac -b:a 128k -f whip {{whipUrl}}";
    }

    /// <summary>
    /// Updates current runtime snapshot.
    /// </summary>
    private void SetSnapshot(
        bool isRunning,
        string state,
        string? failureReason,
        IReadOnlyDictionary<string, object?>? metrics = null)
    {
        lock (_gate)
        {
            _snapshot = new EdgeMediaRuntimeSnapshot(
                IsRunning: isRunning,
                State: state,
                ReportedAtUtc: DateTimeOffset.UtcNow,
                FailureReason: failureReason,
                Metrics: metrics);
        }
    }

    /// <summary>
    /// Waits for process exit with bounded timeout.
    /// </summary>
    private static async Task<bool> WaitForExitAsync(
        Process process,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        if (process.HasExited)
        {
            return true;
        }

        var waitTask = process.WaitForExitAsync(cancellationToken);
        var timeoutTask = Task.Delay(timeoutMs, cancellationToken);
        var completed = await Task.WhenAny(waitTask, timeoutTask).ConfigureAwait(false);
        return completed == waitTask;
    }
}
