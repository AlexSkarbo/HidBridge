using HidBridge.Abstractions;
using HidBridge.Application;
using HidBridge.Contracts;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace HidBridge.ControlPlane.Api.Endpoints;

/// <summary>
/// Registers transport signaling and transport-health endpoints.
/// </summary>
public static class TransportEndpoints
{
    /// <summary>
    /// Maps session transport endpoints onto the API route table.
    /// </summary>
    public static IEndpointRouteBuilder MapTransportEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/sessions")
            .WithTags(ApiEndpointTags.Sessions);

        group.MapPost("/{sessionId}/transport/webrtc/signals", async (
            string sessionId,
            HttpContext httpContext,
            ISessionStore sessionStore,
            WebRtcSignalingService signaling,
            WebRtcSignalPublishBody request,
            CancellationToken ct) =>
        {
            var caller = ApiCallerContext.FromHttpContext(httpContext);
            try
            {
                caller.EnsureEdgeRelayAccess();
                await caller.RequireScopedSessionAsync(sessionStore, sessionId, ct);
                var senderPeerId = caller.EffectivePrincipalId ?? request.SenderPeerId;
                if (string.IsNullOrWhiteSpace(senderPeerId))
                {
                    return Results.BadRequest(new
                    {
                        sessionId,
                        error = "Sender peer id is required.",
                    });
                }

                var appended = await signaling.AppendAsync(
                    sessionId,
                    request.Kind,
                    senderPeerId,
                    request.RecipientPeerId,
                    request.Payload,
                    request.Mid,
                    request.MLineIndex,
                    ct);
                return Results.Ok(appended);
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { sessionId, error = ex.Message });
            }
            catch (UnauthorizedAccessException ex)
            {
                return ApiAuthorizationResults.Forbidden(caller, ex, sessionId: sessionId);
            }
        })
        .Accepts<WebRtcSignalPublishBody>("application/json")
        .Produces<WebRtcSignalMessageBody>(StatusCodes.Status200OK)
        .WithSummary("Publish one WebRTC signaling message for a session.")
        .WithDescription("Persists one signaling message (offer/answer/ice/bye/heartbeat) for the specified session.");

        group.MapGet("/{sessionId}/transport/webrtc/signals", async (
            string sessionId,
            HttpContext httpContext,
            ISessionStore sessionStore,
            WebRtcSignalingService signaling,
            string? recipientPeerId,
            int? afterSequence,
            int? limit,
            CancellationToken ct) =>
        {
            var caller = ApiCallerContext.FromHttpContext(httpContext);
            try
            {
                caller.EnsureViewerAccess();
                await caller.RequireScopedSessionAsync(sessionStore, sessionId, ct);
                var items = await signaling.ListAsync(
                    sessionId,
                    recipientPeerId,
                    afterSequence,
                    limit ?? 100,
                    ct);
                return Results.Ok(items);
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { sessionId, error = ex.Message });
            }
            catch (UnauthorizedAccessException ex)
            {
                return ApiAuthorizationResults.Forbidden(caller, ex, sessionId: sessionId);
            }
        })
        .Produces<IReadOnlyList<WebRtcSignalMessageBody>>(StatusCodes.Status200OK)
        .WithSummary("Read WebRTC signaling messages for a session.")
        .WithDescription("Reads signaling messages for one session with optional recipient and sequence checkpoint filters.");

        group.MapPost("/{sessionId}/transport/webrtc/peers/{peerId}/online", async (
            string sessionId,
            string peerId,
            HttpContext httpContext,
            ISessionStore sessionStore,
            WebRtcCommandRelayService relay,
            WebRtcPeerPresenceBody? request,
            CancellationToken ct) =>
        {
            var caller = ApiCallerContext.FromHttpContext(httpContext);
            try
            {
                caller.EnsureEdgeRelayAccess();
                await caller.RequireScopedSessionAsync(sessionStore, sessionId, ct);
                var snapshot = await relay.MarkPeerOnlineAsync(
                    sessionId,
                    peerId,
                    request?.EndpointId,
                    request?.Metadata,
                    ct);
                return Results.Ok(snapshot);
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { sessionId, error = ex.Message });
            }
            catch (UnauthorizedAccessException ex)
            {
                return ApiAuthorizationResults.Forbidden(caller, ex, sessionId: sessionId);
            }
        })
        .Accepts<WebRtcPeerPresenceBody>("application/json")
        .Produces<WebRtcPeerStateBody>(StatusCodes.Status200OK)
        .WithSummary("Marks one WebRTC peer online for a session.")
        .WithDescription("Registers one peer heartbeat/presence record that can receive relay command envelopes.");

        group.MapPost("/{sessionId}/transport/webrtc/peers/{peerId}/offline", async (
            string sessionId,
            string peerId,
            HttpContext httpContext,
            ISessionStore sessionStore,
            WebRtcCommandRelayService relay,
            CancellationToken ct) =>
        {
            var caller = ApiCallerContext.FromHttpContext(httpContext);
            try
            {
                caller.EnsureEdgeRelayAccess();
                await caller.RequireScopedSessionAsync(sessionStore, sessionId, ct);
                var snapshot = await relay.MarkPeerOfflineAsync(sessionId, peerId, ct);
                return Results.Ok(snapshot);
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { sessionId, error = ex.Message });
            }
            catch (UnauthorizedAccessException ex)
            {
                return ApiAuthorizationResults.Forbidden(caller, ex, sessionId: sessionId);
            }
        })
        .Produces<WebRtcPeerStateBody>(StatusCodes.Status200OK)
        .WithSummary("Marks one WebRTC peer offline for a session.")
        .WithDescription("Updates peer presence to offline so transport health and relay routing can react deterministically.");

        group.MapGet("/{sessionId}/transport/webrtc/peers", async (
            string sessionId,
            HttpContext httpContext,
            ISessionStore sessionStore,
            WebRtcCommandRelayService relay,
            CancellationToken ct) =>
        {
            var caller = ApiCallerContext.FromHttpContext(httpContext);
            try
            {
                caller.EnsureViewerAccess();
                await caller.RequireScopedSessionAsync(sessionStore, sessionId, ct);
                return Results.Ok(await relay.ListPeersAsync(sessionId, ct));
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { sessionId, error = ex.Message });
            }
            catch (UnauthorizedAccessException ex)
            {
                return ApiAuthorizationResults.Forbidden(caller, ex, sessionId: sessionId);
            }
        })
        .Produces<IReadOnlyList<WebRtcPeerStateBody>>(StatusCodes.Status200OK)
        .WithSummary("Lists WebRTC peers for a session.")
        .WithDescription("Returns transient peer presence records tracked by the WebRTC relay path.");

        group.MapPost("/{sessionId}/transport/media/streams", async (
            string sessionId,
            HttpContext httpContext,
            ISessionStore sessionStore,
            SessionMediaRegistryService mediaRegistry,
            SessionMediaStreamRegistrationBody request,
            CancellationToken ct) =>
        {
            var caller = ApiCallerContext.FromHttpContext(httpContext);
            try
            {
                caller.EnsureEdgeRelayAccess();
                await caller.RequireScopedSessionAsync(sessionStore, sessionId, ct);
                if (string.IsNullOrWhiteSpace(request.PeerId))
                {
                    return Results.BadRequest(new { sessionId, error = "peerId is required." });
                }

                if (string.IsNullOrWhiteSpace(request.EndpointId))
                {
                    return Results.BadRequest(new { sessionId, error = "endpointId is required." });
                }

                if (string.IsNullOrWhiteSpace(request.StreamId))
                {
                    return Results.BadRequest(new { sessionId, error = "streamId is required." });
                }

                var snapshot = await mediaRegistry.UpsertAsync(sessionId, request, ct);
                return Results.Ok(snapshot);
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { sessionId, error = ex.Message });
            }
            catch (UnauthorizedAccessException ex)
            {
                return ApiAuthorizationResults.Forbidden(caller, ex, sessionId: sessionId);
            }
        })
        .Accepts<SessionMediaStreamRegistrationBody>("application/json")
        .Produces<SessionMediaStreamSnapshotBody>(StatusCodes.Status200OK)
        .WithSummary("Registers one edge media stream snapshot.")
        .WithDescription("Upserts one media capture stream snapshot for the supplied session and peer.");

        group.MapGet("/{sessionId}/transport/media/streams", async (
            string sessionId,
            HttpContext httpContext,
            ISessionStore sessionStore,
            SessionMediaRegistryService mediaRegistry,
            string? peerId,
            string? endpointId,
            CancellationToken ct) =>
        {
            var caller = ApiCallerContext.FromHttpContext(httpContext);
            try
            {
                caller.EnsureViewerAccess();
                await caller.RequireScopedSessionAsync(sessionStore, sessionId, ct);
                var items = await mediaRegistry.ListAsync(sessionId, peerId, endpointId, ct);
                return Results.Ok(items);
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { sessionId, error = ex.Message });
            }
            catch (UnauthorizedAccessException ex)
            {
                return ApiAuthorizationResults.Forbidden(caller, ex, sessionId: sessionId);
            }
        })
        .Produces<IReadOnlyList<SessionMediaStreamSnapshotBody>>(StatusCodes.Status200OK)
        .WithSummary("Lists registered edge media streams for a session.")
        .WithDescription("Returns media stream snapshots published by edge agents for the supplied session route.");

        group.MapGet("/{sessionId}/transport/webrtc/commands", async (
            string sessionId,
            HttpContext httpContext,
            ISessionStore sessionStore,
            WebRtcCommandRelayService relay,
            string peerId,
            int? afterSequence,
            int? limit,
            CancellationToken ct) =>
        {
            var caller = ApiCallerContext.FromHttpContext(httpContext);
            try
            {
                caller.EnsureEdgeRelayAccess();
                await caller.RequireScopedSessionAsync(sessionStore, sessionId, ct);
                if (string.IsNullOrWhiteSpace(peerId))
                {
                    return Results.BadRequest(new { sessionId, error = "peerId is required." });
                }

                var items = await relay.ListCommandsAsync(
                    sessionId,
                    peerId.Trim(),
                    afterSequence,
                    limit ?? 100,
                    ct);
                return Results.Ok(items);
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { sessionId, error = ex.Message });
            }
            catch (UnauthorizedAccessException ex)
            {
                return ApiAuthorizationResults.Forbidden(caller, ex, sessionId: sessionId);
            }
        })
        .Produces<IReadOnlyList<WebRtcCommandEnvelopeBody>>(StatusCodes.Status200OK)
        .WithSummary("Lists queued relay commands for one WebRTC peer.")
        .WithDescription("Returns command envelopes awaiting execution for the supplied peer and sequence checkpoint.");

        group.MapPost("/{sessionId}/transport/webrtc/commands/{commandId}/ack", async (
            string sessionId,
            string commandId,
            HttpContext httpContext,
            ISessionStore sessionStore,
            WebRtcCommandRelayService relay,
            IEventWriter eventWriter,
            ILoggerFactory loggerFactory,
            WebRtcCommandAckPublishBody? request,
            CancellationToken ct) =>
        {
            var caller = ApiCallerContext.FromHttpContext(httpContext);
            var logger = loggerFactory.CreateLogger("TransportEndpoints");
            try
            {
                caller.EnsureEdgeRelayAccess();
                await caller.RequireScopedSessionAsync(sessionStore, sessionId, ct);
                if (request is null)
                {
                    return Results.BadRequest(new
                    {
                        sessionId,
                        commandId,
                        error = "Ack body is required and must include status.",
                    });
                }

                if (!request.Status.HasValue)
                {
                    return Results.BadRequest(new
                    {
                        sessionId,
                        commandId,
                        error = "Ack status is required.",
                    });
                }

                var status = request.Status.Value;
                if ((status is CommandStatus.Rejected or CommandStatus.Timeout) && request.Error is null)
                {
                    return Results.BadRequest(new
                    {
                        sessionId,
                        commandId,
                        error = "Ack error payload is required for rejected/timeout statuses.",
                    });
                }

                var ack = new CommandAckBody(
                    commandId,
                    status,
                    request.Error,
                    request.Metrics);
                var accepted = await relay.PublishAckAsync(sessionId, ack, ct);
                try
                {
                    await eventWriter.WriteAuditAsync(
                        new AuditEventBody(
                            Category: "transport.ack",
                            Message: accepted
                                ? $"Transport ACK '{status}' accepted for command {commandId}."
                                : $"Transport ACK '{status}' rejected because pending command {commandId} was not found.",
                            SessionId: sessionId,
                            Data: new Dictionary<string, object?>
                            {
                                ["sessionId"] = sessionId,
                                ["commandId"] = commandId,
                                ["status"] = status.ToString(),
                                ["accepted"] = accepted,
                                ["provider"] = RealtimeTransportProvider.WebRtcDataChannel.ToString(),
                                ["principalId"] = caller.EffectivePrincipalId ?? "unknown",
                                ["errorDomain"] = request.Error?.Domain.ToString(),
                                ["errorCode"] = request.Error?.Code,
                            },
                            CreatedAtUtc: DateTimeOffset.UtcNow),
                        ct);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(
                        ex,
                        "Failed to write transport ACK audit event. Session={SessionId}, Command={CommandId}",
                        sessionId,
                        commandId);
                }

                return accepted
                    ? Results.Ok(new { sessionId, commandId, accepted = true })
                    : Results.NotFound(new { sessionId, commandId, error = "Pending command was not found for acknowledgment." });
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { sessionId, error = ex.Message });
            }
            catch (UnauthorizedAccessException ex)
            {
                return ApiAuthorizationResults.Forbidden(caller, ex, sessionId: sessionId);
            }
        })
        .Accepts<WebRtcCommandAckPublishBody>("application/json")
        .Produces(StatusCodes.Status200OK)
        .WithSummary("Publishes command acknowledgment from the WebRTC relay consumer.")
        .WithDescription("Completes the pending relay command by commandId so dispatch can return the final acknowledgment.");

        group.MapGet("/{sessionId}/transport/readiness", async (
            string sessionId,
            HttpContext httpContext,
            ISessionStore sessionStore,
            WebRtcRelayReadinessService readinessService,
            string? provider,
            CancellationToken ct) =>
        {
            var caller = ApiCallerContext.FromHttpContext(httpContext);
            try
            {
                caller.EnsureViewerAccess();
                if (caller.IsPresent)
                {
                    _ = await caller.RequireScopedSessionAsync(sessionStore, sessionId, ct);
                }

                RealtimeTransportProvider? requestedProvider = null;
                if (!string.IsNullOrWhiteSpace(provider))
                {
                    if (!RealtimeTransportProviderParser.TryParse(provider, out var parsedProvider))
                    {
                        return Results.BadRequest(new
                        {
                            sessionId,
                            error = $"Unknown transport provider '{provider}'.",
                        });
                    }

                    requestedProvider = parsedProvider;
                }

                var readiness = await readinessService.EvaluateAsync(sessionId, requestedProvider, ct);
                return Results.Ok(readiness);
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { sessionId, error = ex.Message });
            }
            catch (UnauthorizedAccessException ex)
            {
                return ApiAuthorizationResults.Forbidden(caller, ex, sessionId: sessionId);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { sessionId, error = ex.Message });
            }
        })
        .Produces<SessionTransportReadinessBody>(StatusCodes.Status200OK)
        .WithSummary("Read typed transport readiness projection for a session route.")
        .WithDescription("Evaluates server-side readiness policy for WebRTC relay routing using current session and transport-health state.");

        group.MapGet("/{sessionId}/transport/health", async (
            string sessionId,
            HttpContext httpContext,
            ISessionStore sessionStore,
            ICommandJournalStore journalStore,
            IRealtimeTransportFactory transportFactory,
            SessionMediaRegistryService mediaRegistry,
            string? provider,
            CancellationToken ct) =>
        {
            var caller = ApiCallerContext.FromHttpContext(httpContext);
            try
            {
                caller.EnsureViewerAccess();
                var snapshot = await caller.RequireScopedSessionAsync(sessionStore, sessionId, ct);

                RealtimeTransportProvider? requestedProvider = null;
                if (!string.IsNullOrWhiteSpace(provider))
                {
                    if (!RealtimeTransportProviderParser.TryParse(provider, out var parsedProvider))
                    {
                        return Results.BadRequest(new
                        {
                            sessionId,
                            error = $"Unknown transport provider '{provider}'.",
                        });
                    }

                    requestedProvider = parsedProvider;
                }

                RealtimeTransportRouteResolution routeResolution;
                try
                {
                    routeResolution = transportFactory.ResolveRoute(
                        new RealtimeTransportRoutePolicyContext(
                            EndpointId: snapshot.EndpointId,
                            SessionProvider: null,
                            RequestedProvider: requestedProvider));
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new { sessionId, error = ex.Message });
                }

                var transport = transportFactory.Resolve(routeResolution.Provider);
                var route = new RealtimeTransportRouteContext(
                    AgentId: snapshot.AgentId,
                    EndpointId: snapshot.EndpointId,
                    SessionId: snapshot.SessionId,
                    RoomId: snapshot.SessionId);
                var health = await transport.GetHealthAsync(route, ct);
                var lastCommand = (await journalStore.ListBySessionAsync(sessionId, ct))
                    .OrderByDescending(x => x.CompletedAtUtc ?? x.CreatedAtUtc)
                    .FirstOrDefault();
                var onlinePeerCount = TryReadMetricInt32(health.Metrics, "onlinePeerCount");
                var lastPeerSeenAtUtc = TryReadMetricDateTimeOffset(health.Metrics, "lastPeerSeenAtUtc");
                var lastPeerState = TryReadMetricString(health.Metrics, "lastPeerState");
                var lastPeerFailureReason = TryReadMetricString(health.Metrics, "lastPeerFailureReason");
                var lastPeerConsecutiveFailures = TryReadMetricInt32(health.Metrics, "lastPeerConsecutiveFailures");
                var lastPeerReconnectBackoffMs = TryReadMetricInt32(health.Metrics, "lastPeerReconnectBackoffMs");
                var lastRelayAckAtUtc = TryReadMetricDateTimeOffset(health.Metrics, "lastRelayAckAtUtc");
                var mediaReady = TryReadMetricBoolean(health.Metrics, "lastPeerMediaReady");
                var mediaState = TryReadMetricString(health.Metrics, "lastPeerMediaState");
                var mediaFailureReason = TryReadMetricString(health.Metrics, "lastPeerMediaFailureReason");
                var mediaReportedAtUtc = TryReadMetricDateTimeOffset(health.Metrics, "lastPeerMediaReportedAtUtc");
                var mediaStreamId = TryReadMetricString(health.Metrics, "lastPeerMediaStreamId");
                var mediaSource = TryReadMetricString(health.Metrics, "lastPeerMediaSource");
                var latestMediaSnapshot = await mediaRegistry.GetLatestAsync(snapshot.SessionId, snapshot.EndpointId, ct);
                if (latestMediaSnapshot is not null)
                {
                    mediaReady = latestMediaSnapshot.Ready;
                    mediaState = latestMediaSnapshot.State;
                    mediaFailureReason = latestMediaSnapshot.FailureReason;
                    mediaReportedAtUtc = latestMediaSnapshot.ReportedAtUtc;
                    mediaStreamId = latestMediaSnapshot.StreamId;
                    mediaSource = latestMediaSnapshot.Source;
                }
                return Results.Ok(new SessionTransportHealthBody(
                    SessionId: snapshot.SessionId,
                    AgentId: snapshot.AgentId,
                    EndpointId: snapshot.EndpointId,
                    Provider: routeResolution.Provider.ToString(),
                    ProviderSource: routeResolution.Source,
                    Connected: health.IsConnected,
                    Status: health.Status,
                    Metrics: health.Metrics,
                    LastCommandAck: lastCommand,
                    ReportedAtUtc: DateTimeOffset.UtcNow,
                    OnlinePeerCount: onlinePeerCount,
                    LastPeerSeenAtUtc: lastPeerSeenAtUtc,
                    LastPeerState: lastPeerState,
                    LastPeerFailureReason: lastPeerFailureReason,
                    LastPeerConsecutiveFailures: lastPeerConsecutiveFailures,
                    LastPeerReconnectBackoffMs: lastPeerReconnectBackoffMs,
                    LastRelayAckAtUtc: lastRelayAckAtUtc,
                    MediaReady: mediaReady,
                    MediaState: mediaState,
                    MediaFailureReason: mediaFailureReason,
                    MediaReportedAtUtc: mediaReportedAtUtc,
                    MediaStreamId: mediaStreamId,
                    MediaSource: mediaSource));
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { sessionId, error = ex.Message });
            }
            catch (UnauthorizedAccessException ex)
            {
                return ApiAuthorizationResults.Forbidden(caller, ex, sessionId: sessionId);
            }
        })
        .Produces<SessionTransportHealthBody>(StatusCodes.Status200OK)
        .WithSummary("Read transport health and latest command ack path for a session.")
        .WithDescription("Resolves the deterministic transport route for a session, reads provider health, and returns the latest command journal acknowledgment for end-to-end diagnostics.");

        return endpoints;
    }

    /// <summary>
    /// Reads one metric value as string from transport-health metrics.
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
    /// Reads one metric value as int from transport-health metrics.
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
    /// Reads one metric value as UTC timestamp from transport-health metrics.
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

    /// <summary>
    /// Reads one metric value as boolean from transport-health metrics.
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
            string text when bool.TryParse(text, out var parsedStringBool) => parsedStringBool,
            _ when int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedInt) => parsedInt != 0,
            _ => null,
        };
    }

}
