using HidBridge.Abstractions;
using HidBridge.Contracts;

namespace HidBridge.ControlPlane.Api.Endpoints;

/// <summary>
/// Registers replay-oriented and archive-oriented diagnostics endpoints.
/// </summary>
public static class DiagnosticsEndpoints
{
    /// <summary>
    /// Maps diagnostics endpoints onto the API route table.
    /// </summary>
    /// <param name="endpoints">The route builder to extend.</param>
    /// <returns>The same route builder for chaining.</returns>
    public static IEndpointRouteBuilder MapDiagnosticsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/diagnostics")
            .WithTags(ApiEndpointTags.Diagnostics);

        group.MapGet("/replay/sessions/{sessionId}", async (
            string sessionId,
            int? take,
            HttpContext httpContext,
            ISessionStore sessionStore,
            ReplayArchiveDiagnosticsService service,
            CancellationToken ct) =>
        {
            var caller = ApiCallerContext.FromHttpContext(httpContext);
            try
            {
                caller.EnsureViewerAccess();
                if (caller.IsPresent)
                {
                    await caller.RequireScopedSessionAsync(sessionStore, sessionId, ct);
                }

                return Results.Ok(await service.GetSessionReplayBundleAsync(sessionId, caller, take ?? 100, ct));
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
        .Produces<SessionReplayBundleReadModel>(StatusCodes.Status200OK)
        .WithSummary("Read one replay bundle for a session.")
        .WithDescription("Returns one replay-oriented bundle that combines the persisted session snapshot, audit slice, telemetry slice, command slice, and unified timeline for the specified session.");

        group.MapGet("/archive/summary", async (
            string? sessionId,
            DateTimeOffset? sinceUtc,
            HttpContext httpContext,
            ISessionStore sessionStore,
            ReplayArchiveDiagnosticsService service,
            CancellationToken ct) =>
        {
            var caller = ApiCallerContext.FromHttpContext(httpContext);
            try
            {
                caller.EnsureViewerAccess();
                if (!string.IsNullOrWhiteSpace(sessionId) && caller.IsPresent)
                {
                    await caller.RequireScopedSessionAsync(sessionStore, sessionId, ct);
                }

                return Results.Ok(await service.GetArchiveSummaryAsync(sessionId, caller, sinceUtc, ct));
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
        .Produces<ArchiveDiagnosticsSummaryReadModel>(StatusCodes.Status200OK)
        .WithSummary("Read the archive diagnostics summary.")
        .WithDescription("Returns one archive-oriented summary over audit, telemetry, and command journal data. Optional filters: sessionId and sinceUtc.");

        group.MapGet("/transport/slo", async (
            string? sessionId,
            int? windowMinutes,
            HttpContext httpContext,
            ISessionStore sessionStore,
            TransportSloDiagnosticsService service,
            CancellationToken ct) =>
        {
            var caller = ApiCallerContext.FromHttpContext(httpContext);
            try
            {
                caller.EnsureViewerAccess();
                if (!string.IsNullOrWhiteSpace(sessionId) && caller.IsPresent)
                {
                    await caller.RequireScopedSessionAsync(sessionStore, sessionId, ct);
                }

                return Results.Ok(await service.GetSummaryAsync(caller, sessionId, windowMinutes, ct));
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
        .Produces<TransportSloSummaryReadModel>(StatusCodes.Status200OK)
        .WithSummary("Read transport SLO diagnostics summary.")
        .WithDescription("Returns rolling transport SLO metrics and alerts for relay-ready latency, ACK timeout rate, and reconnect frequency. Optional filters: sessionId and windowMinutes.");

        group.MapGet("/archive/audit", async (
            string? sessionId,
            string? category,
            string? principalId,
            DateTimeOffset? sinceUtc,
            int? skip,
            int? take,
            HttpContext httpContext,
            ISessionStore sessionStore,
            ReplayArchiveDiagnosticsService service,
            CancellationToken ct) =>
        {
            var caller = ApiCallerContext.FromHttpContext(httpContext);
            try
            {
                caller.EnsureViewerAccess();
                if (!string.IsNullOrWhiteSpace(sessionId) && caller.IsPresent)
                {
                    await caller.RequireScopedSessionAsync(sessionStore, sessionId, ct);
                }

                return Results.Ok(await service.QueryArchiveAuditAsync(
                    sessionId,
                    category,
                    principalId,
                    sinceUtc,
                    caller,
                    skip ?? 0,
                    take ?? 100,
                    ct));
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
        .Produces<ProjectionPage<AuditProjectionItemReadModel>>(StatusCodes.Status200OK)
        .WithSummary("Read archive audit slices.")
        .WithDescription("Returns a paged archive-oriented audit slice suitable for diagnostics export and replay preparation. Supports filtering by session, category, principal, and lower time bound.");

        group.MapGet("/archive/telemetry", async (
            string? sessionId,
            string? scope,
            string? metricName,
            DateTimeOffset? sinceUtc,
            int? skip,
            int? take,
            HttpContext httpContext,
            ISessionStore sessionStore,
            ReplayArchiveDiagnosticsService service,
            CancellationToken ct) =>
        {
            var caller = ApiCallerContext.FromHttpContext(httpContext);
            try
            {
                caller.EnsureViewerAccess();
                if (!string.IsNullOrWhiteSpace(sessionId) && caller.IsPresent)
                {
                    await caller.RequireScopedSessionAsync(sessionStore, sessionId, ct);
                }

                return Results.Ok(await service.QueryArchiveTelemetryAsync(
                    sessionId,
                    scope,
                    metricName,
                    sinceUtc,
                    caller,
                    skip ?? 0,
                    take ?? 100,
                    ct));
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
        .Produces<ProjectionPage<TelemetryProjectionItemReadModel>>(StatusCodes.Status200OK)
        .WithSummary("Read archive telemetry slices.")
        .WithDescription("Returns a paged archive-oriented telemetry slice suitable for diagnostics export and replay preparation. Supports filtering by session, scope, metric name, and lower time bound.");

        group.MapGet("/archive/commands", async (
            string? sessionId,
            CommandStatus? status,
            DateTimeOffset? sinceUtc,
            int? skip,
            int? take,
            HttpContext httpContext,
            ISessionStore sessionStore,
            ReplayArchiveDiagnosticsService service,
            CancellationToken ct) =>
        {
            var caller = ApiCallerContext.FromHttpContext(httpContext);
            try
            {
                caller.EnsureViewerAccess();
                if (!string.IsNullOrWhiteSpace(sessionId) && caller.IsPresent)
                {
                    await caller.RequireScopedSessionAsync(sessionStore, sessionId, ct);
                }

                return Results.Ok(await service.QueryArchiveCommandsAsync(
                    sessionId,
                    status,
                    sinceUtc,
                    caller,
                    skip ?? 0,
                    take ?? 100,
                    ct));
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
        .Produces<ProjectionPage<CommandJournalEntryBody>>(StatusCodes.Status200OK)
        .WithSummary("Read archive command slices.")
        .WithDescription("Returns a paged archive-oriented command journal slice suitable for replay and diagnostics export. Supports filtering by session, command status, and lower time bound.");

        group.MapGet("/policies/summary", async (
            HttpContext httpContext,
            PolicyGovernanceDiagnosticsService service,
            CancellationToken ct) =>
        {
            var caller = ApiCallerContext.FromHttpContext(httpContext);
            try
            {
                caller.EnsureViewerAccess();
                return Results.Ok(await service.GetSummaryAsync(caller, ct));
            }
            catch (UnauthorizedAccessException ex)
            {
                return ApiAuthorizationResults.Forbidden(caller, ex);
            }
        })
        .Produces<PolicyDiagnosticsSummaryReadModel>(StatusCodes.Status200OK)
        .WithSummary("Read the policy governance summary.")
        .WithDescription("Returns one aggregate summary over visible policy scopes, assignments, policy revisions, and current retention settings.");

        group.MapPost("/policies/prune", async (
            HttpContext httpContext,
            PolicyRevisionLifecycleService lifecycleService,
            CancellationToken ct) =>
        {
            var caller = ApiCallerContext.FromHttpContext(httpContext);
            try
            {
                caller.EnsureAdminAccess();
                var deleted = await lifecycleService.TrimAsync(ct);
                return Results.Ok(new PolicyRevisionPruneResultReadModel(
                    ExecutedAtUtc: DateTimeOffset.UtcNow,
                    DeletedRevisionCount: deleted,
                    RetentionDays: (int)Math.Round(lifecycleService.Options.Retention.TotalDays),
                    MaxRevisionsPerEntity: lifecycleService.Options.MaxRevisionsPerEntity));
            }
            catch (UnauthorizedAccessException ex)
            {
                return ApiAuthorizationResults.Forbidden(caller, ex);
            }
        })
        .Produces<PolicyRevisionPruneResultReadModel>(StatusCodes.Status200OK)
        .WithSummary("Manually prune policy revision history.")
        .WithDescription("Applies the configured policy revision retention rules immediately and returns the number of deleted revisions. Requires operator.admin.");

        group.MapGet("/policies/revisions", async (
            string? scopeId,
            string? entityType,
            string? principalId,
            int? take,
            HttpContext httpContext,
            IPolicyStore policyStore,
            IPolicyRevisionStore revisionStore,
            CancellationToken ct) =>
        {
            var caller = ApiCallerContext.FromHttpContext(httpContext);
            try
            {
                caller.EnsureViewerAccess();

                var revisions = await revisionStore.ListAsync(ct);
                if (!string.IsNullOrWhiteSpace(scopeId))
                {
                    revisions = revisions
                        .Where(x => string.Equals(x.ScopeId, scopeId, StringComparison.OrdinalIgnoreCase))
                        .ToArray();
                }

                if (!string.IsNullOrWhiteSpace(entityType))
                {
                    revisions = revisions
                        .Where(x => string.Equals(x.EntityType, entityType, StringComparison.OrdinalIgnoreCase))
                        .ToArray();
                }

                if (!string.IsNullOrWhiteSpace(principalId))
                {
                    revisions = revisions
                        .Where(x => string.Equals(x.PrincipalId, principalId, StringComparison.OrdinalIgnoreCase))
                        .ToArray();
                }

                if (caller is { IsPresent: true, IsAdmin: false, HasScope: true })
                {
                    var scopes = await policyStore.ListScopesAsync(ct);
                    var visibleScopeIds = scopes
                        .Where(scope =>
                            (string.IsNullOrWhiteSpace(scope.TenantId)
                                || string.Equals(scope.TenantId, caller.TenantId, StringComparison.OrdinalIgnoreCase))
                            && (string.IsNullOrWhiteSpace(scope.OrganizationId)
                                || string.Equals(scope.OrganizationId, caller.OrganizationId, StringComparison.OrdinalIgnoreCase)))
                        .Select(scope => scope.ScopeId)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);

                    revisions = revisions
                        .Where(revision => visibleScopeIds.Contains(revision.ScopeId))
                        .ToArray();
                }

                return Results.Ok(revisions.Take(take ?? 100).ToArray());
            }
            catch (UnauthorizedAccessException ex)
            {
                return ApiAuthorizationResults.Forbidden(caller, ex);
            }
        })
        .Produces<PolicyRevisionSnapshot[]>(StatusCodes.Status200OK)
        .WithSummary("Read versioned policy revision snapshots.")
        .WithDescription("Returns persisted policy scope and assignment revisions for diagnostics and policy audit. Non-admin callers are filtered by visible tenant/organization policy scope.");

        group.MapGet("/policies/scopes", async (
            HttpContext httpContext,
            PolicyGovernanceManagementService service,
            CancellationToken ct) =>
        {
            var caller = ApiCallerContext.FromHttpContext(httpContext);
            try
            {
                caller.EnsureViewerAccess();
                return Results.Ok(await service.ListScopesAsync(caller, ct));
            }
            catch (UnauthorizedAccessException ex)
            {
                return ApiAuthorizationResults.Forbidden(caller, ex);
            }
        })
        .Produces<PolicyScopeReadModel[]>(StatusCodes.Status200OK)
        .WithSummary("Read visible policy scopes.")
        .WithDescription("Returns visible policy scopes for the current caller. Non-admin callers are scoped by tenant and organization.");

        group.MapGet("/policies/assignments", async (
            HttpContext httpContext,
            PolicyGovernanceManagementService service,
            CancellationToken ct) =>
        {
            var caller = ApiCallerContext.FromHttpContext(httpContext);
            try
            {
                caller.EnsureViewerAccess();
                return Results.Ok(await service.ListAssignmentsAsync(caller, ct));
            }
            catch (UnauthorizedAccessException ex)
            {
                return ApiAuthorizationResults.Forbidden(caller, ex);
            }
        })
        .Produces<PolicyAssignmentReadModel[]>(StatusCodes.Status200OK)
        .WithSummary("Read visible policy assignments.")
        .WithDescription("Returns visible policy assignments for the current caller. Non-admin callers are scoped by tenant and organization.");

        group.MapPost("/policies/scopes", async (
            PolicyScopeUpsertBody body,
            HttpContext httpContext,
            PolicyGovernanceManagementService service,
            CancellationToken ct) =>
        {
            var caller = ApiCallerContext.FromHttpContext(httpContext);
            try
            {
                caller.EnsureAdminAccess();
                return Results.Ok(await service.UpsertScopeAsync(caller, body, ct));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (UnauthorizedAccessException ex)
            {
                return ApiAuthorizationResults.Forbidden(caller, ex);
            }
        })
        .Produces<PolicyScopeReadModel>(StatusCodes.Status200OK)
        .WithSummary("Create or update one policy scope.")
        .WithDescription("Upserts one policy scope, appends one policy revision, and writes one policy audit event. Requires operator.admin.");

        group.MapPost("/policies/scopes/{scopeId}/deactivate", async (
            string scopeId,
            HttpContext httpContext,
            PolicyGovernanceManagementService service,
            CancellationToken ct) =>
        {
            var caller = ApiCallerContext.FromHttpContext(httpContext);
            try
            {
                caller.EnsureAdminAccess();
                return Results.Ok(await service.SetScopeActivationAsync(caller, scopeId, false, ct));
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
            catch (UnauthorizedAccessException ex)
            {
                return ApiAuthorizationResults.Forbidden(caller, ex);
            }
        })
        .Produces<PolicyScopeReadModel>(StatusCodes.Status200OK)
        .WithSummary("Deactivate one policy scope.")
        .WithDescription("Marks one policy scope inactive so its assignments no longer contribute effective caller roles. Requires operator.admin.");

        group.MapPost("/policies/scopes/{scopeId}/activate", async (
            string scopeId,
            HttpContext httpContext,
            PolicyGovernanceManagementService service,
            CancellationToken ct) =>
        {
            var caller = ApiCallerContext.FromHttpContext(httpContext);
            try
            {
                caller.EnsureAdminAccess();
                return Results.Ok(await service.SetScopeActivationAsync(caller, scopeId, true, ct));
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
            catch (UnauthorizedAccessException ex)
            {
                return ApiAuthorizationResults.Forbidden(caller, ex);
            }
        })
        .Produces<PolicyScopeReadModel>(StatusCodes.Status200OK)
        .WithSummary("Activate one policy scope.")
        .WithDescription("Marks one policy scope active so its assignments contribute effective caller roles again. Requires operator.admin.");


        group.MapDelete("/policies/scopes/{scopeId}", async (
            string scopeId,
            HttpContext httpContext,
            PolicyGovernanceManagementService service,
            CancellationToken ct) =>
        {
            var caller = ApiCallerContext.FromHttpContext(httpContext);
            try
            {
                caller.EnsureAdminAccess();
                await service.DeleteScopeAsync(caller, scopeId, ct);
                return Results.NoContent();
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (UnauthorizedAccessException ex)
            {
                return ApiAuthorizationResults.Forbidden(caller, ex);
            }
        })
        .Produces(StatusCodes.Status204NoContent)
        .WithSummary("Delete one policy scope.")
        .WithDescription("Deletes one policy scope after all linked assignments have been removed. Requires operator.admin.");

        group.MapPost("/policies/assignments", async (
            PolicyAssignmentUpsertBody body,
            HttpContext httpContext,
            PolicyGovernanceManagementService service,
            CancellationToken ct) =>
        {
            var caller = ApiCallerContext.FromHttpContext(httpContext);
            try
            {
                caller.EnsureAdminAccess();
                return Results.Ok(await service.UpsertAssignmentAsync(caller, body, ct));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (UnauthorizedAccessException ex)
            {
                return ApiAuthorizationResults.Forbidden(caller, ex);
            }
        })
        .Produces<PolicyAssignmentReadModel>(StatusCodes.Status200OK)
        .WithSummary("Create or update one policy assignment.")
        .WithDescription("Upserts one policy assignment, appends one policy revision, and writes one policy audit event. Requires operator.admin.");

        group.MapPost("/policies/assignments/{assignmentId}/deactivate", async (
            string assignmentId,
            HttpContext httpContext,
            PolicyGovernanceManagementService service,
            CancellationToken ct) =>
        {
            var caller = ApiCallerContext.FromHttpContext(httpContext);
            try
            {
                caller.EnsureAdminAccess();
                return Results.Ok(await service.SetAssignmentActivationAsync(caller, assignmentId, false, ct));
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
            catch (UnauthorizedAccessException ex)
            {
                return ApiAuthorizationResults.Forbidden(caller, ex);
            }
        })
        .Produces<PolicyAssignmentReadModel>(StatusCodes.Status200OK)
        .WithSummary("Deactivate one policy assignment.")
        .WithDescription("Marks one policy assignment inactive so it no longer contributes effective caller roles. Requires operator.admin.");

        group.MapPost("/policies/assignments/{assignmentId}/activate", async (
            string assignmentId,
            HttpContext httpContext,
            PolicyGovernanceManagementService service,
            CancellationToken ct) =>
        {
            var caller = ApiCallerContext.FromHttpContext(httpContext);
            try
            {
                caller.EnsureAdminAccess();
                return Results.Ok(await service.SetAssignmentActivationAsync(caller, assignmentId, true, ct));
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
            catch (UnauthorizedAccessException ex)
            {
                return ApiAuthorizationResults.Forbidden(caller, ex);
            }
        })
        .Produces<PolicyAssignmentReadModel>(StatusCodes.Status200OK)
        .WithSummary("Activate one policy assignment.")
        .WithDescription("Marks one policy assignment active so it contributes effective caller roles again. Requires operator.admin.");

        group.MapDelete("/policies/assignments/{assignmentId}", async (
            string assignmentId,
            HttpContext httpContext,
            PolicyGovernanceManagementService service,
            CancellationToken ct) =>
        {
            var caller = ApiCallerContext.FromHttpContext(httpContext);
            try
            {
                caller.EnsureAdminAccess();
                await service.DeleteAssignmentAsync(caller, assignmentId, ct);
                return Results.NoContent();
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
            catch (UnauthorizedAccessException ex)
            {
                return ApiAuthorizationResults.Forbidden(caller, ex);
            }
        })
        .Produces(StatusCodes.Status204NoContent)
        .WithSummary("Delete one policy assignment.")
        .WithDescription("Deletes one policy assignment, appends one tombstone policy revision, and writes one policy audit event. Requires operator.admin.");

        return endpoints;
    }
}
