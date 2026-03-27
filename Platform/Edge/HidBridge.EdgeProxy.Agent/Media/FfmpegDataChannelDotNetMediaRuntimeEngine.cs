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
        State: "Offline",
        ReportedAtUtc: DateTimeOffset.UtcNow,
        SessionState: "offline",
        VideoTrackState: "missing",
        AudioTrackState: "missing");
    private Process? _ffmpegProcess;
    private DateTimeOffset? _ffmpegStartedAtUtc;
    private DateTimeOffset? _lastTelemetryAtUtc;
    private DateTimeOffset? _sessionObservedAtUtc;
    private string? _lastFfmpegLine;
    private string _sessionState = "offline";
    private string _videoTrackState = "missing";
    private string _audioTrackState = "missing";
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
        SetSnapshot(
            isRunning: false,
            state: "Starting",
            failureReason: null,
            metrics: new Dictionary<string, object?>
            {
                ["engine"] = "ffmpeg-dcd",
                ["streamId"] = context.StreamId,
                ["source"] = context.Source,
            });

        lock (_gate)
        {
            _stopRequested = false;
            _lastTelemetryAtUtc = null;
            _sessionObservedAtUtc = null;
            _lastTelemetryMetrics = null;
            _lastFfmpegLine = null;
            _sessionState = "starting";
            _videoTrackState = "initializing";
            _audioTrackState = "initializing";
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
                },
                sessionState: "offline",
                videoTrackState: "missing",
                audioTrackState: "missing");

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
                },
                sessionState: "offline",
                videoTrackState: "missing",
                audioTrackState: "missing");
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
                    },
                    sessionState: "offline",
                    videoTrackState: "missing",
                    audioTrackState: "missing");
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
                state: "Publishing",
                failureReason: null,
                metrics: new Dictionary<string, object?>
                {
                    ["engine"] = "ffmpeg-dcd",
                    ["pid"] = process.Id,
                    ["executable"] = executable,
                    ["whipUrl"] = _options.MediaWhipUrl,
                    ["whepUrl"] = _options.MediaWhepUrl,
                    ["argsSource"] = argsSource,
                },
                sessionState: "connecting",
                videoTrackState: "negotiating",
                audioTrackState: "negotiating");

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
                },
                sessionState: "offline",
                videoTrackState: "missing",
                audioTrackState: "missing");
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
                state: "Offline",
                failureReason: null,
                sessionState: "offline",
                videoTrackState: "missing",
                audioTrackState: "missing");
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
                state: "Offline",
                failureReason: null,
                sessionState: "offline",
                videoTrackState: "missing",
                audioTrackState: "missing");
        }
        catch (Exception ex)
        {
            SetSnapshot(
                isRunning: false,
                state: "StopFailed",
                failureReason: ex.Message,
                sessionState: "degraded");
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
                if (_lastTelemetryAtUtc.HasValue)
                {
                    var telemetryAgeSec = Math.Max(0, (DateTimeOffset.UtcNow - _lastTelemetryAtUtc.Value).TotalSeconds);
                    metrics["lastTelemetryAtUtc"] = _lastTelemetryAtUtc.Value;
                    metrics["lastTelemetryAgeSec"] = telemetryAgeSec;
                }
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

                if (_sessionObservedAtUtc.HasValue)
                {
                    metrics["mediaSessionObservedAtUtc"] = _sessionObservedAtUtc.Value;
                }

                var state = _snapshot.State;
                string? failureReason = null;
                var sessionState = _sessionState;
                var videoTrackState = _videoTrackState;
                var audioTrackState = _audioTrackState;
                if (_lastTelemetryAtUtc.HasValue)
                {
                    var telemetryAgeSec = Math.Max(0, (DateTimeOffset.UtcNow - _lastTelemetryAtUtc.Value).TotalSeconds);
                    if (telemetryAgeSec > _options.FfmpegTelemetryStaleAfterSec)
                    {
                        state = "Degraded";
                        failureReason = $"ffmpeg progress telemetry is stale for {telemetryAgeSec:F1}s.";
                        sessionState = "degraded";
                    }
                    else
                    {
                        state = "Running";
                        sessionState = "active";
                    }
                }
                else if (string.Equals(state, "Publishing", StringComparison.OrdinalIgnoreCase))
                {
                    state = "Connecting";
                    sessionState = "connecting";
                }

                metrics["mediaSessionState"] = sessionState;
                metrics["mediaVideoTrackState"] = videoTrackState;
                metrics["mediaAudioTrackState"] = audioTrackState;

                return Task.FromResult(_snapshot with
                {
                    State = state,
                    ReportedAtUtc = DateTimeOffset.UtcNow,
                    FailureReason = failureReason,
                    Metrics = metrics,
                    SessionState = sessionState,
                    VideoTrackState = videoTrackState,
                    AudioTrackState = audioTrackState,
                    SessionObservedAtUtc = _sessionObservedAtUtc,
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
            SetSnapshot(
                isRunning: false,
                state: "Offline",
                failureReason: null,
                metrics: new Dictionary<string, object?>
                {
                    ["engine"] = "ffmpeg-dcd",
                    ["lastProcessLine"] = _lastFfmpegLine,
                },
                sessionState: "offline",
                videoTrackState: "missing",
                audioTrackState: "missing");
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
            },
            sessionState: "degraded");
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
                UpdateTrackEvidenceFromProcessLine(args.Data);
                if (TryParseProgressMetrics(args.Data, out var progressMetrics))
                {
                    _lastTelemetryAtUtc = DateTimeOffset.UtcNow;
                    _lastTelemetryMetrics = progressMetrics;
                    _sessionState = "active";
                    _videoTrackState = "active";
                    if (!string.Equals(_audioTrackState, "missing", StringComparison.OrdinalIgnoreCase))
                    {
                        _audioTrackState = "active";
                    }
                    _sessionObservedAtUtc ??= DateTimeOffset.UtcNow;
                    _snapshot = _snapshot with
                    {
                        IsRunning = true,
                        State = "Running",
                        FailureReason = null,
                        ReportedAtUtc = DateTimeOffset.UtcNow,
                        SessionState = _sessionState,
                        VideoTrackState = _videoTrackState,
                        AudioTrackState = _audioTrackState,
                        SessionObservedAtUtc = _sessionObservedAtUtc,
                    };
                }
                else if (TryClassifyRuntimeErrorLine(args.Data, out var runtimeError))
                {
                    _sessionState = "degraded";
                    _snapshot = _snapshot with
                    {
                        IsRunning = true,
                        State = "Degraded",
                        FailureReason = runtimeError,
                        ReportedAtUtc = DateTimeOffset.UtcNow,
                        SessionState = _sessionState,
                        VideoTrackState = _videoTrackState,
                        AudioTrackState = _audioTrackState,
                        SessionObservedAtUtc = _sessionObservedAtUtc,
                    };
                }
            }
        }
    }

    /// <summary>
    /// Parses ffmpeg stream descriptor lines to derive runtime session and track evidence.
    /// </summary>
    private void UpdateTrackEvidenceFromProcessLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        if (TryDetectTrackDescriptor(line, out var hasVideoDescriptor, out var hasAudioDescriptor))
        {
            _sessionState = "connecting";
            _sessionObservedAtUtc ??= DateTimeOffset.UtcNow;
            if (hasVideoDescriptor)
            {
                _videoTrackState = "negotiated";
            }

            if (hasAudioDescriptor)
            {
                _audioTrackState = "negotiated";
            }
        }
    }

    internal static bool TryDetectTrackDescriptor(string line, out bool hasVideoDescriptor, out bool hasAudioDescriptor)
    {
        hasVideoDescriptor = false;
        hasAudioDescriptor = false;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        // Stream descriptor lines usually look like:
        // "Stream #0:0: Video: h264 ..." and "Stream #0:1: Audio: aac ..."
        hasVideoDescriptor = line.Contains("Video:", StringComparison.OrdinalIgnoreCase);
        hasAudioDescriptor = line.Contains("Audio:", StringComparison.OrdinalIgnoreCase);
        return hasVideoDescriptor || hasAudioDescriptor;
    }

    /// <summary>
    /// Classifies fatal/transport ffmpeg stderr lines into deterministic runtime degradation reasons.
    /// </summary>
    internal static bool TryClassifyRuntimeErrorLine(string line, out string reason)
    {
        reason = string.Empty;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var normalized = line.Trim();
        if (normalized.Contains("Connection refused", StringComparison.OrdinalIgnoreCase))
        {
            reason = "ffmpeg media transport connection refused.";
            return true;
        }

        if (normalized.Contains("404 Not Found", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("405 Method Not Allowed", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("406 Not Acceptable", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("401 Unauthorized", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("403 Forbidden", StringComparison.OrdinalIgnoreCase))
        {
            reason = $"ffmpeg media endpoint rejected request: {normalized}";
            return true;
        }

        if (normalized.Contains("Invalid data found when processing input", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("Could not write header", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("Error while opening encoder", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("Immediate exit requested", StringComparison.OrdinalIgnoreCase))
        {
            reason = $"ffmpeg runtime error: {normalized}";
            return true;
        }

        return false;
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
        IReadOnlyDictionary<string, object?>? metrics = null,
        string? sessionState = null,
        string? videoTrackState = null,
        string? audioTrackState = null)
    {
        lock (_gate)
        {
            _sessionState = string.IsNullOrWhiteSpace(sessionState) ? _sessionState : sessionState;
            _videoTrackState = string.IsNullOrWhiteSpace(videoTrackState) ? _videoTrackState : videoTrackState;
            _audioTrackState = string.IsNullOrWhiteSpace(audioTrackState) ? _audioTrackState : audioTrackState;
            if (!string.IsNullOrWhiteSpace(sessionState) && HasSessionEvidenceState(_sessionState))
            {
                _sessionObservedAtUtc ??= DateTimeOffset.UtcNow;
            }
            else if (!string.IsNullOrWhiteSpace(sessionState)
                && string.Equals(_sessionState, "offline", StringComparison.OrdinalIgnoreCase))
            {
                _sessionObservedAtUtc = null;
            }

            _snapshot = new EdgeMediaRuntimeSnapshot(
                IsRunning: isRunning,
                State: state,
                ReportedAtUtc: DateTimeOffset.UtcNow,
                FailureReason: failureReason,
                Metrics: metrics,
                SessionState: _sessionState,
                VideoTrackState: _videoTrackState,
                AudioTrackState: _audioTrackState,
                SessionObservedAtUtc: _sessionObservedAtUtc);
        }
    }

    private static bool HasSessionEvidenceState(string sessionState)
        => string.Equals(sessionState, "connecting", StringComparison.OrdinalIgnoreCase)
            || string.Equals(sessionState, "active", StringComparison.OrdinalIgnoreCase)
            || string.Equals(sessionState, "degraded", StringComparison.OrdinalIgnoreCase);

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
