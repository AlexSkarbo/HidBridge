using System.Net;
using System.Text.Json;
using HidBridge.Edge.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HidBridge.EdgeProxy.Agent;

/// <summary>
/// Probes media/capture health and converts it into typed edge media-readiness snapshots.
/// </summary>
public sealed class EdgeProxyMediaReadinessProbe : IEdgeMediaReadinessProbe
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly EdgeProxyOptions _options;
    private readonly ILogger<EdgeProxyMediaReadinessProbe> _logger;
    private readonly Uri? _mediaHealthUri;

    /// <summary>
    /// Initializes a probe bound to current edge proxy options.
    /// </summary>
    public EdgeProxyMediaReadinessProbe(
        IHttpClientFactory httpClientFactory,
        IOptions<EdgeProxyOptions> options,
        ILogger<EdgeProxyMediaReadinessProbe> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
        _mediaHealthUri = ResolveMediaHealthUri(_options);
    }

    /// <summary>
    /// Reads current media readiness from configured health endpoint.
    /// </summary>
    public async Task<EdgeMediaReadinessSnapshot> GetReadinessAsync(CancellationToken cancellationToken)
    {
        if (_mediaHealthUri is null)
        {
            return BuildNoProbeSnapshot();
        }

        var client = _httpClientFactory.CreateClient("edge-proxy-media");
        using var response = await client.GetAsync(_mediaHealthUri, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var failureReason = BuildHttpFailureReason(response.StatusCode, body);
            return BuildSnapshot(
                isReady: false,
                state: "Unavailable",
                failureReason: failureReason,
                metrics: new Dictionary<string, object?>
                {
                    ["mediaHealthUrl"] = _mediaHealthUri.ToString(),
                    ["statusCode"] = (int)response.StatusCode,
                });
        }

        var parsed = ParseHealthPayload(body);
        return BuildSnapshot(
            isReady: parsed.IsReady,
            state: parsed.State,
            failureReason: parsed.FailureReason,
            streamId: parsed.StreamId,
            source: parsed.Source,
            streamKind: parsed.StreamKind,
            video: parsed.Video,
            audio: parsed.Audio,
            metrics: new Dictionary<string, object?>
            {
                ["mediaHealthUrl"] = _mediaHealthUri.ToString(),
                ["statusCode"] = (int)response.StatusCode,
            });
    }

    /// <summary>
    /// Builds deterministic readiness snapshot when no media probe endpoint is configured.
    /// </summary>
    private EdgeMediaReadinessSnapshot BuildNoProbeSnapshot()
    {
        var assumeReady = _options.AssumeMediaReadyWithoutProbe || !_options.RequireMediaReady;
        return BuildSnapshot(
            isReady: assumeReady,
            state: assumeReady ? "Ready" : "NoProbeConfigured",
            failureReason: assumeReady ? null : "MediaHealthUrl is not configured.");
    }

    /// <summary>
    /// Builds one typed media readiness snapshot from probe inputs.
    /// </summary>
    private EdgeMediaReadinessSnapshot BuildSnapshot(
        bool isReady,
        string state,
        string? failureReason,
        string? streamId = null,
        string? source = null,
        string? streamKind = null,
        EdgeVideoReadinessSnapshot? video = null,
        EdgeAudioReadinessSnapshot? audio = null,
        IReadOnlyDictionary<string, object?>? metrics = null)
    {
        return new EdgeMediaReadinessSnapshot(
            IsReady: isReady,
            State: string.IsNullOrWhiteSpace(state) ? "Unknown" : state,
            ReportedAtUtc: DateTimeOffset.UtcNow,
            FailureReason: failureReason,
            StreamId: string.IsNullOrWhiteSpace(streamId) ? _options.MediaStreamId : streamId,
            Source: string.IsNullOrWhiteSpace(source) ? _options.MediaSource : source,
            StreamKind: string.IsNullOrWhiteSpace(streamKind) ? null : streamKind,
            Video: video,
            Audio: audio,
            Metrics: metrics);
    }

    /// <summary>
    /// Parses health payload variants used by edge media/capture runtimes.
    /// </summary>
    private HealthPayload ParseHealthPayload(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return new HealthPayload(
                IsReady: true,
                State: "Ready",
                FailureReason: null,
                StreamId: null,
                Source: null,
                StreamKind: null,
                Video: null,
                Audio: null);
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new HealthPayload(
                    IsReady: true,
                    State: "Ready",
                    FailureReason: null,
                    StreamId: null,
                    Source: null,
                    StreamKind: null,
                    Video: null,
                    Audio: null);
            }

            var ready = TryReadBoolean(root, "ok")
                ?? TryReadBoolean(root, "ready")
                ?? TryReadBoolean(root, "connected")
                ?? false;

            var state = TryReadString(root, "state")
                ?? TryReadString(root, "status")
                ?? (ready ? "Ready" : "Unavailable");

            if (!TryReadBoolean(root, "ok").HasValue
                && !TryReadBoolean(root, "ready").HasValue
                && !TryReadBoolean(root, "connected").HasValue)
            {
                ready = IsHealthyState(state);
            }

            var failureReason = TryReadString(root, "failureReason")
                ?? TryReadString(root, "error")
                ?? TryReadString(root, "message")
                ?? TryReadString(root, "reason");

            if (ready)
            {
                failureReason = null;
            }

            return new HealthPayload(
                IsReady: ready,
                State: state,
                FailureReason: failureReason,
                StreamId: TryReadString(root, "streamId") ?? TryReadString(root, "stream_id"),
                Source: TryReadString(root, "source") ?? TryReadString(root, "captureSource"),
                StreamKind: TryReadString(root, "streamKind") ?? TryReadString(root, "stream_kind") ?? TryReadString(root, "kind"),
                Video: ParseVideo(root),
                Audio: ParseAudio(root));
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "Media health payload is not valid JSON. Assuming ready due to HTTP 2xx status.");
            return new HealthPayload(
                IsReady: true,
                State: "Ready",
                FailureReason: null,
                StreamId: null,
                Source: null,
                StreamKind: null,
                Video: null,
                Audio: null);
        }
    }

    /// <summary>
    /// Parses optional typed video descriptors from health payload.
    /// </summary>
    private static EdgeVideoReadinessSnapshot? ParseVideo(JsonElement root)
    {
        var codec = TryReadNestedString(root, "video", "codec") ?? TryReadString(root, "videoCodec");
        var width = TryReadNestedInt(root, "video", "width") ?? TryReadInt(root, "width");
        var height = TryReadNestedInt(root, "video", "height") ?? TryReadInt(root, "height");
        var frameRate = TryReadNestedDouble(root, "video", "frameRate")
            ?? TryReadNestedDouble(root, "video", "fps")
            ?? TryReadDouble(root, "frameRate")
            ?? TryReadDouble(root, "fps");
        var bitrateKbps = TryReadNestedInt(root, "video", "bitrateKbps")
            ?? TryReadNestedInt(root, "video", "bitrate")
            ?? TryReadInt(root, "videoBitrateKbps");

        if (codec is null && width is null && height is null && frameRate is null && bitrateKbps is null)
        {
            return null;
        }

        return new EdgeVideoReadinessSnapshot(
            Codec: codec,
            Width: width,
            Height: height,
            FrameRate: frameRate,
            BitrateKbps: bitrateKbps);
    }

    /// <summary>
    /// Parses optional typed audio descriptors from health payload.
    /// </summary>
    private static EdgeAudioReadinessSnapshot? ParseAudio(JsonElement root)
    {
        var codec = TryReadNestedString(root, "audio", "codec") ?? TryReadString(root, "audioCodec");
        var channels = TryReadNestedInt(root, "audio", "channels") ?? TryReadInt(root, "audioChannels");
        var sampleRateHz = TryReadNestedInt(root, "audio", "sampleRateHz")
            ?? TryReadNestedInt(root, "audio", "sampleRate")
            ?? TryReadInt(root, "audioSampleRateHz");
        var bitrateKbps = TryReadNestedInt(root, "audio", "bitrateKbps")
            ?? TryReadNestedInt(root, "audio", "bitrate")
            ?? TryReadInt(root, "audioBitrateKbps");

        if (codec is null && channels is null && sampleRateHz is null && bitrateKbps is null)
        {
            return null;
        }

        return new EdgeAudioReadinessSnapshot(
            Codec: codec,
            Channels: channels,
            SampleRateHz: sampleRateHz,
            BitrateKbps: bitrateKbps);
    }

    /// <summary>
    /// Resolves media health URL from explicit setting or control websocket URL.
    /// </summary>
    private static Uri? ResolveMediaHealthUri(EdgeProxyOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.MediaHealthUrl)
            && Uri.TryCreate(options.MediaHealthUrl, UriKind.Absolute, out var explicitUri))
        {
            return explicitUri;
        }

        // UART executor does not depend on legacy control websocket runtime, so
        // media readiness must come from an explicit MediaHealthUrl when required.
        if (options.GetCommandExecutorKind() != EdgeProxyCommandExecutorKind.ControlWs)
        {
            return null;
        }

        if (!Uri.TryCreate(options.ControlWsUrl, UriKind.Absolute, out var controlWsUri))
        {
            return null;
        }

        if (!string.Equals(controlWsUri.Scheme, Uri.UriSchemeWs, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(controlWsUri.Scheme, Uri.UriSchemeWss, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var healthScheme = string.Equals(controlWsUri.Scheme, Uri.UriSchemeWss, StringComparison.OrdinalIgnoreCase)
            ? Uri.UriSchemeHttps
            : Uri.UriSchemeHttp;
        var builder = new UriBuilder(controlWsUri)
        {
            Scheme = healthScheme,
            Path = "/health",
            Query = string.Empty,
            Fragment = string.Empty,
        };
        return builder.Uri;
    }

    /// <summary>
    /// Checks whether one media status token should be treated as healthy.
    /// </summary>
    private static bool IsHealthyState(string? state)
    {
        if (string.IsNullOrWhiteSpace(state))
        {
            return false;
        }

        return string.Equals(state, "ok", StringComparison.OrdinalIgnoreCase)
            || string.Equals(state, "ready", StringComparison.OrdinalIgnoreCase)
            || string.Equals(state, "healthy", StringComparison.OrdinalIgnoreCase)
            || string.Equals(state, "connected", StringComparison.OrdinalIgnoreCase)
            || string.Equals(state, "streaming", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reads one string property from a JSON object when present.
    /// </summary>
    private static string? TryReadString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Null => null,
            _ => value.ToString(),
        };
    }

    /// <summary>
    /// Reads one integer property from a JSON object when present.
    /// </summary>
    private static int? TryReadInt(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var parsed) => parsed,
            JsonValueKind.String when int.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null,
        };
    }

    /// <summary>
    /// Reads one floating-point property from a JSON object when present.
    /// </summary>
    private static double? TryReadDouble(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDouble(out var parsed) => parsed,
            JsonValueKind.String when double.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null,
        };
    }

    /// <summary>
    /// Reads nested string property from one child JSON object when present.
    /// </summary>
    private static string? TryReadNestedString(JsonElement root, string objectProperty, string childProperty)
    {
        if (!root.TryGetProperty(objectProperty, out var nested) || nested.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return TryReadString(nested, childProperty);
    }

    /// <summary>
    /// Reads nested integer property from one child JSON object when present.
    /// </summary>
    private static int? TryReadNestedInt(JsonElement root, string objectProperty, string childProperty)
    {
        if (!root.TryGetProperty(objectProperty, out var nested) || nested.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return TryReadInt(nested, childProperty);
    }

    /// <summary>
    /// Reads nested floating-point property from one child JSON object when present.
    /// </summary>
    private static double? TryReadNestedDouble(JsonElement root, string objectProperty, string childProperty)
    {
        if (!root.TryGetProperty(objectProperty, out var nested) || nested.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return TryReadDouble(nested, childProperty);
    }

    /// <summary>
    /// Reads one boolean property from a JSON object when present.
    /// </summary>
    private static bool? TryReadBoolean(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null,
        };
    }

    /// <summary>
    /// Builds normalized HTTP failure reason from status code and optional payload body.
    /// </summary>
    private static string BuildHttpFailureReason(HttpStatusCode statusCode, string body)
    {
        var suffix = string.IsNullOrWhiteSpace(body)
            ? string.Empty
            : $": {body.Trim()}";
        return $"HTTP {(int)statusCode}{suffix}";
    }

    /// <summary>
    /// Represents one parsed media-health payload.
    /// </summary>
    private sealed record HealthPayload(
        bool IsReady,
        string State,
        string? FailureReason,
        string? StreamId,
        string? Source,
        string? StreamKind,
        EdgeVideoReadinessSnapshot? Video,
        EdgeAudioReadinessSnapshot? Audio);
}
