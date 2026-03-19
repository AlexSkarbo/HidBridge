using System.Globalization;
using System.Text.Json;
using HidBridge.Abstractions;
using HidBridge.Contracts;

namespace HidBridge.Application;

/// <summary>
/// Evaluates typed WebRTC relay readiness for one session route.
/// </summary>
public sealed class WebRtcRelayReadinessService
{
    private readonly ISessionStore _sessionStore;
    private readonly IRealtimeTransportFactory _transportFactory;
    private readonly SessionMediaRegistryService? _mediaRegistry;
    private readonly WebRtcRelayReadinessOptions _options;

    /// <summary>
    /// Creates the readiness service.
    /// </summary>
    public WebRtcRelayReadinessService(
        ISessionStore sessionStore,
        IRealtimeTransportFactory transportFactory,
        WebRtcRelayReadinessOptions? options = null)
        : this(sessionStore, transportFactory, mediaRegistry: null, options)
    {
    }

    /// <summary>
    /// Creates the readiness service with optional media stream registry integration.
    /// </summary>
    public WebRtcRelayReadinessService(
        ISessionStore sessionStore,
        IRealtimeTransportFactory transportFactory,
        SessionMediaRegistryService? mediaRegistry,
        WebRtcRelayReadinessOptions? options = null)
    {
        _sessionStore = sessionStore;
        _transportFactory = transportFactory;
        _mediaRegistry = mediaRegistry;
        _options = options ?? new WebRtcRelayReadinessOptions();
    }

