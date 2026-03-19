using System.Net;
using System.Net.Http.Json;
using System.IO;
using System.Text.Json;
using HidBridge.ControlPlane.Web.Models;
using HidBridge.ControlPlane.Web.Services;
using Xunit;

namespace HidBridge.Platform.Tests;

/// <summary>
/// Verifies that the thin operator shell targets the expected ControlPlane routes.
/// </summary>
public sealed class ControlPlaneApiClientTests
{
    /// <summary>
    /// Verifies the health route.
    /// </summary>
    [Fact]
    public async Task GetHealthAsync_TargetsHealthRoute()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var handler = new RecordingHandler(new ApiHealthViewModel("HidBridge.ControlPlane.Api", "ok", DateTimeOffset.UtcNow));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:18093") };
        var apiClient = new ControlPlaneApiClient(httpClient);

        _ = await apiClient.GetHealthAsync(cancellationToken);

        Assert.Equal(HttpMethod.Get, handler.LastMethod);
        Assert.Equal("http://localhost:18093/health", handler.LastRequestUri);
    }

    /// <summary>
    /// Verifies the runtime route.
    /// </summary>
    [Fact]
    public async Task GetRuntimeAsync_TargetsRuntimeRoute()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var handler = new RecordingHandler(new ApiRuntimeViewModel(
            "Uart",
            "COM6",
            3000000,
            255,
            254,
            "agent-1",
            "endpoint-1",
            "Sql",
            "App_Data",
            "hidbridge",
            true,
            new ApiRuntimePolicyBootstrapViewModel("default", "local-tenant", "local-org", true, true, true, 3),
            new ApiRuntimeAuthenticationViewModel(true, "http://127.0.0.1:18096/realms/hidbridge-dev", null, false, true, ["/api/v1/diagnostics/policies"], ["/api/v1/sessions"], []),
            new ApiRuntimePolicyRevisionLifecycleViewModel(30, 30, 25),
            new ApiRuntimeMaintenanceViewModel(30, 120, 30, 30, 7)));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:18093") };
        var apiClient = new ControlPlaneApiClient(httpClient);

        _ = await apiClient.GetRuntimeAsync(cancellationToken);

        Assert.Equal(HttpMethod.Get, handler.LastMethod);
        Assert.Equal("http://localhost:18093/api/v1/runtime/uart", handler.LastRequestUri);
    }

    /// <summary>
    /// Verifies the runtime payload from the API endpoint shape can be deserialized by the web client model.
    /// </summary>
    [Fact]
    public async Task GetRuntimeAsync_ParsesApiRuntimePayloadShape()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var handler = new RecordingHandler(new
        {
            transportProvider = "Uart",
            port = "COM6",
            baudRate = 3000000,
            mouseSelector = 255,
            keyboardSelector = 254,
            agentId = "agent-1",
            endpointId = "endpoint-1",
            persistenceProvider = "Sql",
            dataRoot = "App_Data",
            sqlSchema = "hidbridge",
            sqlApplyMigrations = true,
            policyBootstrap = new
            {
                scopeId = "default",
                tenantId = "local-tenant",
                organizationId = "local-org",
                viewerRoleRequired = true,
                moderatorOverrideEnabled = true,
                adminOverrideEnabled = true,
                assignmentCount = 3,
            },
            authentication = new
            {
                enabled = true,
                authority = "http://127.0.0.1:18096/realms/hidbridge-dev",
                audience = "controlplane-api",
                requireHttpsMetadata = false,
                allowHeaderFallback = true,
                bearerOnlyPrefixes = new[] { "/api/v1/diagnostics/policies" },
                callerContextRequiredPrefixes = new[] { "/api/v1/sessions" },
                headerFallbackDisabledPatterns = Array.Empty<string>(),
            },
            policyRevisionLifecycle = new
            {
                maintenanceIntervalSeconds = 30,
                retentionDays = 30,
                maxRevisionsPerEntity = 25,
            },
            maintenance = new
            {
                leaseSeconds = 30,
                recoveryGraceSeconds = 120,
                cleanupIntervalSeconds = 30,
                auditRetentionDays = 30,
                telemetryRetentionDays = 7,
            },
        });
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:18093") };
        var apiClient = new ControlPlaneApiClient(httpClient);

        var runtime = await apiClient.GetRuntimeAsync(cancellationToken);

        Assert.NotNull(runtime);
        Assert.Equal("Uart", runtime!.TransportProvider);
        Assert.True(runtime!.PolicyBootstrap.ViewerRoleRequired);
    }

    /// <summary>
    /// Verifies the open-session route and payload.
    /// </summary>
    [Fact]
    public async Task OpenSessionAsync_SendsExpectedRouteAndPayload()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var request = new SessionOpenRequestViewModel
        {
            SessionId = "room-endpoint-local-001",
            Profile = "Balanced",
            RequestedBy = "operator-a",
            TargetAgentId = "agent-local",
            TargetEndpointId = "endpoint-local",
            ShareMode = "Owner",
            TenantId = "local-tenant",
            OrganizationId = "local-org",
            OperatorRoles = ["operator.viewer"],
        };
        var handler = new RecordingHandler(request);
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:18093") };
        var apiClient = new ControlPlaneApiClient(httpClient);

        _ = await apiClient.OpenSessionAsync(request, cancellationToken);

        Assert.Equal(HttpMethod.Post, handler.LastMethod);
        Assert.Equal("http://localhost:18093/api/v1/sessions", handler.LastRequestUri);

        var payload = JsonSerializer.Deserialize<Dictionary<string, object?>>(handler.LastRequestBody!, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal("room-endpoint-local-001", payload!["sessionId"]?.ToString());
        Assert.Equal("agent-local", payload["targetAgentId"]?.ToString());
        Assert.Equal("endpoint-local", payload["targetEndpointId"]?.ToString());
    }

    /// <summary>
    /// Verifies the close-session route and payload.
    /// </summary>
    [Fact]
    public async Task CloseSessionAsync_SendsExpectedRouteAndPayload()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var handler = new RecordingHandler(new
        {
            sessionId = "room-endpoint-local-001",
            reason = "closed from operator shell",
        });
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:18093") };
        var apiClient = new ControlPlaneApiClient(httpClient);

        await apiClient.CloseSessionAsync("room-endpoint-local-001", "closed from operator shell", cancellationToken);

        Assert.Equal(HttpMethod.Post, handler.LastMethod);
        Assert.Equal("http://localhost:18093/api/v1/sessions/room-endpoint-local-001/close", handler.LastRequestUri);

        var payload = JsonSerializer.Deserialize<Dictionary<string, object?>>(handler.LastRequestBody!, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal("room-endpoint-local-001", payload!["sessionId"]?.ToString());
        Assert.Equal("closed from operator shell", payload["reason"]?.ToString());
    }

    /// <summary>
    /// Verifies the bulk close-failed action route and payload.
    /// </summary>
    [Fact]
    public async Task CloseFailedSessionsAsync_SendsExpectedRouteAndPayload()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var handler = new RecordingHandler(new
        {
            action = "close-failed",
            dryRun = false,
            reason = "bulk cleanup",
            generatedAtUtc = DateTimeOffset.UtcNow,
            scannedSessions = 3,
            matchedSessions = 2,
            closedSessions = 2,
            skippedSessions = 0,
            failedClosures = 0,
            staleAfterMinutes = (int?)null,
            matchedSessionIds = new[] { "room-1", "room-2" },
            closedSessionIds = new[] { "room-1", "room-2" },
            skippedSessionIds = Array.Empty<string>(),
            errors = Array.Empty<string>(),
        });
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:18093") };
        var apiClient = new ControlPlaneApiClient(httpClient);

        _ = await apiClient.CloseFailedSessionsAsync("bulk cleanup", false, cancellationToken);

        Assert.Equal(HttpMethod.Post, handler.LastMethod);
        Assert.Equal("http://localhost:18093/api/v1/sessions/actions/close-failed", handler.LastRequestUri);

        var payload = JsonSerializer.Deserialize<Dictionary<string, object?>>(handler.LastRequestBody!, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal("False", payload!["dryRun"]?.ToString());
        Assert.Equal("bulk cleanup", payload["reason"]?.ToString());
    }

    /// <summary>
    /// Verifies the bulk close-stale action route and payload.
    /// </summary>
    [Fact]
    public async Task CloseStaleSessionsAsync_SendsExpectedRouteAndPayload()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var handler = new RecordingHandler(new
        {
            action = "close-stale",
            dryRun = false,
            reason = "stale cleanup",
            generatedAtUtc = DateTimeOffset.UtcNow,
            scannedSessions = 5,
            matchedSessions = 1,
            closedSessions = 1,
            skippedSessions = 0,
            failedClosures = 0,
            staleAfterMinutes = 45,
            matchedSessionIds = new[] { "room-9" },
            closedSessionIds = new[] { "room-9" },
            skippedSessionIds = Array.Empty<string>(),
            errors = Array.Empty<string>(),
        });
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:18093") };
        var apiClient = new ControlPlaneApiClient(httpClient);

        _ = await apiClient.CloseStaleSessionsAsync("stale cleanup", 45, false, cancellationToken);

        Assert.Equal(HttpMethod.Post, handler.LastMethod);
        Assert.Equal("http://localhost:18093/api/v1/sessions/actions/close-stale", handler.LastRequestUri);

        var payload = JsonSerializer.Deserialize<Dictionary<string, object?>>(handler.LastRequestBody!, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal("False", payload!["dryRun"]?.ToString());
        Assert.Equal("stale cleanup", payload["reason"]?.ToString());
        Assert.Equal("45", payload["staleAfterMinutes"]?.ToString());
    }

    /// <summary>
    /// Verifies the command-dispatch route and payload.
    /// </summary>
    [Fact]
    public async Task DispatchCommandAsync_SendsExpectedRouteAndPayload()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var request = new SessionCommandDispatchRequestViewModel
        {
            CommandId = "cmd-1",
            Channel = "Hid",
            Action = "keyboard.shortcut",
            Args = new Dictionary<string, object?>
            {
                ["shortcut"] = "CTRL+ALT+M",
                ["principalId"] = "operator-a",
                ["participantId"] = "participant-1",
            },
            TimeoutMs = 3000,
            IdempotencyKey = "idem-1",
            TenantId = "local-tenant",
            OrganizationId = "local-org",
            OperatorRoles = ["operator.moderator"],
        };
        var handler = new RecordingHandler(new CommandAckViewModel("cmd-1", "Applied"));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:18093") };
        var apiClient = new ControlPlaneApiClient(httpClient);

        _ = await apiClient.DispatchCommandAsync("session-1", request, cancellationToken);

        Assert.Equal(HttpMethod.Post, handler.LastMethod);
        Assert.Equal("http://localhost:18093/api/v1/sessions/session-1/commands", handler.LastRequestUri);

        var payload = JsonSerializer.Deserialize<Dictionary<string, object?>>(handler.LastRequestBody!, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal("cmd-1", payload!["commandId"]?.ToString());
        Assert.Equal("keyboard.shortcut", payload["action"]?.ToString());
        Assert.Equal("Hid", payload["channel"]?.ToString());
    }

    /// <summary>
    /// Verifies the request-control action route and payload.
    /// </summary>
    [Fact]
    public async Task RequestControlAsync_SendsExpectedRouteAndPayload()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var handler = new RecordingHandler(new SessionControlLeaseViewModel("participant-1", "operator-a", "operator-a", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddSeconds(30)));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:18093") };
        var apiClient = new ControlPlaneApiClient(httpClient);

        _ = await apiClient.RequestControlAsync("session-1", "participant-1", "operator-a", 30, "need control", cancellationToken);

        Assert.Equal(HttpMethod.Post, handler.LastMethod);
        Assert.Equal("http://localhost:18093/api/v1/sessions/session-1/control/request", handler.LastRequestUri);

        var payload = JsonSerializer.Deserialize<Dictionary<string, object?>>(handler.LastRequestBody!, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal("participant-1", payload!["participantId"]?.ToString());
        Assert.Equal("operator-a", payload["requestedBy"]?.ToString());
    }

    /// <summary>
    /// Verifies the control-ensure route and payload.
    /// </summary>
    [Fact]
    public async Task EnsureControlAsync_SendsExpectedRouteAndPayload()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var request = new SessionControlEnsureRequestViewModel
        {
            ParticipantId = "participant-1",
            RequestedBy = "operator-a",
            EndpointId = "endpoint-1",
            Profile = "UltraLowLatency",
            LeaseSeconds = 45,
            Reason = "need control",
            AutoCreateSessionIfMissing = true,
            PreferLiveRelaySession = true,
            TenantId = "local-tenant",
            OrganizationId = "local-org",
            OperatorRoles = ["operator.viewer", "operator.moderator"],
        };
        var handler = new RecordingHandler(new SessionControlEnsureResultViewModel(
            RequestedSessionId: "session-1",
            EffectiveSessionId: "session-1",
            Lease: new SessionControlLeaseViewModel("participant-1", "operator-a", "operator-a", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddSeconds(45)),
            SessionCreated: false,
            SessionReused: false,
            ResolvedAtUtc: DateTimeOffset.UtcNow));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:18093") };
        var apiClient = new ControlPlaneApiClient(httpClient);

        var response = await apiClient.EnsureControlAsync("session-1", request, cancellationToken);

        Assert.Equal(HttpMethod.Post, handler.LastMethod);
        Assert.Equal("http://localhost:18093/api/v1/sessions/session-1/control/ensure", handler.LastRequestUri);
        Assert.NotNull(response);
        Assert.Equal("session-1", response!.EffectiveSessionId);
        Assert.False(response.SessionCreated);
        Assert.False(response.SessionReused);

        var payload = JsonSerializer.Deserialize<Dictionary<string, object?>>(handler.LastRequestBody!, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal("participant-1", payload!["participantId"]?.ToString());
        Assert.Equal("endpoint-1", payload["endpointId"]?.ToString());
        Assert.Equal("True", payload["autoCreateSessionIfMissing"]?.ToString());
    }

    /// <summary>
    /// Verifies the invitation-approval route and payload.
    /// </summary>
    [Fact]
    public async Task ApproveInvitationAsync_SendsExpectedRouteAndPayload()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var handler = new RecordingHandler(new SessionShareViewModel("share-1", "user-a", "moderator-a", "Controller", "Pending", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:18093") };
        var apiClient = new ControlPlaneApiClient(httpClient);

        _ = await apiClient.ApproveInvitationAsync("session-1", "share-1", "moderator-a", "Controller", "approved", cancellationToken);

        Assert.Equal(HttpMethod.Post, handler.LastMethod);
        Assert.Equal("http://localhost:18093/api/v1/sessions/session-1/invitations/share-1/approve", handler.LastRequestUri);

        var payload = JsonSerializer.Deserialize<Dictionary<string, object?>>(handler.LastRequestBody!, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal("share-1", payload!["shareId"]?.ToString());
        Assert.Equal("moderator-a", payload["actedBy"]?.ToString());
        Assert.Equal("Controller", payload["grantedRole"]?.ToString());
    }

    /// <summary>
    /// Verifies the share-grant route and payload.
    /// </summary>
    [Fact]
    public async Task CreateShareAsync_SendsExpectedRouteAndPayload()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var request = new SessionShareGrantRequestViewModel
        {
            ShareId = "share-1",
            PrincipalId = "viewer@example.com",
            GrantedBy = "moderator-a",
            Role = "Observer",
            TenantId = "local-tenant",
            OrganizationId = "local-org",
            OperatorRoles = ["operator.moderator"],
        };
        var handler = new RecordingHandler(new SessionShareViewModel("share-1", "viewer@example.com", "moderator-a", "Observer", "Pending", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:18093") };
        var apiClient = new ControlPlaneApiClient(httpClient);

        _ = await apiClient.CreateShareAsync("session-1", request, cancellationToken);

        Assert.Equal(HttpMethod.Post, handler.LastMethod);
        Assert.Equal("http://localhost:18093/api/v1/sessions/session-1/shares", handler.LastRequestUri);

        var payload = JsonSerializer.Deserialize<Dictionary<string, object?>>(handler.LastRequestBody!, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal("share-1", payload!["shareId"]?.ToString());
        Assert.Equal("viewer@example.com", payload["principalId"]?.ToString());
        Assert.Equal("moderator-a", payload["grantedBy"]?.ToString());
    }

    /// <summary>
    /// Verifies the invitation-request route and payload.
    /// </summary>
    [Fact]
    public async Task RequestInvitationAsync_SendsExpectedRouteAndPayload()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var request = new SessionInvitationRequestViewModel
        {
            ShareId = "share-2",
            PrincipalId = "viewer@example.com",
            RequestedBy = "viewer@example.com",
            RequestedRole = "Observer",
            Message = "join room",
        };
        var handler = new RecordingHandler(new SessionShareViewModel("share-2", "viewer@example.com", "viewer@example.com", "Observer", "Requested", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:18093") };
        var apiClient = new ControlPlaneApiClient(httpClient);

        _ = await apiClient.RequestInvitationAsync("session-1", request, cancellationToken);

        Assert.Equal(HttpMethod.Post, handler.LastMethod);
        Assert.Equal("http://localhost:18093/api/v1/sessions/session-1/invitations/requests", handler.LastRequestUri);

        var payload = JsonSerializer.Deserialize<Dictionary<string, object?>>(handler.LastRequestBody!, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal("share-2", payload!["shareId"]?.ToString());
        Assert.Equal("viewer@example.com", payload["principalId"]?.ToString());
        Assert.Equal("viewer@example.com", payload["requestedBy"]?.ToString());
    }

    /// <summary>
    /// Verifies the share-accept route and payload.
    /// </summary>
    [Fact]
    public async Task AcceptShareAsync_SendsExpectedRouteAndPayload()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var handler = new RecordingHandler(new SessionShareViewModel("share-3", "viewer@example.com", "moderator-a", "Observer", "Accepted", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:18093") };
        var apiClient = new ControlPlaneApiClient(httpClient);

        _ = await apiClient.AcceptShareAsync("session-1", "share-3", "viewer@example.com", "accepted", "local-tenant", "local-org", ["operator.viewer"], cancellationToken);

        Assert.Equal(HttpMethod.Post, handler.LastMethod);
        Assert.Equal("http://localhost:18093/api/v1/sessions/session-1/shares/share-3/accept", handler.LastRequestUri);

        var payload = JsonSerializer.Deserialize<Dictionary<string, object?>>(handler.LastRequestBody!, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal("share-3", payload!["shareId"]?.ToString());
        Assert.Equal("viewer@example.com", payload["actedBy"]?.ToString());
        Assert.Equal("local-tenant", payload["tenantId"]?.ToString());
    }

    /// <summary>
    /// Verifies the share-reject route and payload.
    /// </summary>
    [Fact]
    public async Task RejectShareAsync_SendsExpectedRouteAndPayload()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var handler = new RecordingHandler(new SessionShareViewModel("share-4", "viewer@example.com", "moderator-a", "Observer", "Rejected", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:18093") };
        var apiClient = new ControlPlaneApiClient(httpClient);

        _ = await apiClient.RejectShareAsync("session-1", "share-4", "viewer@example.com", "rejected", "local-tenant", "local-org", ["operator.viewer"], cancellationToken);

        Assert.Equal(HttpMethod.Post, handler.LastMethod);
        Assert.Equal("http://localhost:18093/api/v1/sessions/session-1/shares/share-4/reject", handler.LastRequestUri);

        var payload = JsonSerializer.Deserialize<Dictionary<string, object?>>(handler.LastRequestBody!, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal("share-4", payload!["shareId"]?.ToString());
        Assert.Equal("viewer@example.com", payload["actedBy"]?.ToString());
        Assert.Equal("local-org", payload["organizationId"]?.ToString());
    }

    /// <summary>
    /// Verifies the share-revoke route and payload.
    /// </summary>
    [Fact]
    public async Task RevokeShareAsync_SendsExpectedRouteAndPayload()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var handler = new RecordingHandler(new SessionShareViewModel("share-5", "viewer@example.com", "moderator-a", "Observer", "Revoked", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:18093") };
        var apiClient = new ControlPlaneApiClient(httpClient);

        _ = await apiClient.RevokeShareAsync("session-1", "share-5", "moderator-a", "revoked", "local-tenant", "local-org", ["operator.moderator"], cancellationToken);

        Assert.Equal(HttpMethod.Post, handler.LastMethod);
        Assert.Equal("http://localhost:18093/api/v1/sessions/session-1/shares/share-5/revoke", handler.LastRequestUri);

        var payload = JsonSerializer.Deserialize<Dictionary<string, object?>>(handler.LastRequestBody!, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal("share-5", payload!["shareId"]?.ToString());
        Assert.Equal("moderator-a", payload["actedBy"]?.ToString());
        Assert.Equal("revoked", payload["reason"]?.ToString());
    }

    /// <summary>
    /// Verifies the participant-activity route.
    /// </summary>
    [Fact]
    public async Task GetParticipantActivityAsync_TargetsExpectedRoute()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var handler = new RecordingHandler(Array.Empty<ParticipantActivityViewModel>());
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:18093") };
        var apiClient = new ControlPlaneApiClient(httpClient);

        _ = await apiClient.GetParticipantActivityAsync("session-1", cancellationToken);

        Assert.Equal(HttpMethod.Get, handler.LastMethod);
        Assert.Equal("http://localhost:18093/api/v1/collaboration/sessions/session-1/participants/activity", handler.LastRequestUri);
    }

    /// <summary>
    /// Verifies the operator-timeline route.
    /// </summary>
    [Fact]
    public async Task GetOperatorTimelineAsync_TargetsExpectedRoute()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var handler = new RecordingHandler(new OperatorTimelineViewModel("session-1", 0, Array.Empty<TimelineEntryViewModel>()));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:18093") };
        var apiClient = new ControlPlaneApiClient(httpClient);

        _ = await apiClient.GetOperatorTimelineAsync("session-1", 20, cancellationToken);

        Assert.Equal(HttpMethod.Get, handler.LastMethod);
        Assert.Equal("http://localhost:18093/api/v1/collaboration/sessions/session-1/operators/timeline?take=20", handler.LastRequestUri);
    }

    /// <summary>
    /// Verifies the transport-health route.
    /// </summary>
    [Fact]
    public async Task GetSessionTransportHealthAsync_TargetsExpectedRoute()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var handler = new RecordingHandler(new SessionTransportHealthViewModel(
            "session-1",
            "agent-1",
            "endpoint-1",
            "Uart",
            "default",
            true,
            "ok",
            new Dictionary<string, object?>(),
            LastCommandAck: null,
            ReportedAtUtc: DateTimeOffset.UtcNow,
            OnlinePeerCount: 1,
            LastPeerSeenAtUtc: DateTimeOffset.UtcNow,
            LastPeerState: "Connected",
            LastPeerFailureReason: null,
            LastPeerConsecutiveFailures: 0,
            LastPeerReconnectBackoffMs: 0,
            LastRelayAckAtUtc: DateTimeOffset.UtcNow));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:18093") };
        var apiClient = new ControlPlaneApiClient(httpClient);

        var response = await apiClient.GetSessionTransportHealthAsync("session-1", cancellationToken: cancellationToken);

        Assert.Equal(HttpMethod.Get, handler.LastMethod);
        Assert.Equal("http://localhost:18093/api/v1/sessions/session-1/transport/health", handler.LastRequestUri);
        Assert.NotNull(response);
        Assert.Equal(1, response!.OnlinePeerCount);
        Assert.Equal("Connected", response.LastPeerState);
    }

    /// <summary>
    /// Verifies the transport-health provider query parameter.
    /// </summary>
    [Fact]
    public async Task GetSessionTransportHealthAsync_AppendsProviderQuery()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var handler = new RecordingHandler(new SessionTransportHealthViewModel(
            "session-1",
            "agent-1",
            "endpoint-1",
            "WebRtcDataChannel",
            "endpoint-override",
            true,
            "ok",
            new Dictionary<string, object?>()));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:18093") };
        var apiClient = new ControlPlaneApiClient(httpClient);

        _ = await apiClient.GetSessionTransportHealthAsync("session-1", "webrtc-datachannel", cancellationToken);

        Assert.Equal(HttpMethod.Get, handler.LastMethod);
        Assert.Equal("http://localhost:18093/api/v1/sessions/session-1/transport/health?provider=webrtc-datachannel", handler.LastRequestUri);
    }

    /// <summary>
    /// Verifies the transport-readiness route.
    /// </summary>
    [Fact]
    public async Task GetSessionTransportReadinessAsync_TargetsExpectedRoute()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var handler = new RecordingHandler(new SessionTransportReadinessViewModel(
            SessionId: "session-1",
            AgentId: "agent-1",
            EndpointId: "endpoint-1",
            Provider: "WebRtcDataChannel",
            ProviderSource: "request",
            Ready: true,
            ReasonCode: "ready",
            Reason: "WebRTC relay route is ready.",
            Connected: true,
            OnlinePeerCount: 1,
            LastPeerState: "Connected",
            LastPeerFailureReason: null,
            LastPeerSeenAtUtc: DateTimeOffset.UtcNow,
            LastRelayAckAtUtc: DateTimeOffset.UtcNow,
            Metrics: new Dictionary<string, object?>(),
            EvaluatedAtUtc: DateTimeOffset.UtcNow));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:18093") };
        var apiClient = new ControlPlaneApiClient(httpClient);

        var response = await apiClient.GetSessionTransportReadinessAsync("session-1", cancellationToken: cancellationToken);

        Assert.Equal(HttpMethod.Get, handler.LastMethod);
        Assert.Equal("http://localhost:18093/api/v1/sessions/session-1/transport/readiness", handler.LastRequestUri);
        Assert.NotNull(response);
        Assert.True(response!.Ready);
        Assert.Equal("ready", response.ReasonCode);
    }

    /// <summary>
    /// Verifies the transport-readiness provider query parameter.
    /// </summary>
    [Fact]
    public async Task GetSessionTransportReadinessAsync_AppendsProviderQuery()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var handler = new RecordingHandler(new SessionTransportReadinessViewModel(
            SessionId: "session-1",
            AgentId: "agent-1",
            EndpointId: "endpoint-1",
            Provider: "WebRtcDataChannel",
            ProviderSource: "request",
            Ready: true,
            ReasonCode: "ready",
            Reason: "WebRTC relay route is ready.",
            Connected: true,
            OnlinePeerCount: 1));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:18093") };
        var apiClient = new ControlPlaneApiClient(httpClient);

        _ = await apiClient.GetSessionTransportReadinessAsync("session-1", "webrtc-datachannel", cancellationToken);

        Assert.Equal(HttpMethod.Get, handler.LastMethod);
        Assert.Equal("http://localhost:18093/api/v1/sessions/session-1/transport/readiness?provider=webrtc-datachannel", handler.LastRequestUri);
    }

    /// <summary>
    /// Verifies the transport media-stream list route.
    /// </summary>
    [Fact]
    public async Task GetSessionMediaStreamsAsync_TargetsExpectedRoute()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var handler = new RecordingHandler(new[]
        {
            new SessionMediaStreamSnapshotViewModel(
                SessionId: "session-1",
                PeerId: "peer-1",
                EndpointId: "endpoint-1",
                StreamId: "stream-main",
                Ready: true,
                State: "Ready",
                ReportedAtUtc: DateTimeOffset.UtcNow,
                UpdatedAtUtc: DateTimeOffset.UtcNow,
                Source: "hdmi-usb-capture"),
        });
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:18093") };
        var apiClient = new ControlPlaneApiClient(httpClient);

        var response = await apiClient.GetSessionMediaStreamsAsync("session-1", cancellationToken: cancellationToken);

        Assert.Equal(HttpMethod.Get, handler.LastMethod);
        Assert.Equal("http://localhost:18093/api/v1/sessions/session-1/transport/media/streams", handler.LastRequestUri);
        Assert.NotNull(response);
        Assert.Single(response!);
        Assert.Equal("stream-main", response[0].StreamId);
    }

    /// <summary>
    /// Verifies the transport media-stream list query filters.
    /// </summary>
    [Fact]
    public async Task GetSessionMediaStreamsAsync_AppendsPeerAndEndpointFilters()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var handler = new RecordingHandler(Array.Empty<SessionMediaStreamSnapshotViewModel>());
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:18093") };
        var apiClient = new ControlPlaneApiClient(httpClient);

        _ = await apiClient.GetSessionMediaStreamsAsync("session-1", peerId: "peer-1", endpointId: "endpoint-1", cancellationToken);

        Assert.Equal(HttpMethod.Get, handler.LastMethod);
        Assert.Equal("http://localhost:18093/api/v1/sessions/session-1/transport/media/streams?peerId=peer-1&endpointId=endpoint-1", handler.LastRequestUri);
    }

    /// <summary>
    /// Verifies the WebRTC signaling list route and query parameters.
    /// </summary>
    [Fact]
    public async Task GetWebRtcSignalsAsync_TargetsExpectedRouteAndQuery()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var handler = new RecordingHandler(new[]
        {
            new WebRtcSignalMessageViewModel("session-1", 10, "Offer", "peer-a", "peer-b", "{ }"),
        });
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:18093") };
        var apiClient = new ControlPlaneApiClient(httpClient);

        _ = await apiClient.GetWebRtcSignalsAsync("session-1", "peer-b", 9, 25, cancellationToken);

        Assert.Equal(HttpMethod.Get, handler.LastMethod);
        Assert.Equal("http://localhost:18093/api/v1/sessions/session-1/transport/webrtc/signals?recipientPeerId=peer-b&afterSequence=9&limit=25", handler.LastRequestUri);
    }

    /// <summary>
    /// Verifies the WebRTC signaling publish route and payload.
    /// </summary>
    [Fact]
    public async Task PublishWebRtcSignalAsync_SendsExpectedRouteAndPayload()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var request = new WebRtcSignalPublishRequestViewModel
        {
            Kind = "IceCandidate",
            SenderPeerId = "peer-a",
            RecipientPeerId = "peer-b",
            Payload = "{\"candidate\":\"cand\"}",
            Mid = "0",
            MLineIndex = 0,
        };
        var handler = new RecordingHandler(new WebRtcSignalMessageViewModel(
            "session-1",
            11,
            "IceCandidate",
            "peer-a",
            "peer-b",
            "{\"candidate\":\"cand\"}",
            "0",
            0));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:18093") };
        var apiClient = new ControlPlaneApiClient(httpClient);

        _ = await apiClient.PublishWebRtcSignalAsync("session-1", request, cancellationToken);

        Assert.Equal(HttpMethod.Post, handler.LastMethod);
        Assert.Equal("http://localhost:18093/api/v1/sessions/session-1/transport/webrtc/signals", handler.LastRequestUri);

        var payload = JsonSerializer.Deserialize<Dictionary<string, object?>>(handler.LastRequestBody!, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal("IceCandidate", payload!["kind"]?.ToString());
        Assert.Equal("peer-a", payload["senderPeerId"]?.ToString());
        Assert.Equal("peer-b", payload["recipientPeerId"]?.ToString());
    }

    /// <summary>
    /// Verifies the WebRTC relay peer-online route and payload.
    /// </summary>
    [Fact]
    public async Task MarkWebRtcPeerOnlineAsync_SendsExpectedRouteAndPayload()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var request = new WebRtcPeerPresenceRequestViewModel
        {
            EndpointId = "endpoint-1",
            Metadata = new Dictionary<string, string> { ["client"] = "web-shell" },
        };
        var handler = new RecordingHandler(new WebRtcPeerStateViewModel(
            "session-1",
            "peer-a",
            "endpoint-1",
            true,
            DateTimeOffset.UtcNow,
            request.Metadata));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:18093") };
        var apiClient = new ControlPlaneApiClient(httpClient);

        _ = await apiClient.MarkWebRtcPeerOnlineAsync("session-1", "peer-a", request, cancellationToken);

        Assert.Equal(HttpMethod.Post, handler.LastMethod);
        Assert.Equal("http://localhost:18093/api/v1/sessions/session-1/transport/webrtc/peers/peer-a/online", handler.LastRequestUri);

        var payload = JsonSerializer.Deserialize<Dictionary<string, object?>>(handler.LastRequestBody!, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal("endpoint-1", payload!["endpointId"]?.ToString());
    }

    /// <summary>
    /// Verifies the WebRTC relay peer-offline route.
    /// </summary>
    [Fact]
    public async Task MarkWebRtcPeerOfflineAsync_TargetsExpectedRoute()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var handler = new RecordingHandler(new WebRtcPeerStateViewModel(
            "session-1",
            "peer-a",
            "endpoint-1",
            false,
            DateTimeOffset.UtcNow));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:18093") };
        var apiClient = new ControlPlaneApiClient(httpClient);

        _ = await apiClient.MarkWebRtcPeerOfflineAsync("session-1", "peer-a", cancellationToken);

        Assert.Equal(HttpMethod.Post, handler.LastMethod);
        Assert.Equal("http://localhost:18093/api/v1/sessions/session-1/transport/webrtc/peers/peer-a/offline", handler.LastRequestUri);
    }

    /// <summary>
    /// Verifies the WebRTC relay peers list route.
    /// </summary>
    [Fact]
    public async Task GetWebRtcPeersAsync_TargetsExpectedRoute()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var handler = new RecordingHandler(new[]
        {
            new WebRtcPeerStateViewModel("session-1", "peer-a", "endpoint-1", true, DateTimeOffset.UtcNow),
        });
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:18093") };
        var apiClient = new ControlPlaneApiClient(httpClient);

        _ = await apiClient.GetWebRtcPeersAsync("session-1", cancellationToken);

        Assert.Equal(HttpMethod.Get, handler.LastMethod);
        Assert.Equal("http://localhost:18093/api/v1/sessions/session-1/transport/webrtc/peers", handler.LastRequestUri);
    }

    /// <summary>
    /// Verifies the WebRTC relay command queue route and query parameters.
    /// </summary>
    [Fact]
    public async Task GetWebRtcCommandsAsync_TargetsExpectedRouteAndQuery()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var handler = new RecordingHandler(new[]
        {
            new WebRtcCommandEnvelopeViewModel(
                "session-1",
                5,
                "endpoint-1",
                "peer-a",
                new WebRtcRelayCommandRequestViewModel
                {
                    CommandId = "cmd-1",
                    SessionId = "session-1",
                    Channel = "Hid",
                    Action = "keyboard.text",
                    Args = new Dictionary<string, object?>(),
                    TimeoutMs = 1500,
                    IdempotencyKey = "idem-1",
                },
                DateTimeOffset.UtcNow),
        });
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:18093") };
        var apiClient = new ControlPlaneApiClient(httpClient);

        _ = await apiClient.GetWebRtcCommandsAsync("session-1", "peer-a", 4, 20, cancellationToken);

        Assert.Equal(HttpMethod.Get, handler.LastMethod);
        Assert.Equal("http://localhost:18093/api/v1/sessions/session-1/transport/webrtc/commands?peerId=peer-a&afterSequence=4&limit=20", handler.LastRequestUri);
    }

    /// <summary>
    /// Verifies the WebRTC relay command-ack route and payload.
    /// </summary>
    [Fact]
    public async Task PublishWebRtcCommandAckAsync_SendsExpectedRouteAndPayload()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var request = new WebRtcCommandAckPublishRequestViewModel
        {
            Status = "Applied",
            Metrics = new Dictionary<string, double> { ["rttMs"] = 12.5 },
        };
        var handler = new RecordingHandler(new WebRtcCommandAckPublishResultViewModel("session-1", "cmd-1", true));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:18093") };
        var apiClient = new ControlPlaneApiClient(httpClient);

        _ = await apiClient.PublishWebRtcCommandAckAsync("session-1", "cmd-1", request, cancellationToken);

        Assert.Equal(HttpMethod.Post, handler.LastMethod);
        Assert.Equal("http://localhost:18093/api/v1/sessions/session-1/transport/webrtc/commands/cmd-1/ack", handler.LastRequestUri);

        var payload = JsonSerializer.Deserialize<Dictionary<string, object?>>(handler.LastRequestBody!, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal("Applied", payload!["status"]?.ToString());
    }

    /// <summary>
    /// Verifies the policy-scopes route.
    /// </summary>
    [Fact]
    public async Task GetPolicyScopesAsync_TargetsExpectedRoute()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var handler = new RecordingHandler(new[]
        {
            new PolicyScopeViewModel("scope-local", "local-tenant", "local-org", true, true, true, DateTimeOffset.UtcNow, true),
        });
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:18093") };
        var apiClient = new ControlPlaneApiClient(httpClient);

        _ = await apiClient.GetPolicyScopesAsync(cancellationToken);

        Assert.Equal(HttpMethod.Get, handler.LastMethod);
        Assert.Equal("http://localhost:18093/api/v1/diagnostics/policies/scopes", handler.LastRequestUri);
    }

    /// <summary>
    /// Verifies the policy-scope upsert route and payload.
    /// </summary>
    [Fact]
    public async Task UpsertPolicyScopeAsync_SendsExpectedRouteAndPayload()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var request = new PolicyScopeUpsertRequestViewModel("scope-local", "local-tenant", "local-org", true, true, true, true);
        var handler = new RecordingHandler(new PolicyScopeViewModel("scope-local", "local-tenant", "local-org", true, true, true, DateTimeOffset.UtcNow, true));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:18093") };
        var apiClient = new ControlPlaneApiClient(httpClient);

        _ = await apiClient.UpsertPolicyScopeAsync(request, cancellationToken);

        Assert.Equal(HttpMethod.Post, handler.LastMethod);
        Assert.Equal("http://localhost:18093/api/v1/diagnostics/policies/scopes", handler.LastRequestUri);

        var payload = JsonSerializer.Deserialize<Dictionary<string, object?>>(handler.LastRequestBody!, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal("scope-local", payload!["scopeId"]?.ToString());
        Assert.Equal("local-tenant", payload["tenantId"]?.ToString());
        Assert.Equal("True", payload["isActive"]?.ToString());
    }

    /// <summary>
    /// Verifies the policy-scope deactivate route.
    /// </summary>
    [Fact]
    public async Task DeactivatePolicyScopeAsync_SendsExpectedRoute()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var handler = new RecordingHandler(new PolicyScopeViewModel("scope-local", "local-tenant", "local-org", true, true, true, DateTimeOffset.UtcNow, false));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:18093") };
        var apiClient = new ControlPlaneApiClient(httpClient);

        _ = await apiClient.DeactivatePolicyScopeAsync("scope-local", cancellationToken);

        Assert.Equal(HttpMethod.Post, handler.LastMethod);
        Assert.Equal("http://localhost:18093/api/v1/diagnostics/policies/scopes/scope-local/deactivate", handler.LastRequestUri);
    }

    /// <summary>
    /// Verifies the policy-scope activate route.
    /// </summary>
    [Fact]
    public async Task ActivatePolicyScopeAsync_SendsExpectedRoute()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var handler = new RecordingHandler(new PolicyScopeViewModel("scope-local", "local-tenant", "local-org", true, true, true, DateTimeOffset.UtcNow, true));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:18093") };
        var apiClient = new ControlPlaneApiClient(httpClient);

        _ = await apiClient.ActivatePolicyScopeAsync("scope-local", cancellationToken);

        Assert.Equal(HttpMethod.Post, handler.LastMethod);
        Assert.Equal("http://localhost:18093/api/v1/diagnostics/policies/scopes/scope-local/activate", handler.LastRequestUri);
    }

    /// <summary>
    /// Verifies the policy-scope delete route.
    /// </summary>
    [Fact]
    public async Task DeletePolicyScopeAsync_TargetsExpectedRoute()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var handler = new RecordingHandler(new { });
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:18093") };
        var apiClient = new ControlPlaneApiClient(httpClient);

        await apiClient.DeletePolicyScopeAsync("scope-local", cancellationToken);

        Assert.Equal(HttpMethod.Delete, handler.LastMethod);
        Assert.Equal("http://localhost:18093/api/v1/diagnostics/policies/scopes/scope-local", handler.LastRequestUri);
    }

    /// <summary>
    /// Verifies the release-control action route and payload.
    /// </summary>
    [Fact]
    public async Task ReleaseControlAsync_SendsExpectedRouteAndPayload()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var handler = new RecordingHandler(new SessionControlLeaseViewModel("participant-1", "operator-a", "operator-a", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddSeconds(30)));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:18093") };
        var apiClient = new ControlPlaneApiClient(httpClient);

        _ = await apiClient.ReleaseControlAsync("session-1", "participant-1", "operator-a", "release", cancellationToken);

        Assert.Equal(HttpMethod.Post, handler.LastMethod);
        Assert.Equal("http://localhost:18093/api/v1/sessions/session-1/control/release", handler.LastRequestUri);

        var payload = JsonSerializer.Deserialize<Dictionary<string, object?>>(handler.LastRequestBody!, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal("participant-1", payload!["participantId"]?.ToString());
        Assert.Equal("operator-a", payload["actedBy"]?.ToString());
    }

    /// <summary>
    /// Verifies the force-takeover action route and payload.
    /// </summary>
    [Fact]
    public async Task ForceTakeoverControlAsync_SendsExpectedRouteAndPayload()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var handler = new RecordingHandler(new SessionControlLeaseViewModel("participant-2", "owner-a", "owner-a", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddSeconds(30)));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:18093") };
        var apiClient = new ControlPlaneApiClient(httpClient);

        _ = await apiClient.ForceTakeoverControlAsync("session-1", "participant-2", "owner-a", 30, "takeover", cancellationToken);

        Assert.Equal(HttpMethod.Post, handler.LastMethod);
        Assert.Equal("http://localhost:18093/api/v1/sessions/session-1/control/force-takeover", handler.LastRequestUri);

        var payload = JsonSerializer.Deserialize<Dictionary<string, object?>>(handler.LastRequestBody!, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal("participant-2", payload!["participantId"]?.ToString());
        Assert.Equal("owner-a", payload["grantedBy"]?.ToString());
    }

    /// <summary>
    /// Verifies that control-arbitration conflicts are parsed into a typed conflict payload.
    /// </summary>
    [Fact]
    public async Task RequestControlAsync_ParsesControlArbitrationConflict()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var handler = new RecordingHandler(
            HttpStatusCode.Conflict,
            new ControlArbitrationConflictViewModel(
                "session-1",
                "E_CONTROL_LEASE_HELD_BY_OTHER",
                "Session session-1 is currently controlled by owner@example.com.",
                "participant-2",
                "viewer@example.com",
                new ControlArbitrationCurrentControllerViewModel("participant-1", "owner@example.com", DateTimeOffset.UtcNow.AddSeconds(10))));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:18093") };
        var apiClient = new ControlPlaneApiClient(httpClient);

        var exception = await Assert.ThrowsAsync<ControlPlaneApiException>(() =>
            apiClient.RequestControlAsync("session-1", "participant-2", "viewer@example.com", 30, "need control", cancellationToken));

        Assert.Equal(HttpStatusCode.Conflict, exception.StatusCode);
        Assert.NotNull(exception.ControlConflict);
        Assert.Equal("E_CONTROL_LEASE_HELD_BY_OTHER", exception.ControlConflict!.Code);
        Assert.Equal("participant-1", exception.ControlConflict.CurrentController?.ParticipantId);
        Assert.Null(exception.AuthorizationDenied);
    }

    /// <summary>
    /// Verifies that structured backend denials are surfaced as typed client exceptions.
    /// </summary>
    [Fact]
    public async Task RequestControlAsync_ParsesStructuredAuthorizationDenial()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var handler = new RecordingHandler(
            HttpStatusCode.Forbidden,
            new AuthorizationDeniedViewModel(
                403,
                "moderation_access_required",
                "Caller does not have an operator role that grants moderation access.",
                "session-1",
                null,
                null,
                ["operator.moderator", "operator.admin"],
                "tenant-a",
                "org-a",
                new AuthorizationDeniedCallerViewModel("user-1", "viewer@example.com", "tenant-a", "org-a", ["operator.viewer"])));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:18093") };
        var apiClient = new ControlPlaneApiClient(httpClient);

        var exception = await Assert.ThrowsAsync<ControlPlaneApiException>(() =>
            apiClient.RequestControlAsync("session-1", "participant-1", "viewer@example.com", 30, "need control", cancellationToken));

        Assert.Equal(HttpStatusCode.Forbidden, exception.StatusCode);
        Assert.NotNull(exception.AuthorizationDenied);
        Assert.Equal("moderation_access_required", exception.AuthorizationDenied!.Code);
        Assert.Contains("operator.moderator", exception.Message);
    }


    /// <summary>
    /// Verifies that structured authentication failures are surfaced as typed client exceptions.
    /// </summary>
    [Fact]
    public async Task RequestControlAsync_ParsesStructuredAuthenticationDenial()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var handler = new RecordingHandler(
            HttpStatusCode.Unauthorized,
            new AuthorizationDeniedViewModel(
                401,
                "authentication_required",
                "Caller must authenticate with a bearer token or an approved caller-scope header set before invoking this API.",
                "session-1",
                null,
                null,
                null,
                null,
                null,
                new AuthorizationDeniedCallerViewModel(null, null, null, null, [])));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:18093") };
        var apiClient = new ControlPlaneApiClient(httpClient);

        var exception = await Assert.ThrowsAsync<ControlPlaneApiException>(() =>
            apiClient.RequestControlAsync("session-1", "participant-1", "viewer@example.com", 30, "need control", cancellationToken));

        Assert.Equal(HttpStatusCode.Unauthorized, exception.StatusCode);
        Assert.NotNull(exception.AuthorizationDenied);
        Assert.Equal("authentication_required", exception.AuthorizationDenied!.Code);
    }

    /// <summary>
    /// Verifies that transport failures surface as a typed unavailable exception.
    /// </summary>
    [Fact]
    public async Task GetInventoryDashboardAsync_ThrowsUnavailableException_WhenBackendIsOffline()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var handler = new ThrowingHandler(new HttpRequestException("Connection refused"));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:18093") };
        var apiClient = new ControlPlaneApiClient(httpClient);

        var exception = await Assert.ThrowsAsync<ControlPlaneApiUnavailableException>(() =>
            apiClient.GetInventoryDashboardAsync(cancellationToken));

        Assert.Contains("/api/v1/dashboards/inventory", exception.Message);
    }

    /// <summary>
    /// Verifies that transport timeouts wrapped as canceled operations are surfaced as unavailable.
    /// </summary>
    [Fact]
    public async Task GetInventoryDashboardAsync_ThrowsUnavailableException_WhenBackendTimeoutIncludesInnerException()
    {
        var cancellationToken = CancellationToken.None;
        var timeoutException = new TaskCanceledException(
            "The operation was canceled.",
            new IOException("Unable to read data from the transport connection."));
        var handler = new ThrowingHandler(timeoutException);
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:18093") };
        var apiClient = new ControlPlaneApiClient(httpClient);

        var exception = await Assert.ThrowsAsync<ControlPlaneApiUnavailableException>(() =>
            apiClient.GetInventoryDashboardAsync(cancellationToken));

        Assert.Contains("/api/v1/dashboards/inventory", exception.Message);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly object _responseBody;
        private readonly HttpStatusCode _statusCode;

        public RecordingHandler(object responseBody)
            : this(HttpStatusCode.OK, responseBody)
        {
        }

        public RecordingHandler(HttpStatusCode statusCode, object responseBody)
        {
            _statusCode = statusCode;
            _responseBody = responseBody;
        }

        public HttpMethod? LastMethod { get; private set; }

        public string? LastRequestUri { get; private set; }

        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastMethod = request.Method;
            LastRequestUri = request.RequestUri?.ToString();
            LastRequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            var response = new HttpResponseMessage(_statusCode)
            {
                Content = JsonContent.Create(_responseBody),
            };

            await Task.CompletedTask;
            return response;
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        private readonly Exception _exception;

        public ThrowingHandler(Exception exception)
        {
            _exception = exception;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromException<HttpResponseMessage>(_exception);
    }
}
