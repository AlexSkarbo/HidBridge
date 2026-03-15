namespace HidBridge.ControlPlane.Api.Endpoints;

/// <summary>
/// Registers health, diagnostics, and runtime metadata endpoints.
/// </summary>
public static class SystemEndpoints
{
    /// <summary>
    /// Maps the system endpoint group onto the API route table.
    /// </summary>
    public static IEndpointRouteBuilder MapSystemEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/health", () => Results.Ok(new
        {
            service = "HidBridge.ControlPlane.Api",
            status = "ok",
            utc = DateTimeOffset.UtcNow,
        }))
        .WithTags(ApiEndpointTags.System)
        .WithSummary("Read basic service liveness information.")
        .WithDescription("Returns a minimal health payload proving that the ControlPlane API process is running.");

        endpoints.MapGet("/", () => Results.Ok(new
        {
            service = "HidBridge.ControlPlane.Api",
            docs = new[]
            {
                "/health",
                "/openapi/v1.json",
                "/scalar",
                "/api/v1/agents",
                "/api/v1/endpoints",
                "/api/v1/sessions",
                "/api/v1/sessions/{sessionId}/transport/health",
                "/api/v1/sessions/{sessionId}/transport/webrtc/signals",
                "/api/v1/sessions/{sessionId}/transport/webrtc/peers",
                "/api/v1/sessions/{sessionId}/transport/webrtc/commands",
                "/api/v1/events/audit",
                "/api/v1/events/telemetry",
                "/api/v1/runtime/uart",
            },
        }))
        .WithTags(ApiEndpointTags.System)
        .WithSummary("Read the API entrypoint document.")
        .WithDescription("Returns the primary discovery links exposed by the current Platform baseline.");

        endpoints.MapGet("/api/v1/architecture/repository-strategy", () => Results.Ok(new
        {
            decision = "keep-legacy-tools-stable-create-new-platform-root",
            rationale = new[]
            {
                "Avoid large risky moves before the new modular core is stable.",
                "Keep existing scripts and relative paths in Tools intact during migration.",
                "Develop the new clean architecture in Platform as a greenfield baseline.",
                "Move or archive legacy code only after parity gates are met.",
            }
        }))
        .WithTags(ApiEndpointTags.System)
        .WithSummary("Read the repository modernization strategy.")
        .WithDescription("Explains why legacy code remains in Tools while the new modular platform is developed in Platform.");

        endpoints.MapGet("/api/v1/runtime/uart", (ApiRuntimeSettings runtimeSettings) => Results.Ok(new
        {
            runtimeSettings.TransportProvider,
            port = runtimeSettings.UartPort,
            baudRate = runtimeSettings.UartBaudRate,
            runtimeSettings.MouseSelector,
            runtimeSettings.KeyboardSelector,
            runtimeSettings.UartCommandTimeoutMs,
            runtimeSettings.UartInjectTimeoutMs,
            runtimeSettings.UartInjectRetries,
            runtimeSettings.UartUsesMasterSecret,
            webRtc = new
            {
                runtimeSettings.WebRtcRequireCapability,
                runtimeSettings.WebRtcEnableConnectorBridge,
                runtimeSettings.TransportFallbackToDefaultOnWebRtcError,
            },
            runtimeSettings.AgentId,
            runtimeSettings.EndpointId,
            runtimeSettings.PersistenceProvider,
            runtimeSettings.DataRoot,
            runtimeSettings.SqlSchema,
            runtimeSettings.SqlApplyMigrations,
            policyBootstrap = new
            {
                runtimeSettings.PolicyBootstrap.ScopeId,
                runtimeSettings.PolicyBootstrap.TenantId,
                runtimeSettings.PolicyBootstrap.OrganizationId,
                runtimeSettings.PolicyBootstrap.ViewerRoleRequired,
                runtimeSettings.PolicyBootstrap.ModeratorOverrideEnabled,
                runtimeSettings.PolicyBootstrap.AdminOverrideEnabled,
                assignmentCount = runtimeSettings.PolicyBootstrap.Assignments.Count,
            },
            authentication = new
            {
                runtimeSettings.Authentication.Enabled,
                runtimeSettings.Authentication.Authority,
                runtimeSettings.Authentication.Audience,
                runtimeSettings.Authentication.RequireHttpsMetadata,
                runtimeSettings.Authentication.AllowHeaderFallback,
                runtimeSettings.Authentication.BearerOnlyPrefixes,
                runtimeSettings.Authentication.CallerContextRequiredPrefixes,
                runtimeSettings.Authentication.HeaderFallbackDisabledPatterns,
                runtimeSettings.Authentication.DefaultTenantId,
                runtimeSettings.Authentication.DefaultOrganizationId,
            },
            policyRevisionLifecycle = new
            {
                maintenanceIntervalSeconds = runtimeSettings.PolicyRevisionLifecycle.MaintenanceInterval.TotalSeconds,
                retentionDays = runtimeSettings.PolicyRevisionLifecycle.Retention.TotalDays,
                runtimeSettings.PolicyRevisionLifecycle.MaxRevisionsPerEntity,
            },
            maintenance = new
            {
                leaseSeconds = runtimeSettings.Maintenance.LeaseDuration.TotalSeconds,
                recoveryGraceSeconds = runtimeSettings.Maintenance.RecoveryGracePeriod.TotalSeconds,
                cleanupIntervalSeconds = runtimeSettings.Maintenance.CleanupInterval.TotalSeconds,
                auditRetentionDays = runtimeSettings.Maintenance.AuditRetention.TotalDays,
                telemetryRetentionDays = runtimeSettings.Maintenance.TelemetryRetention.TotalDays,
            },
        }))
        .WithTags(ApiEndpointTags.System)
        .WithSummary("Read active UART runtime configuration.")
        .WithDescription("Returns the effective transport provider together with UART and WebRTC transport runtime options, logical selector values, and active persistence settings used by the local connector stack.");

        return endpoints;
    }
}
