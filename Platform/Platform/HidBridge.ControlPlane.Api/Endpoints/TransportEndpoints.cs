using HidBridge.Abstractions;
using HidBridge.Application;
using HidBridge.Contracts;

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
                caller.EnsureViewerAccess();
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

        group.MapGet("/{sessionId}/transport/health", async (
            string sessionId,
            HttpContext httpContext,
            ISessionStore sessionStore,
            ICommandJournalStore journalStore,
            IRealtimeTransportFactory transportFactory,
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
                    ReportedAtUtc: DateTimeOffset.UtcNow));
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

    private sealed record WebRtcSignalPublishBody(
        WebRtcSignalKind Kind,
        string SenderPeerId,
        string? RecipientPeerId,
        string Payload,
        string? Mid = null,
        int? MLineIndex = null);
}
