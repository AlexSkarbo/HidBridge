using HidBridge.Abstractions;
using HidBridge.Application;
using HidBridge.Connectors.HidBridgeUart;
using HidBridge.ControlPlane.Api;
using HidBridge.ControlPlane.Api.Endpoints;
using HidBridge.ConnectorHost;
using HidBridge.Persistence;
using HidBridge.Persistence.Sql;
using HidBridge.SessionOrchestrator;
using HidBridge.Transport.Uart;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Scalar.AspNetCore;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi("v1");
builder.Services.ConfigureHttpJsonOptions(options =>
{
    // Keep transport payloads human-readable and compatible with client JSON examples.
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

var dataRoot = builder.Configuration["HIDBRIDGE_DATA_ROOT"]
    ?? Path.Combine(builder.Environment.ContentRootPath, "App_Data");
var persistenceProvider = builder.Configuration["HIDBRIDGE_PERSISTENCE_PROVIDER"] ?? "File";
var sqlConnectionString = builder.Configuration["HIDBRIDGE_SQL_CONNECTION"]
    ?? "Host=localhost;Port=5432;Database=hidbridge;Username=postgres;Password=postgres";
var sqlSchema = builder.Configuration["HIDBRIDGE_SQL_SCHEMA"] ?? "hidbridge";
var sqlApplyMigrations = bool.TryParse(builder.Configuration["HIDBRIDGE_SQL_APPLY_MIGRATIONS"], out var parsedSqlApplyMigrations)
    ? parsedSqlApplyMigrations
    : true;
var maintenanceOptions = new SessionMaintenanceOptions
{
    LeaseDuration = TimeSpan.FromSeconds(int.TryParse(builder.Configuration["HIDBRIDGE_SESSION_LEASE_SECONDS"], out var parsedLeaseSeconds) ? parsedLeaseSeconds : 30),
    ControlLeaseDuration = TimeSpan.FromSeconds(int.TryParse(builder.Configuration["HIDBRIDGE_CONTROL_LEASE_SECONDS"], out var parsedControlLeaseSeconds) ? parsedControlLeaseSeconds : 30),
    RecoveryGracePeriod = TimeSpan.FromSeconds(int.TryParse(builder.Configuration["HIDBRIDGE_SESSION_RECOVERY_GRACE_SECONDS"], out var parsedRecoveryGraceSeconds) ? parsedRecoveryGraceSeconds : 120),
    CleanupInterval = TimeSpan.FromSeconds(int.TryParse(builder.Configuration["HIDBRIDGE_MAINTENANCE_INTERVAL_SECONDS"], out var parsedCleanupSeconds) ? parsedCleanupSeconds : 30),
    AuditRetention = TimeSpan.FromDays(int.TryParse(builder.Configuration["HIDBRIDGE_AUDIT_RETENTION_DAYS"], out var parsedAuditRetentionDays) ? parsedAuditRetentionDays : 30),
    TelemetryRetention = TimeSpan.FromDays(int.TryParse(builder.Configuration["HIDBRIDGE_TELEMETRY_RETENTION_DAYS"], out var parsedTelemetryRetentionDays) ? parsedTelemetryRetentionDays : 7),
};
var policyBootstrapOptions = new PolicyBootstrapOptions
{
    ScopeId = builder.Configuration["HIDBRIDGE_POLICY_SCOPE_ID"] ?? "scope-local-default",
    TenantId = builder.Configuration["HIDBRIDGE_POLICY_TENANT_ID"] ?? "local-tenant",
    OrganizationId = builder.Configuration["HIDBRIDGE_POLICY_ORGANIZATION_ID"] ?? "local-org",
    ViewerRoleRequired = !bool.TryParse(builder.Configuration["HIDBRIDGE_POLICY_VIEWER_ROLE_REQUIRED"], out var parsedViewerRoleRequired) || parsedViewerRoleRequired,
    ModeratorOverrideEnabled = !bool.TryParse(builder.Configuration["HIDBRIDGE_POLICY_MODERATOR_OVERRIDE_ENABLED"], out var parsedModeratorOverride) || parsedModeratorOverride,
    AdminOverrideEnabled = !bool.TryParse(builder.Configuration["HIDBRIDGE_POLICY_ADMIN_OVERRIDE_ENABLED"], out var parsedAdminOverride) || parsedAdminOverride,
    Assignments = ParseBootstrapAssignments(builder.Configuration["HIDBRIDGE_POLICY_BOOTSTRAP_ASSIGNMENTS"]),
};
var apiAuthenticationOptions = new ApiAuthenticationOptions
{
    Enabled = bool.TryParse(builder.Configuration["HIDBRIDGE_AUTH_ENABLED"], out var parsedAuthEnabled) && parsedAuthEnabled,
    Authority = builder.Configuration["HIDBRIDGE_AUTH_AUTHORITY"],
    Audience = builder.Configuration["HIDBRIDGE_AUTH_AUDIENCE"],
    RequireHttpsMetadata = !bool.TryParse(builder.Configuration["HIDBRIDGE_AUTH_REQUIRE_HTTPS_METADATA"], out var parsedAuthHttps) || parsedAuthHttps,
    AllowHeaderFallback = !bool.TryParse(builder.Configuration["HIDBRIDGE_AUTH_ALLOW_HEADER_FALLBACK"], out var parsedAllowHeaderFallback) || parsedAllowHeaderFallback,
    BearerOnlyPrefixes = ParsePrefixes(builder.Configuration["HIDBRIDGE_AUTH_BEARER_ONLY_PREFIXES"]),
    CallerContextRequiredPrefixes = ParsePrefixes(builder.Configuration["HIDBRIDGE_AUTH_CALLER_CONTEXT_REQUIRED_PREFIXES"]),
    HeaderFallbackDisabledPatterns = ParsePrefixes(builder.Configuration["HIDBRIDGE_AUTH_HEADER_FALLBACK_DISABLED_PATTERNS"]),
    DefaultTenantId = builder.Configuration["HIDBRIDGE_AUTH_DEFAULT_TENANT_ID"] ?? "local-tenant",
    DefaultOrganizationId = builder.Configuration["HIDBRIDGE_AUTH_DEFAULT_ORGANIZATION_ID"] ?? "local-org",
};
var policyRevisionLifecycleOptions = new PolicyRevisionLifecycleOptions
{
    MaintenanceInterval = TimeSpan.FromSeconds(int.TryParse(builder.Configuration["HIDBRIDGE_POLICY_REVISION_MAINTENANCE_INTERVAL_SECONDS"], out var parsedPolicyRevisionIntervalSeconds) ? parsedPolicyRevisionIntervalSeconds : 300),
    Retention = TimeSpan.FromDays(int.TryParse(builder.Configuration["HIDBRIDGE_POLICY_REVISION_RETENTION_DAYS"], out var parsedPolicyRevisionRetentionDays) ? parsedPolicyRevisionRetentionDays : 90),
    MaxRevisionsPerEntity = int.TryParse(builder.Configuration["HIDBRIDGE_POLICY_REVISION_MAX_PER_ENTITY"], out var parsedPolicyRevisionMaxPerEntity) ? parsedPolicyRevisionMaxPerEntity : 20,
};
var transportProviderToken = builder.Configuration["HIDBRIDGE_TRANSPORT_PROVIDER"] ?? "uart";
var defaultTransportProvider = ParseTransportProvider(transportProviderToken);
var endpointTransportProviders = ParseTransportProviderOverrides(
    builder.Configuration["HIDBRIDGE_TRANSPORT_PROVIDER_OVERRIDES"]);
var webRtcRequireCapability = bool.TryParse(builder.Configuration["HIDBRIDGE_WEBRTC_REQUIRE_CAPABILITY"], out var parsedWebRtcRequireCapability)
    && parsedWebRtcRequireCapability;
var webRtcEnableConnectorBridge = !bool.TryParse(builder.Configuration["HIDBRIDGE_WEBRTC_ENABLE_CONNECTOR_BRIDGE"], out var parsedWebRtcEnableConnectorBridge)
    || parsedWebRtcEnableConnectorBridge;
var transportFallbackToDefaultOnWebRtcError = !bool.TryParse(builder.Configuration["HIDBRIDGE_TRANSPORT_FALLBACK_TO_DEFAULT_ON_WEBRTC_ERROR"], out var parsedTransportFallbackToDefaultOnWebRtcError)
    || parsedTransportFallbackToDefaultOnWebRtcError;

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme);
builder.Services.AddAuthorization();
if (apiAuthenticationOptions.Enabled && !string.IsNullOrWhiteSpace(apiAuthenticationOptions.Authority))
{
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            options.Authority = apiAuthenticationOptions.Authority;
            options.RequireHttpsMetadata = apiAuthenticationOptions.RequireHttpsMetadata;
            options.Audience = string.IsNullOrWhiteSpace(apiAuthenticationOptions.Audience) ? null : apiAuthenticationOptions.Audience;
            options.MapInboundClaims = false;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                NameClaimType = "preferred_username",
                RoleClaimType = "role",
                ValidateAudience = !string.IsNullOrWhiteSpace(apiAuthenticationOptions.Audience),
            };
        });
}