    /// <summary>
    /// Evaluates relay readiness for a session/provider pair.
    /// </summary>
    /// <exception cref="KeyNotFoundException">Thrown when the session does not exist.</exception>
    /// <exception cref="InvalidOperationException">Thrown when routing policy cannot resolve provider deterministically.</exception>
    public async Task<SessionTransportReadinessBody> EvaluateAsync(
        string sessionId,
        RealtimeTransportProvider? requestedProvider,
        CancellationToken cancellationToken)
    {
        var snapshot = (await _sessionStore.ListAsync(cancellationToken))
            .FirstOrDefault(x => string.Equals(x.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"Session {sessionId} was not found.");

        var routeResolution = _transportFactory.ResolveRoute(
            new RealtimeTransportRoutePolicyContext(
                EndpointId: snapshot.EndpointId,
                SessionProvider: null,
                RequestedProvider: requestedProvider ?? _options.DefaultProvider));
        var provider = routeResolution.Provider;
        var transport = _transportFactory.Resolve(provider);
        var route = new RealtimeTransportRouteContext(
            AgentId: snapshot.AgentId,
            EndpointId: snapshot.EndpointId,
            SessionId: snapshot.SessionId,
            RoomId: snapshot.SessionId);
        var health = await transport.GetHealthAsync(route, cancellationToken);

        var connected = health.IsConnected;
        var onlinePeerCount = TryReadMetricInt32(health.Metrics, "onlinePeerCount") ?? 0;
        var lastPeerState = TryReadMetricString(health.Metrics, "lastPeerState");
        var lastPeerFailureReason = TryReadMetricString(health.Metrics, "lastPeerFailureReason");
        var lastPeerSeenAtUtc = TryReadMetricDateTimeOffset(health.Metrics, "lastPeerSeenAtUtc");
        var lastRelayAckAtUtc = TryReadMetricDateTimeOffset(health.Metrics, "lastRelayAckAtUtc");
        var mediaReady = TryReadMetricBoolean(health.Metrics, "lastPeerMediaReady");
        var mediaState = TryReadMetricString(health.Metrics, "lastPeerMediaState");
        var mediaFailureReason = TryReadMetricString(health.Metrics, "lastPeerMediaFailureReason");
        var mediaReportedAtUtc = TryReadMetricDateTimeOffset(health.Metrics, "lastPeerMediaReportedAtUtc");
        var mediaStreamId = TryReadMetricString(health.Metrics, "lastPeerMediaStreamId");
        var mediaSource = TryReadMetricString(health.Metrics, "lastPeerMediaSource");
        if (_mediaRegistry is not null)
        {
            var latestMediaSnapshot = await _mediaRegistry.GetLatestAsync(snapshot.SessionId, snapshot.EndpointId, cancellationToken);
            if (latestMediaSnapshot is not null)
            {
                mediaReady = latestMediaSnapshot.Ready;
                mediaState = latestMediaSnapshot.State;
                mediaFailureReason = latestMediaSnapshot.FailureReason;
                mediaReportedAtUtc = latestMediaSnapshot.ReportedAtUtc;
                mediaStreamId = latestMediaSnapshot.StreamId;
                mediaSource = latestMediaSnapshot.Source;
            }
        }

        var (ready, reasonCode, reason) = ResolveReadiness(
            snapshot.State,
            provider,
            connected,
            onlinePeerCount,
            lastPeerState,
            lastPeerFailureReason,
            mediaReady,
            mediaState,
            mediaFailureReason);
        return new SessionTransportReadinessBody(
            SessionId: snapshot.SessionId,
            AgentId: snapshot.AgentId,
            EndpointId: snapshot.EndpointId,
            Provider: provider.ToString(),
            ProviderSource: routeResolution.Source,
            Ready: ready,
            ReasonCode: reasonCode,
            Reason: reason,
            Connected: connected,
            OnlinePeerCount: onlinePeerCount,
            LastPeerState: lastPeerState,
            LastPeerFailureReason: lastPeerFailureReason,
            LastPeerSeenAtUtc: lastPeerSeenAtUtc,
            LastRelayAckAtUtc: lastRelayAckAtUtc,
            MediaReady: mediaReady,
            MediaState: mediaState,
            MediaFailureReason: mediaFailureReason,
            MediaReportedAtUtc: mediaReportedAtUtc,
            MediaStreamId: mediaStreamId,
            MediaSource: mediaSource,
            Metrics: health.Metrics,
            EvaluatedAtUtc: DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Applies canonical readiness policy from current session + transport snapshot.
    /// </summary>
    private (bool Ready, string ReasonCode, string Reason) ResolveReadiness(
        SessionState sessionState,
        RealtimeTransportProvider provider,
        bool connected,
        int onlinePeerCount,
        string? lastPeerState,
        string? lastPeerFailureReason,
        bool? mediaReady,
        string? mediaState,
        string? mediaFailureReason)
    {
        if (sessionState is SessionState.Ended or SessionState.Failed or SessionState.Terminating)
        {
            return (false, "session_not_active", $"Session state is {sessionState}.");
        }

        if (provider != RealtimeTransportProvider.WebRtcDataChannel)
        {
            return (false, "provider_not_webrtc", $"Resolved provider is {provider}, expected WebRtcDataChannel.");
        }

        if (!connected)
        {
            return (false, "transport_disconnected", "WebRTC transport route is disconnected.");
        }

        if (onlinePeerCount < Math.Max(1, _options.MinOnlinePeerCount))
        {
            return (false, "peer_offline", $"Online peer count is {onlinePeerCount}, expected at least {_options.MinOnlinePeerCount}.");
        }

        if (!string.IsNullOrWhiteSpace(lastPeerState)
            && !_options.HealthyPeerStates.Contains(lastPeerState))
        {
            var suffix = string.IsNullOrWhiteSpace(lastPeerFailureReason)
                ? string.Empty
                : $" Failure reason: {lastPeerFailureReason}";
            return (false, "peer_state_unhealthy", $"Last peer state is '{lastPeerState}', which is outside readiness policy.{suffix}");
        }

        if (_options.RequireMediaReady)
        {
            if (mediaReady != true)
            {
                var suffix = string.IsNullOrWhiteSpace(mediaFailureReason)
                    ? string.Empty
                    : $" Failure reason: {mediaFailureReason}";
                return (false, "media_not_ready", $"Media capture path is not ready.{suffix}");
            }

            if (!string.IsNullOrWhiteSpace(mediaState)
                && !_options.HealthyMediaStates.Contains(mediaState))
            {
                var suffix = string.IsNullOrWhiteSpace(mediaFailureReason)
                    ? string.Empty
                    : $" Failure reason: {mediaFailureReason}";
                return (false, "media_state_unhealthy", $"Media state is '{mediaState}', which is outside readiness policy.{suffix}");
            }
        }

        return (true, "ready", "WebRTC relay route is ready.");
    }

    /// <summary>
    /// Reads a metrics dictionary value as boolean.
    /// </summary>
    private static bool? TryReadMetricBoolean(IReadOnlyDictionary<string, object?> metrics, string key)
    {
        if (!metrics.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            bool direct => direct,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement { ValueKind: JsonValueKind.False } => false,
            JsonElement { ValueKind: JsonValueKind.Number } element when element.TryGetInt32(out var number) => number != 0,
            JsonElement { ValueKind: JsonValueKind.String } element when bool.TryParse(element.GetString(), out var parsedJsonBool) => parsedJsonBool,
            string text when bool.TryParse(text, out var parsedTextBool) => parsedTextBool,
            _ when int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedInt) => parsedInt != 0,
            _ => null,
        };
    }

    /// <summary>
    /// Reads a metrics dictionary value as string.
    /// </summary>
    private static string? TryReadMetricString(IReadOnlyDictionary<string, object?> metrics, string key)
    {
        if (!metrics.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            string text when !string.IsNullOrWhiteSpace(text) => text,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            JsonElement element => element.ToString(),
            _ => value.ToString(),
        };
    }

    /// <summary>
    /// Reads a metrics dictionary value as int.
    /// </summary>
    private static int? TryReadMetricInt32(IReadOnlyDictionary<string, object?> metrics, string key)
    {
        if (!metrics.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            int direct => direct,
            long i64 when i64 is >= int.MinValue and <= int.MaxValue => (int)i64,
            double dbl when dbl >= int.MinValue && dbl <= int.MaxValue => Convert.ToInt32(dbl, CultureInfo.InvariantCulture),
            JsonElement { ValueKind: JsonValueKind.Number } element when element.TryGetInt32(out var parsedInt32) => parsedInt32,
            JsonElement { ValueKind: JsonValueKind.String } element when int.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedStringInt32) => parsedStringInt32,
            _ when int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedFallbackInt32) => parsedFallbackInt32,
            _ => null,
        };
    }

    /// <summary>
    /// Reads a metrics dictionary value as UTC timestamp.
    /// </summary>
    private static DateTimeOffset? TryReadMetricDateTimeOffset(IReadOnlyDictionary<string, object?> metrics, string key)
    {
        if (!metrics.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            DateTimeOffset dto => dto,
            DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)),
            JsonElement { ValueKind: JsonValueKind.String } element when DateTimeOffset.TryParse(element.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedJsonString) => parsedJsonString,
            string text when DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedString) => parsedString,
            _ when DateTimeOffset.TryParse(value.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedFallback) => parsedFallback,
            _ => null,
        };
    }
}
