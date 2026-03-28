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
                "/api/v1/diagnostics/transport/slo",
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
            runtimeSettings.UartPassiveHealthMode,
            runtimeSettings.UartReleasePortAfterExecute,
            webRtc = new
            {
                runtimeSettings.WebRtcRequireCapability,
                runtimeSettings.WebRtcEnableConnectorBridge,
                runtimeSettings.WebRtcEnableDcdControlBridge,
                runtimeSettings.WebRtcRequireMediaReady,
                runtimeSettings.WebRtcPeerStaleAfterSec,
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
            transportSlo = new
            {
                runtimeSettings.TransportSlo.DefaultWindowMinutes,
                runtimeSettings.TransportSlo.RelayReadyLatencyWarnMs,
                runtimeSettings.TransportSlo.RelayReadyLatencyCriticalMs,
                runtimeSettings.TransportSlo.AckTimeoutRateWarn,
                runtimeSettings.TransportSlo.AckTimeoutRateCritical,
                runtimeSettings.TransportSlo.ReconnectFrequencyWarnPerHour,
                runtimeSettings.TransportSlo.ReconnectFrequencyCriticalPerHour,
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

        endpoints.MapPost("/api/v1/runtime/agent-install/link",
            (
                HttpContext httpContext,
                AgentInstallLinkRequest request,
                AgentInstallRuntimeOptions installOptions,
                AgentInstallTokenService tokenService) =>
            {
                if (!installOptions.Enabled)
                {
                    return Results.NotFound(new
                    {
                        code = "agent_install_disabled",
                        message = "Agent install-link flow is disabled by runtime policy.",
                    });
                }

                var baseUrl = request.BaseUrl?.Trim();
                if (string.IsNullOrWhiteSpace(baseUrl))
                {
                    return Results.BadRequest(new
                    {
                        code = "invalid_request",
                        message = "BaseUrl is required.",
                    });
                }

                var sessionId = string.IsNullOrWhiteSpace(request.SessionId)
                    ? $"room-webrtc-peer-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}"
                    : request.SessionId.Trim();
                var endpointId = string.IsNullOrWhiteSpace(request.EndpointId)
                    ? "endpoint_local_demo"
                    : request.EndpointId.Trim();
                var peerId = string.IsNullOrWhiteSpace(request.PeerId)
                    ? $"peer-{SanitizePeerSuffix(endpointId)}"
                    : request.PeerId.Trim();

                var expiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(Math.Clamp(
                    request.ExpiresInMinutes ?? installOptions.TokenTtlMinutes,
                    5,
                    24 * 60));
                var publicBaseUrl = ResolvePublicBaseUrl(httpContext, installOptions.PublicBaseUrl);
                var payload = new AgentInstallBootstrapPayload(
                    SessionId: sessionId,
                    PeerId: peerId,
                    EndpointId: endpointId,
                    BaseUrl: baseUrl,
                    ControlPlaneWebUrl: string.IsNullOrWhiteSpace(request.ControlPlaneWebUrl)
                        ? publicBaseUrl
                        : request.ControlPlaneWebUrl.Trim().TrimEnd('/'),
                    UartPort: request.UartPort.Trim(),
                    UartHmacKey: request.UartHmacKey.Trim(),
                    FfmpegLatencyProfile: request.FfmpegLatencyProfile.Trim().ToLowerInvariant(),
                    MediaStreamId: request.MediaStreamId.Trim(),
                    MediaWhipUrl: request.MediaWhipUrl.Trim(),
                    MediaWhepUrl: request.MediaWhepUrl.Trim(),
                    MediaPlaybackUrl: request.MediaPlaybackUrl.Trim(),
                    MediaHealthUrl: request.MediaHealthUrl.Trim(),
                    FfmpegExecutablePath: request.FfmpegExecutablePath.Trim(),
                    FfmpegVideoDevice: request.FfmpegVideoDevice?.Trim() ?? string.Empty,
                    FfmpegAudioDevice: request.FfmpegAudioDevice?.Trim() ?? string.Empty,
                    FfmpegUseTestSource: request.FfmpegUseTestSource,
                    MediaBackendAutoStart: request.MediaBackendAutoStart,
                    RequireMediaReady: request.RequireMediaReady,
                    PackageUrl: installOptions.PackageUrl.Trim(),
                    PackageSha256: installOptions.PackageSha256.Trim(),
                    AgentExecutableRelativePath: installOptions.AgentExecutableRelativePath.Trim(),
                    DefaultInstallDirectory: installOptions.DefaultInstallDirectory,
                    ExpiresAtUtc: expiresAtUtc);

                var token = tokenService.CreateToken(payload);
                var bootstrapScriptUrl = $"{publicBaseUrl}/agent-install/bootstrap.ps1?token={Uri.EscapeDataString(token)}";

                return Results.Ok(new AgentInstallLinkResponse(
                    InstallUrl: bootstrapScriptUrl,
                    BootstrapScriptUrl: bootstrapScriptUrl,
                    SessionId: sessionId,
                    PeerId: peerId,
                    EndpointId: endpointId,
                    ExpiresAtUtc: expiresAtUtc,
                    OneShotCommand: $"powershell -ExecutionPolicy Bypass -NoProfile -Command \"iwr '{bootstrapScriptUrl}' -UseBasicParsing | iex\""));
            })
        .WithTags(ApiEndpointTags.System)
        .WithSummary("Generates one signed install URL for remote edge-agent bootstrap.")
        .WithDescription("Returns a time-limited signed URL that produces one PowerShell bootstrap script with pre-filled agent configuration.");

        endpoints.MapGet("/agent-install/bootstrap.ps1",
            (
                HttpContext httpContext,
                string? token,
                AgentInstallTokenService tokenService) =>
            {
                if (!tokenService.TryValidate(token, out var payload, out var error))
                {
                    return Results.BadRequest($"# Invalid bootstrap token: {error}");
                }

                var script = BuildBootstrapScript(payload, httpContext.Request.Scheme, httpContext.Request.Host.ToString());
                return Results.Text(script, "text/plain; charset=utf-8");
            })
        .WithTags(ApiEndpointTags.System)
        .WithSummary("Returns bootstrap PowerShell for one signed agent install token.")
        .WithDescription("Tokenized bootstrap endpoint intended for one-click remote edge-agent setup.");

        return endpoints;
    }

    private static string ResolvePublicBaseUrl(HttpContext context, string configuredPublicBaseUrl)
    {
        if (!string.IsNullOrWhiteSpace(configuredPublicBaseUrl))
        {
            return configuredPublicBaseUrl.Trim().TrimEnd('/');
        }

        return $"{context.Request.Scheme}://{context.Request.Host}".TrimEnd('/');
    }

    private static string SanitizePeerSuffix(string endpointId)
    {
        var chars = endpointId
            .Where(static c => char.IsLetterOrDigit(c) || c is '-' or '_')
            .ToArray();
        var normalized = new string(chars);
        return string.IsNullOrWhiteSpace(normalized) ? "edge" : normalized.ToLowerInvariant();
    }

    private static string BuildBootstrapScript(
        AgentInstallBootstrapPayload payload,
        string requestScheme,
        string requestHost)
    {
        var configJson = $$"""
{
  "BaseUrl": {{ToJsonString(payload.BaseUrl)}},
  "SessionId": {{ToJsonString(payload.SessionId)}},
  "PeerId": {{ToJsonString(payload.PeerId)}},
  "EndpointId": {{ToJsonString(payload.EndpointId)}},
  "CommandExecutor": "uart",
  "TransportEngine": "dcd",
  "MediaEngine": "ffmpeg-dcd",
  "FfmpegLatencyProfile": {{ToJsonString(payload.FfmpegLatencyProfile)}},
  "MediaStreamId": {{ToJsonString(payload.MediaStreamId)}},
  "MediaWhipUrl": {{ToJsonString(payload.MediaWhipUrl)}},
  "MediaWhepUrl": {{ToJsonString(payload.MediaWhepUrl)}},
  "MediaPlaybackUrl": {{ToJsonString(payload.MediaPlaybackUrl)}},
  "MediaHealthUrl": {{ToJsonString(payload.MediaHealthUrl)}},
  "FfmpegExecutablePath": {{ToJsonString(payload.FfmpegExecutablePath)}},
  "FfmpegVideoDevice": {{ToJsonString(payload.FfmpegVideoDevice)}},
  "FfmpegAudioDevice": {{ToJsonString(payload.FfmpegAudioDevice)}},
  "FfmpegUseTestSource": {{ToJsonBool(payload.FfmpegUseTestSource)}},
  "MediaBackendAutoStart": {{ToJsonBool(payload.MediaBackendAutoStart)}},
  "RequireMediaReady": {{ToJsonBool(payload.RequireMediaReady)}},
  "UartPort": {{ToJsonString(payload.UartPort)}},
  "UartHmacKey": {{ToJsonString(payload.UartHmacKey)}}
}
""";
        var escapedConfig = configJson.Replace("'", "''", StringComparison.Ordinal);
        var escapedInstallDir = payload.DefaultInstallDirectory.Replace("'", "''", StringComparison.Ordinal);
        var escapedPackageUrl = payload.PackageUrl.Replace("'", "''", StringComparison.Ordinal);
        var escapedPackageSha = payload.PackageSha256.Replace("'", "''", StringComparison.Ordinal);
        var escapedExeRelativePath = payload.AgentExecutableRelativePath.Replace("'", "''", StringComparison.Ordinal);
        var sessionUrl = $"{requestScheme}://{requestHost}/sessions/{Uri.EscapeDataString(payload.SessionId)}";
        var escapedSessionUrl = sessionUrl.Replace("'", "''", StringComparison.Ordinal);

        return $$"""
param(
  [string]$InstallDir = '{{escapedInstallDir}}',
  [switch]$StartAgent = $true
)
$ErrorActionPreference = 'Stop'

Write-Host '[hidbridge] Preparing install directory...' -ForegroundColor Cyan
New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null

$configJson = @'
{{escapedConfig}}
'@

$configPath = Join-Path $InstallDir 'appsettings.json'
Set-Content -Path $configPath -Value $configJson -Encoding UTF8
Write-Host "[hidbridge] Wrote config: $configPath" -ForegroundColor Green

$packageUrl = '{{escapedPackageUrl}}'
$packageSha = '{{escapedPackageSha}}'
if (-not [string]::IsNullOrWhiteSpace($packageUrl)) {
  $zipPath = Join-Path $InstallDir 'edge-agent-package.zip'
  Write-Host "[hidbridge] Downloading package: $packageUrl" -ForegroundColor Cyan
  Invoke-WebRequest -Uri $packageUrl -OutFile $zipPath -UseBasicParsing
  if (-not [string]::IsNullOrWhiteSpace($packageSha)) {
    $actual = (Get-FileHash -Path $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $packageSha.ToLowerInvariant()) {
      throw "Package hash mismatch. expected=$packageSha actual=$actual"
    }
  }
  Expand-Archive -Path $zipPath -DestinationPath $InstallDir -Force
  Remove-Item $zipPath -Force -ErrorAction SilentlyContinue
}

$exePath = Join-Path $InstallDir '{{escapedExeRelativePath}}'
if (-not (Test-Path $exePath)) {
  Write-Warning "[hidbridge] Agent executable not found at $exePath"
  Write-Host '[hidbridge] Config is ready. Place published agent files into install dir and run manually.' -ForegroundColor Yellow
  Write-Host "[hidbridge] Session URL: {{escapedSessionUrl}}"
  exit 0
}

if ($StartAgent) {
  Write-Host "[hidbridge] Starting edge agent: $exePath" -ForegroundColor Cyan
  Start-Process -FilePath $exePath -WorkingDirectory $InstallDir -WindowStyle Hidden | Out-Null
  Start-Sleep -Seconds 3
}

Write-Host '[hidbridge] Bootstrap completed.' -ForegroundColor Green
Write-Host "[hidbridge] Open session: {{escapedSessionUrl}}"
""";
    }

    private static string ToJsonString(string value)
        => $"\"{(value ?? string.Empty).Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

    private static string ToJsonBool(bool value) => value ? "true" : "false";
}

/// <summary>
/// Defines request payload for install-link generation.
/// </summary>
public sealed record AgentInstallLinkRequest(
    string BaseUrl,
    string? ControlPlaneWebUrl,
    string? SessionId,
    string? PeerId,
    string EndpointId,
    string UartPort,
    string UartHmacKey,
    string FfmpegLatencyProfile,
    string MediaStreamId,
    string MediaWhipUrl,
    string MediaWhepUrl,
    string MediaPlaybackUrl,
    string MediaHealthUrl,
    string FfmpegExecutablePath,
    string? FfmpegVideoDevice,
    string? FfmpegAudioDevice,
    bool FfmpegUseTestSource,
    bool MediaBackendAutoStart,
    bool RequireMediaReady,
    int? ExpiresInMinutes);

/// <summary>
/// Defines generated install-link response.
/// </summary>
public sealed record AgentInstallLinkResponse(
    string InstallUrl,
    string BootstrapScriptUrl,
    string SessionId,
    string PeerId,
    string EndpointId,
    DateTimeOffset ExpiresAtUtc,
    string OneShotCommand);