if (string.Equals(persistenceProvider, "Sql", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSqlPersistence(new SqlPersistenceOptions(sqlConnectionString, sqlSchema));
}
else
{
    builder.Services.AddFilePersistence(new FilePersistenceOptions(dataRoot));
}

builder.Services.AddConnectorHost();
builder.Services.AddSingleton(new RealtimeTransportRuntimeOptions
{
    DefaultProvider = defaultTransportProvider,
    EndpointProviders = endpointTransportProviders,
});
builder.Services.AddSingleton(new WebRtcTransportRuntimeOptions
{
    RequireDataChannelCapability = webRtcRequireCapability,
    EnableConnectorBridge = webRtcEnableConnectorBridge,
});
builder.Services.AddSingleton(new DispatchCommandRuntimeOptions
{
    EnableDefaultProviderFallbackOnWebRtcError = transportFallbackToDefaultOnWebRtcError,
});
builder.Services.AddSingleton<WebRtcCommandRelayService>();
builder.Services.AddSingleton<IRealtimeTransport>(sp =>
    new ConnectorBackedRealtimeTransport(
        RealtimeTransportProvider.Uart,
        sp.GetRequiredService<IConnectorRegistry>()));
builder.Services.AddSingleton<IRealtimeTransport>(sp =>
    new WebRtcDataChannelRealtimeTransport(
        sp.GetRequiredService<IConnectorRegistry>(),
        sp.GetRequiredService<IEndpointSnapshotStore>(),
        sp.GetRequiredService<WebRtcCommandRelayService>(),
        sp.GetRequiredService<WebRtcTransportRuntimeOptions>()));
builder.Services.AddSingleton<IRealtimeTransportFactory, DefaultRealtimeTransportFactory>();
builder.Services.AddSessionOrchestrator();
builder.Services.AddSingleton(maintenanceOptions);
builder.Services.AddSingleton<RegisterAgentUseCase>();
builder.Services.AddHostedService<ConnectorBootstrapHostedService>();
builder.Services.AddHostedService<StartupReconciliationHostedService>();
builder.Services.AddHostedService<SessionMaintenanceHostedService>();
builder.Services.AddHostedService<PolicyBootstrapHostedService>();
builder.Services.AddSingleton(policyRevisionLifecycleOptions);
builder.Services.AddSingleton<PolicyRevisionLifecycleService>();
builder.Services.AddHostedService<PolicyRevisionMaintenanceHostedService>();
builder.Services.AddSingleton<CollaborationReadModelService>();
builder.Services.AddSingleton<OperationsDashboardReadModelService>();
builder.Services.AddSingleton<ProjectionQueryService>();
builder.Services.AddSingleton<ReplayArchiveDiagnosticsService>();
builder.Services.AddSingleton<PolicyGovernanceDiagnosticsService>();
builder.Services.AddSingleton<PolicyGovernanceManagementService>();
builder.Services.AddSingleton<WebRtcSignalingService>();

var uartPort = builder.Configuration["HIDBRIDGE_UART_PORT"] ?? "COM6";
var uartMasterSecret = builder.Configuration["HIDBRIDGE_UART_MASTER_SECRET"] ?? string.Empty;
var configuredUartHmacKey = builder.Configuration["HIDBRIDGE_UART_HMAC_KEY"];
var uartHmacKey = !string.IsNullOrWhiteSpace(configuredUartHmacKey)
    ? configuredUartHmacKey
    : (!string.IsNullOrWhiteSpace(uartMasterSecret) ? uartMasterSecret : "changeme");
var uartBaud = int.TryParse(builder.Configuration["HIDBRIDGE_UART_BAUD"], out var parsedBaud) ? parsedBaud : 3000000;
var mouseSelector = byte.TryParse(builder.Configuration["HIDBRIDGE_UART_MOUSE_SELECTOR"], out var parsedMouseSelector)
    ? parsedMouseSelector
    : (byte)0xFF;
var keyboardSelector = byte.TryParse(builder.Configuration["HIDBRIDGE_UART_KEYBOARD_SELECTOR"], out var parsedKeyboardSelector)
    ? parsedKeyboardSelector
    : (byte)0xFE;
var uartCommandTimeoutMs = int.TryParse(builder.Configuration["HIDBRIDGE_UART_COMMAND_TIMEOUT_MS"], out var parsedUartCommandTimeoutMs)
    ? parsedUartCommandTimeoutMs
    : 300;
var uartInjectTimeoutMs = int.TryParse(builder.Configuration["HIDBRIDGE_UART_INJECT_TIMEOUT_MS"], out var parsedUartInjectTimeoutMs)
    ? parsedUartInjectTimeoutMs
    : 200;
var uartInjectRetries = int.TryParse(builder.Configuration["HIDBRIDGE_UART_INJECT_RETRIES"], out var parsedUartInjectRetries)
    ? parsedUartInjectRetries
    : 2;
var agentId = builder.Configuration["HIDBRIDGE_AGENT_ID"] ?? "agent_hidbridge_uart_local";
var endpointId = builder.Configuration["HIDBRIDGE_ENDPOINT_ID"] ?? "endpoint_local_demo";
var uartOptions = new HidBridgeUartClientOptions(
    uartPort,
    uartBaud,
    uartHmacKey,
    uartMasterSecret,
    mouseSelector,
    keyboardSelector,
    uartCommandTimeoutMs,
    uartInjectTimeoutMs,
    uartInjectRetries);

builder.Services.AddSingleton(new ApiRuntimeSettings
{
    DataRoot = dataRoot,
    PersistenceProvider = persistenceProvider,
    TransportProvider = defaultTransportProvider.ToString(),
    SqlSchema = sqlSchema,
    SqlApplyMigrations = sqlApplyMigrations,
    UartPort = uartPort,
    UartBaudRate = uartBaud,
    MouseSelector = mouseSelector,
    KeyboardSelector = keyboardSelector,
    UartCommandTimeoutMs = uartCommandTimeoutMs,
    UartInjectTimeoutMs = uartInjectTimeoutMs,
    UartInjectRetries = uartInjectRetries,
    UartUsesMasterSecret = !string.IsNullOrWhiteSpace(uartMasterSecret),
    WebRtcRequireCapability = webRtcRequireCapability,
    WebRtcEnableConnectorBridge = webRtcEnableConnectorBridge,
    TransportFallbackToDefaultOnWebRtcError = transportFallbackToDefaultOnWebRtcError,
    AgentId = agentId,
    EndpointId = endpointId,
    Maintenance = maintenanceOptions,
    PolicyBootstrap = policyBootstrapOptions,
    Authentication = apiAuthenticationOptions,
    PolicyRevisionLifecycle = policyRevisionLifecycleOptions,
});
builder.Services.AddSingleton(policyBootstrapOptions);

builder.Services.AddSingleton<IConnector>(_ =>
    new HidBridgeUartConnector(
        agentId: agentId,
        endpointId: endpointId,
        options: uartOptions));

var app = builder.Build();

if (string.Equals(persistenceProvider, "Sql", StringComparison.OrdinalIgnoreCase) && sqlApplyMigrations)
{
    await app.Services.ApplySqlPersistenceMigrationsAsync();
}

app.UseAuthentication();
app.UseAuthorization();
app.UseApiAuthenticationGuard(apiAuthenticationOptions);
app.UsePolicyContextEnrichment();

app.MapOpenApi();
app.MapScalarApiReference("/scalar");
app.MapSystemEndpoints();
app.MapInventoryEndpoints();
app.MapSessionEndpoints();
app.MapTransportEndpoints();
app.MapCollaborationEndpoints();
app.MapInvitationEndpoints();
app.MapControlEndpoints();
app.MapCollaborationReadModelEndpoints();
app.MapDashboardEndpoints();
app.MapProjectionEndpoints();
app.MapDiagnosticsEndpoints();
app.MapEventEndpoints();

app.Run();

static IReadOnlyList<string> ParsePrefixes(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return [];
    }

    return value
        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(static entry => !string.IsNullOrWhiteSpace(entry))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
}

