using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Implements the <c>BearerSmokeCommand</c> RuntimeCtl lane.
/// Execution contract:
/// 1) Parse CLI arguments into strongly typed options.
/// 2) Execute lane-specific orchestration (native RuntimeCtl commands first, script bridge only when explicitly needed).
/// 3) Persist step logs/summaries under <c>Platform/.logs</c> and return process-style exit code semantics (0 = success).
/// </summary>
internal static class BearerSmokeCommand
{
    public static async Task<int> RunAsync(string platformRoot, IReadOnlyList<string> args)
    {
        if (!BearerSmokeOptions.TryParse(args, out var options, out var parseError))
        {
            Console.Error.WriteLine($"bearer-smoke options error: {parseError}");
            return 1;
        }

        var repoRoot = Directory.GetParent(platformRoot)?.FullName ?? platformRoot;
        var logRoot = Path.Combine(platformRoot, ".logs", "bearer-smoke", DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(logRoot);
        var latest = Path.Combine(platformRoot, ".logs", "bearer-smoke", "latest.log");
        Directory.CreateDirectory(Path.GetDirectoryName(latest)!);
        var logPath = Path.Combine(logRoot, "bearer-smoke.log");
        var summaryPath = Path.Combine(logRoot, "smoke.result.json");
        var stdoutLog = Path.Combine(logRoot, "controlplane.stdout.log");
        var stderrLog = Path.Combine(logRoot, "controlplane.stderr.log");
        var projectPath = Path.Combine(repoRoot, "Platform", "Platform", "HidBridge.ControlPlane.Api", "HidBridge.ControlPlane.Api.csproj");

        var lines = new List<string>();
        Process? apiProcess = null;
        var startedApi = false;

        try
        {
            if (!options.NoBuild)
            {
                var buildLogPath = Path.Combine(logRoot, "build.log");
                var buildExitCode = await RunDotnetStepAsync(
                    repoRoot,
                    ["build", "Platform/HidBridge.Platform.sln", "-c", options.Configuration, "-v", "minimal", "-m:1", "-nodeReuse:false"],
                    buildLogPath);
                if (buildExitCode != 0)
                {
                    throw new InvalidOperationException($"Build failed with exit code {buildExitCode}. See {buildLogPath}.");
                }
                lines.Add("PASS build");
            }

            if (string.IsNullOrWhiteSpace(options.DataRoot))
            {
                options.DataRoot = Path.Combine(platformRoot, ".smoke-data", options.Provider, DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss"));
            }
            Directory.CreateDirectory(options.DataRoot);

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            var apiHealthyAtRequestedUrl = await TryHealthAsync(http, options.BaseUrl);
            var effectiveBaseUrl = options.BaseUrl.TrimEnd('/');
            var shouldLaunchDedicatedApi = !apiHealthyAtRequestedUrl || options.ProviderExplicit;
            if (shouldLaunchDedicatedApi)
            {
                var launchBaseUrl = options.BaseUrl.TrimEnd('/');
                if (apiHealthyAtRequestedUrl)
                {
                    var requestedUri = new Uri(options.BaseUrl);
                    var requestedPort = requestedUri.IsDefaultPort ? 80 : requestedUri.Port;
                    var freePort = FindAvailablePort(requestedPort + 1);
                    launchBaseUrl = $"{requestedUri.Scheme}://127.0.0.1:{freePort}";
                    lines.Add($"INFO api.dedicated.url={launchBaseUrl}");
                }

                var startInfo = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    WorkingDirectory = repoRoot,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                startInfo.ArgumentList.Add("run");
                startInfo.ArgumentList.Add("--project");
                startInfo.ArgumentList.Add(projectPath);
                startInfo.ArgumentList.Add("-c");
                startInfo.ArgumentList.Add(options.Configuration);
                startInfo.ArgumentList.Add("--no-build");
                startInfo.ArgumentList.Add("--no-launch-profile");
                startInfo.Environment["ASPNETCORE_URLS"] = launchBaseUrl;
                // API appsettings pins Kestrel endpoint to :18093; force dedicated smoke URL explicitly.
                startInfo.Environment["Kestrel__Endpoints__Http__Url"] = launchBaseUrl;
                startInfo.Environment["HIDBRIDGE_PERSISTENCE_PROVIDER"] = options.Provider;
                startInfo.Environment["HIDBRIDGE_DATA_ROOT"] = options.DataRoot;
                startInfo.Environment["HIDBRIDGE_SQL_CONNECTION"] = options.ConnectionString;
                startInfo.Environment["HIDBRIDGE_SQL_SCHEMA"] = options.Schema;
                startInfo.Environment["HIDBRIDGE_SQL_APPLY_MIGRATIONS"] = "true";
                startInfo.Environment["HIDBRIDGE_TRANSPORT_PROVIDER"] = "webrtc-datachannel";
                startInfo.Environment["HIDBRIDGE_AUTH_ENABLED"] = options.EnableApiAuth ? "true" : "false";
                startInfo.Environment["HIDBRIDGE_AUTH_AUTHORITY"] = options.AuthAuthority;
                startInfo.Environment["HIDBRIDGE_AUTH_AUDIENCE"] = options.AuthAudience;
                startInfo.Environment["HIDBRIDGE_AUTH_REQUIRE_HTTPS_METADATA"] = options.EnableApiAuth && options.AuthAuthority.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? "false" : "true";
                startInfo.Environment["HIDBRIDGE_AUTH_ALLOW_HEADER_FALLBACK"] = options.DisableHeaderFallback ? "false" : "true";
                startInfo.Environment["HIDBRIDGE_UART_PASSIVE_HEALTH_MODE"] = "true";
                startInfo.Environment["HIDBRIDGE_UART_RELEASE_PORT_AFTER_EXECUTE"] = "true";

                apiProcess = WebRtcStackCommand.StartRedirectedProcess(startInfo, stdoutLog, stderrLog);
                startedApi = true;

                var healthReady = false;
                for (var i = 0; i < 40; i++)
                {
                    await Task.Delay(500);
                    if (await TryHealthAsync(http, launchBaseUrl))
                    {
                        healthReady = true;
                        break;
                    }
                    if (apiProcess.HasExited)
                    {
                        break;
                    }
                }

                if (!healthReady)
                {
                    throw new InvalidOperationException($"API did not become healthy at {launchBaseUrl}.");
                }

                effectiveBaseUrl = launchBaseUrl;
                options.BaseUrl = launchBaseUrl;
            }

            lines.Add("PASS api.health");

            var inventoryUri = $"{options.BaseUrl.TrimEnd('/')}/api/v1/dashboards/inventory";
            var ownerToken = string.Empty;
            var viewerToken = string.Empty;
            var foreignToken = string.Empty;
            var bearerChecksRequested = options.EnableApiAuth || options.BearerOnly;
            var effectiveBearerChecks = bearerChecksRequested;
            var providedAccessToken = !string.IsNullOrWhiteSpace(options.AccessToken) && IsJwtTokenLike(options.AccessToken);
            if (!effectiveBearerChecks)
            {
                var anonymousStatus = await GetStatusAsync(http, inventoryUri, null);
                if (anonymousStatus is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    effectiveBearerChecks = true;
                    lines.Add("WARN inventory.anonymous.denied.auto-switch-bearer");
                }
                else if ((int)anonymousStatus >= 200 && (int)anonymousStatus < 300)
                {
                    lines.Add("PASS inventory.anonymous.access");
                }
                else
                {
                    throw new InvalidOperationException($"Unexpected anonymous status for inventory endpoint: {(int)anonymousStatus}.");
                }
            }

            if (effectiveBearerChecks)
            {
                if (providedAccessToken)
                {
                    ownerToken = options.AccessToken;
                    viewerToken = options.AccessToken;
                    foreignToken = options.AccessToken;
                    lines.Add("PASS oidc.tokens.provided");
                }

                var unauthorizedStatus = await GetStatusAsync(http, inventoryUri, null);
                if (unauthorizedStatus is not HttpStatusCode.Unauthorized and not HttpStatusCode.Forbidden)
                {
                    throw new InvalidOperationException($"Expected 401/403 without bearer token for inventory endpoint, got {(int)unauthorizedStatus}.");
                }
                lines.Add("PASS inventory.anonymous.denied");

                if (providedAccessToken)
                {
                    await EnsureSuccessAsync(http, inventoryUri, ownerToken, "owner");
                    await EnsureSuccessAsync(http, inventoryUri, viewerToken, "viewer");
                    await EnsureSuccessAsync(http, inventoryUri, foreignToken, "foreign");
                }
                else
                {
                    Exception? lastAuthError = null;
                    var selectedAuthority = options.AuthAuthority;
                    foreach (var authorityCandidate in BuildAuthAuthorityCandidates(options.AuthAuthority))
                    {
                        try
                        {
                            ownerToken = await RequestOidcTokenAsync(authorityCandidate, options.TokenClientId, options.TokenClientSecret, options.TokenScope, options.TokenUsername, options.TokenPassword);
                            viewerToken = await RequestOidcTokenAsync(authorityCandidate, options.TokenClientId, options.TokenClientSecret, options.TokenScope, options.ViewerTokenUsername, options.ViewerTokenPassword);
                            foreignToken = await RequestOidcTokenAsync(authorityCandidate, options.TokenClientId, options.TokenClientSecret, options.TokenScope, options.ForeignTokenUsername, options.ForeignTokenPassword);
                            await EnsureSuccessAsync(http, inventoryUri, ownerToken, "owner");
                            await EnsureSuccessAsync(http, inventoryUri, viewerToken, "viewer");
                            await EnsureSuccessAsync(http, inventoryUri, foreignToken, "foreign");
                            selectedAuthority = authorityCandidate;
                            lastAuthError = null;
                            break;
                        }
                        catch (Exception ex)
                        {
                            lastAuthError = ex;
                        }
                    }

                    if (lastAuthError is not null)
                    {
                        throw lastAuthError;
                    }

                    lines.Add("PASS oidc.tokens");
                    if (!string.Equals(selectedAuthority, options.AuthAuthority, StringComparison.OrdinalIgnoreCase))
                    {
                        lines.Add($"WARN oidc.authority.fallback={selectedAuthority}");
                    }
                }

                lines.Add("PASS inventory.bearer.access");

                var runtimeUri = $"{options.BaseUrl.TrimEnd('/')}/api/v1/runtime/uart";
                using var runtimeRequest = new HttpRequestMessage(HttpMethod.Get, runtimeUri);
                runtimeRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ownerToken);
                using var runtimeResponse = await http.SendAsync(runtimeRequest);
                var runtimePayload = await runtimeResponse.Content.ReadAsStringAsync();
                if (!runtimeResponse.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException($"Runtime probe failed ({(int)runtimeResponse.StatusCode}): {runtimePayload}");
                }

                using var runtimeDoc = JsonDocument.Parse(runtimePayload);
                var provider = runtimeDoc.RootElement.TryGetProperty("persistenceProvider", out var providerProperty)
                    ? (providerProperty.GetString() ?? string.Empty)
                    : string.Empty;
                if (!string.IsNullOrWhiteSpace(provider)
                    && !string.Equals(provider, options.Provider, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Runtime provider mismatch. Expected '{options.Provider}', got '{provider}'.");
                }
                lines.Add("PASS runtime.provider");
            }

            var multiActorBearerMode = effectiveBearerChecks && !providedAccessToken;
            JsonElement? scenario = null;
            var shouldRunScenario = effectiveBearerChecks || !options.DisableHeaderFallback;
            if (shouldRunScenario)
            {
                scenario = await RunSessionScenarioAsync(
                    http,
                    options,
                    effectiveBearerChecks,
                    ownerToken,
                    viewerToken,
                    foreignToken,
                    multiActorBearerMode);
                lines.Add("PASS session.flow");
            }
            else
            {
                lines.Add("WARN session.flow.skipped");
            }

            var summary = new
            {
                generatedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
                status = "PASS",
                baseUrl = options.BaseUrl,
                provider = options.Provider,
                dataRoot = options.DataRoot,
                authAuthority = options.AuthAuthority,
                multiActorBearerMode,
                checks = lines,
                scenario,
            };
            await File.WriteAllTextAsync(summaryPath, JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
            await File.WriteAllLinesAsync(logPath, lines);
            await File.WriteAllTextAsync(latest, logPath);

            Console.WriteLine();
            Console.WriteLine("=== Bearer Smoke Summary ===");
            Console.WriteLine("PASS  Bearer Smoke");
            Console.WriteLine($"Log:   {logPath}");
            return 0;
        }
        catch (Exception ex)
        {
            lines.Add($"FAIL {ex.Message}");
            await File.WriteAllLinesAsync(logPath, lines);
            await File.WriteAllTextAsync(latest, logPath);
            Console.WriteLine();
            Console.WriteLine("=== Bearer Smoke Summary ===");
            Console.WriteLine("FAIL  Bearer Smoke");
            Console.WriteLine($"Log:   {logPath}");
            return 1;
        }
        finally
        {
            if (startedApi && apiProcess is not null && !apiProcess.HasExited)
            {
                try
                {
                    apiProcess.Kill(entireProcessTree: true);
                }
                catch
                {
                    // best effort
                }
            }
        }
    }

    private static async Task EnsureSuccessAsync(HttpClient http, string uri, string accessToken, string actor)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, uri);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var resp = await http.SendAsync(req);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"Expected success for {actor} bearer on {uri}, got {(int)resp.StatusCode} {resp.StatusCode}. {body}");
        }
    }

    private static async Task<int> RunDotnetStepAsync(string workingDirectory, IReadOnlyList<string> args, string logPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            await File.WriteAllTextAsync(logPath, "Failed to start dotnet process.");
            return 1;
        }

        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Directory.CreateDirectory(Path.GetDirectoryName(logPath) ?? workingDirectory);
        await File.WriteAllTextAsync(logPath, $"{stdout}{Environment.NewLine}{stderr}".Trim());
        return process.ExitCode;
    }

    private static bool IsJwtTokenLike(string value) =>
        value.Count(static c => c == '.') >= 2;

    private static int FindAvailablePort(int startingPort)
    {
        var port = Math.Max(1025, startingPort);
        for (var attempt = 0; attempt < 200; attempt++, port++)
        {
            try
            {
                using var listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                return port;
            }
            catch (SocketException)
            {
                // try next port
            }
        }

        throw new InvalidOperationException($"Unable to find free TCP port starting from {startingPort}.");
    }

    private static async Task<HttpStatusCode> GetStatusAsync(HttpClient http, string uri, string? accessToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, uri);
        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }
        using var resp = await http.SendAsync(req);
        return resp.StatusCode;
    }

    private static IReadOnlyList<string> BuildAuthAuthorityCandidates(string configuredAuthority)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<string>();

        static void AddCandidate(List<string> list, HashSet<string> seenSet, string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            var normalized = value.Trim().TrimEnd('/');
            if (seenSet.Add(normalized))
            {
                list.Add(normalized);
            }
        }

        AddCandidate(candidates, seen, configuredAuthority);
        if (!Uri.TryCreate(configuredAuthority, UriKind.Absolute, out var uri))
        {
            return candidates;
        }

        var port = uri.IsDefaultPort
            ? string.Empty
            : $":{uri.Port}";
        var pathAndQuery = string.IsNullOrWhiteSpace(uri.PathAndQuery)
            ? string.Empty
            : uri.PathAndQuery.TrimEnd('/');

        if (string.Equals(uri.Host, "host.docker.internal", StringComparison.OrdinalIgnoreCase))
        {
            AddCandidate(candidates, seen, $"{uri.Scheme}://127.0.0.1{port}{pathAndQuery}");
            AddCandidate(candidates, seen, $"{uri.Scheme}://localhost{port}{pathAndQuery}");
        }
        else if (string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
                 || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            AddCandidate(candidates, seen, $"{uri.Scheme}://host.docker.internal{port}{pathAndQuery}");
        }

        return candidates;
    }

    private static async Task<bool> TryHealthAsync(HttpClient http, string baseUrl)
    {
        try
        {
            using var response = await http.GetAsync($"{baseUrl.TrimEnd('/')}/health");
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return doc.RootElement.TryGetProperty("status", out var status)
                   && string.Equals(status.GetString(), "ok", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static async Task<string> RequestOidcTokenAsync(
        string authAuthority,
        string clientId,
        string clientSecret,
        string scope,
        string username,
        string password)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var scopeCandidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(scope))
        {
            scopeCandidates.Add(scope);
        }
        if (!scopeCandidates.Contains("openid", StringComparer.Ordinal))
        {
            scopeCandidates.Add("openid");
        }
        if (!scopeCandidates.Contains(string.Empty, StringComparer.Ordinal))
        {
            scopeCandidates.Add(string.Empty);
        }

        string? lastError = null;
        foreach (var candidateScope in scopeCandidates)
        {
            var form = new Dictionary<string, string>
            {
                ["grant_type"] = "password",
                ["client_id"] = clientId,
                ["username"] = username,
                ["password"] = password,
            };
            if (!string.IsNullOrWhiteSpace(clientSecret))
            {
                form["client_secret"] = clientSecret;
            }
            if (!string.IsNullOrWhiteSpace(candidateScope))
            {
                form["scope"] = candidateScope;
            }

            using var response = await client.PostAsync(
                $"{authAuthority.TrimEnd('/')}/protocol/openid-connect/token",
                new FormUrlEncodedContent(form));
            var payload = await response.Content.ReadAsStringAsync();
            if (response.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(payload);
                if (!doc.RootElement.TryGetProperty("access_token", out var accessToken))
                {
                    throw new InvalidOperationException("OIDC token response did not contain access_token.");
                }

                return accessToken.GetString() ?? string.Empty;
            }

            lastError = $"Token request failed ({(int)response.StatusCode}): {payload}";
            var invalidScope = response.StatusCode == HttpStatusCode.BadRequest
                               && payload.Contains("invalid_scope", StringComparison.OrdinalIgnoreCase);
            var hasMore = !string.Equals(candidateScope, scopeCandidates[^1], StringComparison.Ordinal);
            if (invalidScope && hasMore)
            {
                continue;
            }

            throw new InvalidOperationException(lastError);
        }

        throw new InvalidOperationException(lastError ?? "Token request failed.");
    }

    private static async Task<JsonElement> RunSessionScenarioAsync(
        HttpClient http,
        BearerSmokeOptions options,
        bool requireBearer,
        string ownerToken,
        string viewerToken,
        string foreignToken,
        bool multiActorBearerMode)
    {
        static int Count(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Array)
            {
                return value.GetArrayLength();
            }
            if (value.ValueKind == JsonValueKind.Null || value.ValueKind == JsonValueKind.Undefined)
            {
                return 0;
            }
            return 1;
        }

        static void Ensure(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }

        var runId = Guid.NewGuid().ToString("N")[..8];
        var sessionId = $"smoke-{options.Provider.ToLowerInvariant()}-{runId}";
        var participantId = $"participant-observer-{runId}";
        var shareId = $"share-observer-{runId}";
        var invitationShareId = $"invite-controller-{runId}";
        var commandId = $"cmd-noop-{runId}";
        var baseUrl = options.BaseUrl.TrimEnd('/');
        var includeFallbackHeaders = !options.DisableHeaderFallback;

        var ownerHeaders = BuildCallerHeaders(
            subjectId: "user-smoke-owner",
            principalId: "smoke-runner",
            roles: "operator.admin",
            tenantId: "local-tenant",
            organizationId: "local-org",
            includeFallbackHeaders: includeFallbackHeaders,
            bearerToken: requireBearer ? ownerToken : null);
        var controllerHeaders = BuildCallerHeaders(
            subjectId: "user-smoke-controller",
            principalId: "controller-user",
            roles: "operator.viewer",
            tenantId: "local-tenant",
            organizationId: "local-org",
            includeFallbackHeaders: includeFallbackHeaders,
            bearerToken: requireBearer ? (multiActorBearerMode ? viewerToken : ownerToken) : null);
        var foreignHeaders = BuildCallerHeaders(
            subjectId: "user-foreign-viewer",
            principalId: "foreign-user",
            roles: "operator.viewer",
            tenantId: "foreign-tenant",
            organizationId: "foreign-org",
            includeFallbackHeaders: includeFallbackHeaders,
            bearerToken: requireBearer ? (multiActorBearerMode ? foreignToken : ownerToken) : null);

        var agents = await InvokeJsonAsync(
            http,
            HttpMethod.Get,
            $"{baseUrl}/api/v1/agents",
            ownerHeaders,
            null);
        var endpoints = await InvokeJsonAsync(
            http,
            HttpMethod.Get,
            $"{baseUrl}/api/v1/endpoints",
            ownerHeaders,
            null);
        var auditBefore = await InvokeJsonAsync(
            http,
            HttpMethod.Get,
            $"{baseUrl}/api/v1/events/audit",
            ownerHeaders,
            null);
        Ensure(Count(agents) >= 1, "Expected at least one registered agent.");
        Ensure(Count(endpoints) >= 1, "Expected at least one endpoint snapshot.");
        Ensure(Count(auditBefore) >= 1, "Expected at least one audit event before smoke session.");

        var session = await InvokeJsonAsync(
            http,
            HttpMethod.Post,
            $"{baseUrl}/api/v1/sessions",
            ownerHeaders,
            new
            {
                sessionId,
                profile = "UltraLowLatency",
                requestedBy = "smoke-runner",
                targetAgentId = "agent_hidbridge_uart_local",
                targetEndpointId = "endpoint_local_demo",
                shareMode = "Owner",
                tenantId = "local-tenant",
                organizationId = "local-org",
            });
        Ensure(string.Equals(GetStringProperty(session, "sessionId"), sessionId, StringComparison.Ordinal), "Opened session id mismatch.");

        _ = await InvokeJsonAsync(
            http,
            HttpMethod.Post,
            $"{baseUrl}/api/v1/sessions/{Uri.EscapeDataString(sessionId)}/participants",
            ownerHeaders,
            new
            {
                participantId,
                principalId = "observer-user",
                role = "Observer",
                addedBy = "smoke-runner",
                tenantId = "local-tenant",
                organizationId = "local-org",
            });

        _ = await InvokeJsonAsync(
            http,
            HttpMethod.Post,
            $"{baseUrl}/api/v1/sessions/{Uri.EscapeDataString(sessionId)}/shares",
            ownerHeaders,
            new
            {
                shareId,
                principalId = "observer-user",
                grantedBy = "smoke-runner",
                role = "Observer",
                tenantId = "local-tenant",
                organizationId = "local-org",
            });

        _ = await InvokeJsonAsync(
            http,
            HttpMethod.Post,
            $"{baseUrl}/api/v1/sessions/{Uri.EscapeDataString(sessionId)}/invitations/requests",
            controllerHeaders,
            new
            {
                shareId = invitationShareId,
                principalId = "controller-user",
                requestedBy = "controller-user",
                requestedRole = "Controller",
                message = "Need temporary control",
                tenantId = "local-tenant",
                organizationId = "local-org",
            });

        JsonElement viewerDeniedApproval = default;
        if (requireBearer && multiActorBearerMode)
        {
            viewerDeniedApproval = await InvokeExpectedFailureAsync(
                http,
                HttpMethod.Post,
                $"{baseUrl}/api/v1/sessions/{Uri.EscapeDataString(sessionId)}/invitations/{Uri.EscapeDataString(invitationShareId)}/approve",
                controllerHeaders,
                new
                {
                    shareId = invitationShareId,
                    actedBy = "controller-user",
                    grantedRole = "Controller",
                    reason = "viewer should be denied",
                    tenantId = "local-tenant",
                    organizationId = "local-org",
                },
                expectedStatusCode: HttpStatusCode.Forbidden);
            var viewerDeniedCode = GetStringProperty(viewerDeniedApproval, "code");
            Ensure(string.Equals(viewerDeniedCode, "moderation_access_required", StringComparison.OrdinalIgnoreCase),
                $"Expected viewer denial code 'moderation_access_required', got '{viewerDeniedCode}'.");
        }

        _ = await InvokeJsonAsync(
            http,
            HttpMethod.Post,
            $"{baseUrl}/api/v1/sessions/{Uri.EscapeDataString(sessionId)}/invitations/{Uri.EscapeDataString(invitationShareId)}/approve",
            ownerHeaders,
            new
            {
                shareId = invitationShareId,
                actedBy = "smoke-runner",
                grantedRole = "Controller",
                reason = "approved by smoke runner",
                tenantId = "local-tenant",
                organizationId = "local-org",
            });

        _ = await InvokeJsonAsync(
            http,
            HttpMethod.Post,
            $"{baseUrl}/api/v1/sessions/{Uri.EscapeDataString(sessionId)}/shares/{Uri.EscapeDataString(invitationShareId)}/accept",
            controllerHeaders,
            new
            {
                shareId = invitationShareId,
                actedBy = "controller-user",
                reason = "joining approved invitation",
                tenantId = "local-tenant",
                organizationId = "local-org",
            });

        var controllerParticipantId = $"share:{invitationShareId}";
        var controlLease = await InvokeJsonAsync(
            http,
            HttpMethod.Post,
            $"{baseUrl}/api/v1/sessions/{Uri.EscapeDataString(sessionId)}/control/request",
            controllerHeaders,
            new
            {
                participantId = controllerParticipantId,
                requestedBy = "controller-user",
                leaseSeconds = 30,
                reason = "smoke control request",
                tenantId = "local-tenant",
                organizationId = "local-org",
            });
        Ensure(string.Equals(GetStringProperty(controlLease, "principalId"), "controller-user", StringComparison.OrdinalIgnoreCase),
            "Expected controller-user to hold control lease.");

        var requestControlConflict = await InvokeExpectedFailureAsync(
            http,
            HttpMethod.Post,
            $"{baseUrl}/api/v1/sessions/{Uri.EscapeDataString(sessionId)}/control/request",
            ownerHeaders,
            new
            {
                participantId = "owner:smoke-runner",
                requestedBy = "smoke-runner",
                leaseSeconds = 30,
                reason = "owner should wait for active lease",
                tenantId = "local-tenant",
                organizationId = "local-org",
            },
            expectedStatusCode: HttpStatusCode.Conflict);
        Ensure(string.Equals(GetStringProperty(requestControlConflict, "code"), "E_CONTROL_LEASE_HELD_BY_OTHER", StringComparison.OrdinalIgnoreCase),
            "Expected control request conflict code E_CONTROL_LEASE_HELD_BY_OTHER.");

        var notEligibleControlConflict = await InvokeExpectedFailureAsync(
            http,
            HttpMethod.Post,
            $"{baseUrl}/api/v1/sessions/{Uri.EscapeDataString(sessionId)}/control/grant",
            ownerHeaders,
            new
            {
                participantId,
                grantedBy = "smoke-runner",
                leaseSeconds = 30,
                reason = "observer should be ineligible",
                tenantId = "local-tenant",
                organizationId = "local-org",
            },
            expectedStatusCode: HttpStatusCode.Conflict);
        Ensure(string.Equals(GetStringProperty(notEligibleControlConflict, "code"), "E_CONTROL_NOT_ELIGIBLE", StringComparison.OrdinalIgnoreCase),
            "Expected control grant conflict code E_CONTROL_NOT_ELIGIBLE.");

        var releaseMismatchConflict = await InvokeExpectedFailureAsync(
            http,
            HttpMethod.Post,
            $"{baseUrl}/api/v1/sessions/{Uri.EscapeDataString(sessionId)}/control/release",
            ownerHeaders,
            new
            {
                participantId = "owner:smoke-runner",
                actedBy = "smoke-runner",
                reason = "release target mismatch",
                tenantId = "local-tenant",
                organizationId = "local-org",
            },
            expectedStatusCode: HttpStatusCode.Conflict);
        Ensure(string.Equals(GetStringProperty(releaseMismatchConflict, "code"), "E_CONTROL_PARTICIPANT_MISMATCH", StringComparison.OrdinalIgnoreCase),
            "Expected control release conflict code E_CONTROL_PARTICIPANT_MISMATCH.");

        var activeControl = await InvokeJsonAsync(
            http,
            HttpMethod.Get,
            $"{baseUrl}/api/v1/sessions/{Uri.EscapeDataString(sessionId)}/control",
            controllerHeaders,
            null);
        Ensure(string.Equals(GetStringProperty(activeControl, "principalId"), "controller-user", StringComparison.OrdinalIgnoreCase),
            "Expected controller-user in active control snapshot.");

        var commandAck = await InvokeJsonAsync(
            http,
            HttpMethod.Post,
            $"{baseUrl}/api/v1/sessions/{Uri.EscapeDataString(sessionId)}/commands",
            controllerHeaders,
            new
            {
                commandId,
                sessionId,
                channel = "Hid",
                action = "noop",
                args = new
                {
                    principalId = "controller-user",
                    participantId = controllerParticipantId,
                    shareId = invitationShareId,
                },
                timeoutMs = 250,
                idempotencyKey = commandId,
                tenantId = "local-tenant",
                organizationId = "local-org",
            });
        var commandStatus = GetStringProperty(commandAck, "status");
        var commandErrorCode = string.Empty;
        if (commandAck.ValueKind == JsonValueKind.Object
            && commandAck.TryGetProperty("error", out var commandError)
            && commandError.ValueKind == JsonValueKind.Object)
        {
            commandErrorCode = GetStringProperty(commandError, "code");
        }

        var commandTransportIssue =
            string.Equals(commandStatus, "Timeout", StringComparison.OrdinalIgnoreCase)
            || (string.Equals(commandStatus, "Rejected", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(commandErrorCode)
                && (commandErrorCode.StartsWith("E_TRANSPORT_", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(commandErrorCode, "E_COMMAND_EXECUTION_FAILED", StringComparison.OrdinalIgnoreCase)));

        if (!string.Equals(commandStatus, "Applied", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(commandStatus, "Accepted", StringComparison.OrdinalIgnoreCase)
            && !commandTransportIssue)
        {
            throw new InvalidOperationException(
                $"Expected command to be Applied/Accepted (or transport-level Timeout/Rejected), got '{commandStatus}' ({commandErrorCode}).");
        }

        var releasedControl = await InvokeJsonAsync(
            http,
            HttpMethod.Post,
            $"{baseUrl}/api/v1/sessions/{Uri.EscapeDataString(sessionId)}/control/release",
            controllerHeaders,
            new
            {
                participantId = controllerParticipantId,
                actedBy = "controller-user",
                reason = "smoke release",
                tenantId = "local-tenant",
                organizationId = "local-org",
            });
        Ensure(string.Equals(GetStringProperty(releasedControl, "participantId"), controllerParticipantId, StringComparison.OrdinalIgnoreCase),
            "Expected released control participant to match controller.");

        var takeoverControl = await InvokeJsonAsync(
            http,
            HttpMethod.Post,
            $"{baseUrl}/api/v1/sessions/{Uri.EscapeDataString(sessionId)}/control/force-takeover",
            ownerHeaders,
            new
            {
                participantId = "owner:smoke-runner",
                grantedBy = "smoke-runner",
                leaseSeconds = 30,
                reason = "owner takeover",
                tenantId = "local-tenant",
                organizationId = "local-org",
            });
        Ensure(string.Equals(GetStringProperty(takeoverControl, "principalId"), "smoke-runner", StringComparison.OrdinalIgnoreCase),
            "Expected owner takeover principal smoke-runner.");

        JsonElement foreignDenied = default;
        if (requireBearer && multiActorBearerMode)
        {
            foreignDenied = await InvokeExpectedFailureAsync(
                http,
                HttpMethod.Get,
                $"{baseUrl}/api/v1/collaboration/sessions/{Uri.EscapeDataString(sessionId)}/summary",
                foreignHeaders,
                null,
                expectedStatusCode: HttpStatusCode.Forbidden);
            var denialCode = GetStringProperty(foreignDenied, "code");
            if (!string.Equals(denialCode, "tenant_scope_mismatch", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Expected foreign denial code 'tenant_scope_mismatch', got '{denialCode}'.");
            }
        }

        var sessions = await InvokeJsonAsync(
            http,
            HttpMethod.Get,
            $"{baseUrl}/api/v1/sessions",
            ownerHeaders,
            null);
        var participants = await InvokeJsonAsync(
            http,
            HttpMethod.Get,
            $"{baseUrl}/api/v1/sessions/{Uri.EscapeDataString(sessionId)}/participants",
            ownerHeaders,
            null);
        var shares = await InvokeJsonAsync(
            http,
            HttpMethod.Get,
            $"{baseUrl}/api/v1/sessions/{Uri.EscapeDataString(sessionId)}/shares",
            ownerHeaders,
            null);
        var invitations = await InvokeJsonAsync(
            http,
            HttpMethod.Get,
            $"{baseUrl}/api/v1/sessions/{Uri.EscapeDataString(sessionId)}/invitations",
            ownerHeaders,
            null);
        var commandJournal = await InvokeJsonAsync(
            http,
            HttpMethod.Get,
            $"{baseUrl}/api/v1/sessions/{Uri.EscapeDataString(sessionId)}/commands/journal",
            ownerHeaders,
            null);
        var timeline = await InvokeJsonAsync(
            http,
            HttpMethod.Get,
            $"{baseUrl}/api/v1/events/timeline/{Uri.EscapeDataString(sessionId)}?take=20",
            ownerHeaders,
            null);
        var auditAfter = await InvokeJsonAsync(
            http,
            HttpMethod.Get,
            $"{baseUrl}/api/v1/events/audit",
            ownerHeaders,
            null);
        Ensure(Count(sessions) >= 1, "Expected at least one persisted session.");
        Ensure(Count(participants) >= 2, "Expected at least two participants.");
        Ensure(Count(shares) >= 2, "Expected at least two shares.");
        Ensure(Count(invitations) >= 1, "Expected at least one invitation.");
        Ensure(Count(commandJournal) >= 1, "Expected at least one command journal entry.");
        Ensure(Count(timeline) >= 1, "Expected at least one timeline entry.");
        Ensure(Count(auditAfter) > Count(auditBefore), "Expected audit stream growth after smoke actions.");

        var inventoryDashboard = await InvokeJsonAsync(
            http,
            HttpMethod.Get,
            $"{baseUrl}/api/v1/dashboards/inventory",
            ownerHeaders,
            null);
        var auditDashboard = await InvokeJsonAsync(
            http,
            HttpMethod.Get,
            $"{baseUrl}/api/v1/dashboards/audit?take=20",
            ownerHeaders,
            null);
        var telemetryDashboard = await InvokeJsonAsync(
            http,
            HttpMethod.Get,
            $"{baseUrl}/api/v1/dashboards/telemetry?take=20",
            ownerHeaders,
            null);
        var sessionProjection = await InvokeJsonAsync(
            http,
            HttpMethod.Get,
            $"{baseUrl}/api/v1/projections/sessions?principalId=smoke-runner&take=50",
            ownerHeaders,
            null);
        var endpointProjection = await InvokeJsonAsync(
            http,
            HttpMethod.Get,
            $"{baseUrl}/api/v1/projections/endpoints?take=50",
            ownerHeaders,
            null);
        var auditProjection = await InvokeJsonAsync(
            http,
            HttpMethod.Get,
            $"{baseUrl}/api/v1/projections/audit?sessionId={Uri.EscapeDataString(sessionId)}&take=20",
            ownerHeaders,
            null);
        var telemetryProjection = await InvokeJsonAsync(
            http,
            HttpMethod.Get,
            $"{baseUrl}/api/v1/projections/telemetry?sessionId={Uri.EscapeDataString(sessionId)}&scope=command&take=20",
            ownerHeaders,
            null);
        var replayBundle = await InvokeJsonAsync(
            http,
            HttpMethod.Get,
            $"{baseUrl}/api/v1/diagnostics/replay/sessions/{Uri.EscapeDataString(sessionId)}?take=20",
            ownerHeaders,
            null);
        var archiveSummary = await InvokeJsonAsync(
            http,
            HttpMethod.Get,
            $"{baseUrl}/api/v1/diagnostics/archive/summary?sessionId={Uri.EscapeDataString(sessionId)}",
            ownerHeaders,
            null);
        var archiveAudit = await InvokeJsonAsync(
            http,
            HttpMethod.Get,
            $"{baseUrl}/api/v1/diagnostics/archive/audit?sessionId={Uri.EscapeDataString(sessionId)}&take=20",
            ownerHeaders,
            null);
        var archiveTelemetry = await InvokeJsonAsync(
            http,
            HttpMethod.Get,
            $"{baseUrl}/api/v1/diagnostics/archive/telemetry?sessionId={Uri.EscapeDataString(sessionId)}&scope=command&take=20",
            ownerHeaders,
            null);
        var archiveCommands = await InvokeJsonAsync(
            http,
            HttpMethod.Get,
            $"{baseUrl}/api/v1/diagnostics/archive/commands?sessionId={Uri.EscapeDataString(sessionId)}&take=20",
            ownerHeaders,
            null);
        Ensure(GetNumberProperty(inventoryDashboard, "totalAgents") >= 1, "Expected inventory dashboard totalAgents >= 1.");
        Ensure(GetNumberProperty(auditDashboard, "totalEvents") >= 1, "Expected audit dashboard totalEvents >= 1.");
        _ = telemetryDashboard;
        Ensure(GetNumberProperty(auditProjection, "total") >= 1, "Expected audit projection total >= 1.");
        Ensure(GetNumberProperty(telemetryProjection, "total") >= 1, "Expected telemetry projection total >= 1.");
        Ensure(string.Equals(GetStringProperty(replayBundle, "sessionId"), sessionId, StringComparison.OrdinalIgnoreCase), "Replay bundle sessionId mismatch.");
        Ensure(GetNumberProperty(archiveSummary, "commandCount") >= 1, "Expected archive summary commandCount >= 1.");
        Ensure(GetNumberProperty(archiveAudit, "total") >= 1, "Expected archive audit total >= 1.");
        Ensure(GetNumberProperty(archiveTelemetry, "total") >= 1, "Expected archive telemetry total >= 1.");
        Ensure(GetNumberProperty(archiveCommands, "total") >= 1, "Expected archive commands total >= 1.");
        _ = sessionProjection;
        _ = endpointProjection;

        _ = await InvokeJsonAsync(
            http,
            HttpMethod.Post,
            $"{baseUrl}/api/v1/sessions/{Uri.EscapeDataString(sessionId)}/close",
            ownerHeaders,
            new
            {
                sessionId,
                reason = "smoke teardown",
            });

        var sessionsAfterClose = await InvokeJsonAsync(
            http,
            HttpMethod.Get,
            $"{baseUrl}/api/v1/sessions",
            ownerHeaders,
            null);
        if (sessionsAfterClose.ValueKind == JsonValueKind.Array)
        {
            var stillVisible = sessionsAfterClose.EnumerateArray()
                .Any(x => string.Equals(GetStringProperty(x, "sessionId"), sessionId, StringComparison.Ordinal));
            if (stillVisible)
            {
                throw new InvalidOperationException("Expected closed smoke session to disappear from session list.");
            }
        }

        var inventoryAfterClose = await InvokeJsonAsync(
            http,
            HttpMethod.Get,
            $"{baseUrl}/api/v1/dashboards/inventory",
            ownerHeaders,
            null);

        return JsonSerializer.SerializeToElement(new
        {
            sessionId,
            commandId,
            commandStatus,
            commandErrorCode,
            commandTransportIssue,
            provider = options.Provider,
            multiActorBearerMode,
            sessionCount = Count(sessions),
            participantCount = Count(participants),
            shareCount = Count(shares),
            invitationCount = Count(invitations),
            commandJournalCount = Count(commandJournal),
            timelineCount = Count(timeline),
            auditCount = Count(auditAfter),
            requestControlConflictCode = GetStringProperty(requestControlConflict, "code"),
            notEligibleControlConflictCode = GetStringProperty(notEligibleControlConflict, "code"),
            releaseMismatchConflictCode = GetStringProperty(releaseMismatchConflict, "code"),
            foreignDeniedCode = GetStringProperty(foreignDenied, "code"),
            viewerDeniedCode = GetStringProperty(viewerDeniedApproval, "code"),
            inventoryAfterCloseTotalAgents = GetNumberProperty(inventoryAfterClose, "totalAgents"),
        });
    }

    private static async Task<JsonElement> InvokeJsonAsync(
        HttpClient http,
        HttpMethod method,
        string uri,
        IReadOnlyDictionary<string, string> headers,
        object? body)
    {
        using var request = new HttpRequestMessage(method, uri);
        foreach (var header in headers)
        {
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (body is not null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        }

        using var response = await http.SendAsync(request);
        var payload = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"HTTP {method.Method} {uri} failed ({(int)response.StatusCode}): {payload}");
        }

        if (string.IsNullOrWhiteSpace(payload))
        {
            return JsonSerializer.SerializeToElement(new { });
        }

        using var doc = JsonDocument.Parse(payload);
        return doc.RootElement.Clone();
    }

    private static async Task<JsonElement> InvokeExpectedFailureAsync(
        HttpClient http,
        HttpMethod method,
        string uri,
        IReadOnlyDictionary<string, string> headers,
        object? body,
        HttpStatusCode expectedStatusCode)
    {
        using var request = new HttpRequestMessage(method, uri);
        foreach (var header in headers)
        {
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (body is not null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        }

        using var response = await http.SendAsync(request);
        var payload = await response.Content.ReadAsStringAsync();
        if (response.StatusCode != expectedStatusCode)
        {
            throw new InvalidOperationException(
                $"Expected {(int)expectedStatusCode} for {method.Method} {uri}, got {(int)response.StatusCode}: {payload}");
        }

        if (string.IsNullOrWhiteSpace(payload))
        {
            return JsonSerializer.SerializeToElement(new { });
        }

        using var doc = JsonDocument.Parse(payload);
        return doc.RootElement.Clone();
    }

    private static Dictionary<string, string> BuildCallerHeaders(
        string subjectId,
        string principalId,
        string roles,
        string tenantId,
        string organizationId,
        bool includeFallbackHeaders,
        string? bearerToken)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (includeFallbackHeaders)
        {
            headers["X-HidBridge-UserId"] = subjectId;
            headers["X-HidBridge-PrincipalId"] = principalId;
            headers["X-HidBridge-TenantId"] = tenantId;
            headers["X-HidBridge-OrganizationId"] = organizationId;
            headers["X-HidBridge-Role"] = roles;
        }

        if (!string.IsNullOrWhiteSpace(bearerToken))
        {
            headers["Authorization"] = $"Bearer {bearerToken}";
        }

        return headers;
    }

    private static string GetStringProperty(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(name, out var property)
            || property.ValueKind == JsonValueKind.Null)
        {
            return string.Empty;
        }

        return property.ValueKind == JsonValueKind.String
            ? (property.GetString() ?? string.Empty)
            : property.GetRawText();
    }

    private static int GetNumberProperty(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(name, out var property))
        {
            return 0;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var parsed))
        {
            return parsed;
        }

        if (property.ValueKind == JsonValueKind.String && int.TryParse(property.GetString(), out var fromString))
        {
            return fromString;
        }

        return 0;
    }

    private sealed class BearerSmokeOptions
    {
        public string Provider { get; set; } = "Sql";
        public bool ProviderExplicit { get; set; }
        public string Configuration { get; set; } = "Debug";
        public string ConnectionString { get; set; } = "Host=127.0.0.1;Port=5434;Database=hidbridge;Username=hidbridge;Password=hidbridge";
        public string Schema { get; set; } = "hidbridge";
        public string BaseUrl { get; set; } = "http://127.0.0.1:18093";
        public string DataRoot { get; set; } = string.Empty;
        public string AccessToken { get; set; } = string.Empty;
        public bool EnableApiAuth { get; set; } = true;
        public string AuthAuthority { get; set; } = "http://host.docker.internal:18096/realms/hidbridge-dev";
        public string AuthAudience { get; set; } = string.Empty;
        public string TokenClientId { get; set; } = "controlplane-smoke";
        public string TokenClientSecret { get; set; } = string.Empty;
        public string TokenScope { get; set; } = "openid profile email";
        public string TokenUsername { get; set; } = "operator.smoke.admin";
        public string TokenPassword { get; set; } = "ChangeMe123!";
        public string ViewerTokenUsername { get; set; } = "operator.smoke.viewer";
        public string ViewerTokenPassword { get; set; } = "ChangeMe123!";
        public string ForeignTokenUsername { get; set; } = "operator.smoke.foreign";
        public string ForeignTokenPassword { get; set; } = "ChangeMe123!";
        public bool DisableHeaderFallback { get; set; } = true;
        public bool BearerOnly { get; set; } = true;
        public bool NoBuild { get; set; }

        public static bool TryParse(IReadOnlyList<string> args, out BearerSmokeOptions options, out string? error)
        {
            options = new BearerSmokeOptions();
            error = null;
            for (var i = 0; i < args.Count; i++)
            {
                var token = args[i];
                if (string.IsNullOrWhiteSpace(token))
                {
                    continue;
                }
                if (!token.StartsWith("-", StringComparison.Ordinal))
                {
                    error = $"Unexpected token '{token}'. Expected PowerShell-style option.";
                    return false;
                }

                var name = token.TrimStart('-');
                var hasValue = i + 1 < args.Count && !args[i + 1].StartsWith("-", StringComparison.Ordinal);
                var value = hasValue ? args[i + 1] : null;
                switch (name.ToLowerInvariant())
                {
                    case "provider":
                        options.Provider = RequireValue(name, value, ref i, ref hasValue, ref error);
                        options.ProviderExplicit = true;
                        if (!string.Equals(options.Provider, "File", StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(options.Provider, "Sql", StringComparison.OrdinalIgnoreCase))
                        {
                            error = $"Option -{name} supports only 'File' or 'Sql'.";
                        }
                        break;
                    case "configuration": options.Configuration = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "connectionstring": options.ConnectionString = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "schema": options.Schema = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "baseurl": options.BaseUrl = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "dataroot": options.DataRoot = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "accesstoken": options.AccessToken = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "enableapiauth": options.EnableApiAuth = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "authauthority": options.AuthAuthority = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "authaudience": options.AuthAudience = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "tokenclientid": options.TokenClientId = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "tokenclientsecret": options.TokenClientSecret = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "tokenscope": options.TokenScope = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "tokenusername": options.TokenUsername = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "tokenpassword": options.TokenPassword = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "viewertokenusername": options.ViewerTokenUsername = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "viewertokenpassword": options.ViewerTokenPassword = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "foreigntokenusername": options.ForeignTokenUsername = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "foreigntokenpassword": options.ForeignTokenPassword = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "disableheaderfallback": options.DisableHeaderFallback = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "beareronly": options.BearerOnly = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "nobuild": options.NoBuild = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    default:
                        error = $"Unsupported bearer-smoke option '{token}'.";
                        return false;
                }

                if (error is not null)
                {
                    return false;
                }
            }

            return true;
        }

        private static string RequireValue(string name, string? value, ref int index, ref bool hasValue, ref string? error)
        {
            if (!hasValue || string.IsNullOrWhiteSpace(value))
            {
                error = $"Option -{name} requires a value.";
                return string.Empty;
            }
            index++;
            return value;
        }

        private static bool ParseSwitch(string name, string? value, bool hasValue, ref int index, ref string? error)
        {
            if (!hasValue)
            {
                return true;
            }
            if (!TryParseBool(value, out var parsed))
            {
                error = $"Option -{name} requires a boolean value when explicitly provided (true/false).";
                return false;
            }
            index++;
            return parsed;
        }

        private static bool TryParseBool(string? value, out bool parsed)
        {
            parsed = false;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }
            switch (value.Trim().ToLowerInvariant())
            {
                case "1":
                case "true":
                case "yes":
                case "on":
                    parsed = true;
                    return true;
                case "0":
                case "false":
                case "no":
                case "off":
                    parsed = false;
                    return true;
                default:
                    return false;
            }
        }
    }
}