static RealtimeTransportProvider ParseTransportProvider(string? value)
{
    if (RealtimeTransportProviderParser.TryParse(value, out var parsedProvider))
    {
        return parsedProvider;
    }

    return RealtimeTransportProvider.Uart;
}

static IReadOnlyDictionary<string, RealtimeTransportProvider> ParseTransportProviderOverrides(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return new Dictionary<string, RealtimeTransportProvider>(StringComparer.OrdinalIgnoreCase);
    }

    var map = new Dictionary<string, RealtimeTransportProvider>(StringComparer.OrdinalIgnoreCase);
    foreach (var entry in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        var parts = entry.Split('=', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]))
        {
            continue;
        }

        if (!RealtimeTransportProviderParser.TryParse(parts[1], out var provider))
        {
            continue;
        }

        map[parts[0]] = provider;
    }

    return map;
}

static IReadOnlyList<PolicyBootstrapAssignment> ParseBootstrapAssignments(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return [];
    }

    var assignments = new List<PolicyBootstrapAssignment>();
    foreach (var entry in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        var parts = entry.Split('=', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]))
        {
            continue;
        }

        var roles = parts[1]
            .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static role => !string.IsNullOrWhiteSpace(role))
            .ToArray();

        assignments.Add(new PolicyBootstrapAssignment(parts[0], roles, "bootstrap-config"));
    }

    return assignments;
}
