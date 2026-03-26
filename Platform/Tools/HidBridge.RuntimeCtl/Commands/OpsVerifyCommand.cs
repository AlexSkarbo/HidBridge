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
/// Implements the <c>OpsVerifyCommand</c> RuntimeCtl lane.
/// Execution contract:
/// 1) Parse CLI arguments into strongly typed options.
/// 2) Execute lane-specific orchestration (native RuntimeCtl commands first, script bridge only when explicitly needed).
/// 3) Persist step logs/summaries under <c>Platform/.logs</c> and return process-style exit code semantics (0 = success).
/// </summary>
internal static class OpsVerifyCommand
{
    public static async Task<int> RunAsync(string platformRoot, IReadOnlyList<string> args)
    {
        if (!OpsVerifyOptions.TryParse(args, out var options, out var parseError))
        {
            Console.Error.WriteLine($"ops-verify options error: {parseError}");
            return 1;
        }

        if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out _))
        {
            Console.Error.WriteLine("ops-verify options error: BaseUrl must be an absolute URL.");
            return 1;
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var checks = new List<OpsCheck>();
        var tokenState = TokenState.Initialize(options.AuthAuthority, options.AccessToken);

        JsonDocument? runtime = null;
        JsonDocument? slo = null;
        JsonDocument? audit = null;
        var auditEventCount = 0;

        try
        {
            using var healthClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var healthUri = $"{options.BaseUrl.TrimEnd('/')}/health";
            using var healthResponse = await healthClient.GetAsync(healthUri);
            var healthJson = await ReadJsonOrThrowAsync(healthResponse);
            var healthStatus = ReadString(healthJson.RootElement, "status") ?? string.Empty;
            var isHealthy = string.Equals(healthStatus, "ok", StringComparison.OrdinalIgnoreCase);
            AddCheck(checks, "api.health", isHealthy ? "PASS" : "FAIL",
                isHealthy ? "API health endpoint is reachable." : "API health endpoint returned non-ok status.",
                healthStatus, "ok");

            var windowMinutes = Clamp(options.SloWindowMinutes, 5, 24 * 60);
            runtime = await InvokeApiJsonAsync(http, options, tokenState, "/api/v1/runtime/uart");
            slo = await InvokeApiJsonAsync(http, options, tokenState, $"/api/v1/diagnostics/transport/slo?windowMinutes={windowMinutes}");
            audit = await InvokeApiJsonAsync(http, options, tokenState, "/api/v1/events/audit");

            EvaluateSecurityChecks(checks, runtime.RootElement, options);
            EvaluateSloChecks(checks, slo.RootElement, options);

            auditEventCount = audit.RootElement.ValueKind == JsonValueKind.Array ? audit.RootElement.GetArrayLength() : 0;
            EvaluateAuditChecks(checks, audit.RootElement, options);
        }
        catch (Exception ex)
        {
            AddCheck(checks, "ops.verify.exception", "FAIL", ex.Message, null, null);
        }

        var failedCount = checks.Count(static c => string.Equals(c.Status, "FAIL", StringComparison.OrdinalIgnoreCase));
        var warnCount = checks.Count(static c => string.Equals(c.Status, "WARN", StringComparison.OrdinalIgnoreCase));
        var passCount = checks.Count(static c => string.Equals(c.Status, "PASS", StringComparison.OrdinalIgnoreCase));
        var resultStatus = failedCount > 0 ? "FAIL" : warnCount > 0 ? "WARN" : "PASS";

        var summary = new
        {
            generatedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
            baseUrl = options.BaseUrl,
            status = resultStatus,
            checkCounts = new
            {
                pass = passCount,
                warn = warnCount,
                fail = failedCount,
            },
            slo = new
            {
                status = slo is null ? null : ReadString(slo.RootElement, "status"),
                relayCommandCount = slo is null ? null : ReadScalar(slo.RootElement, "relayCommandCount"),
                relayTimeoutCount = slo is null ? null : ReadScalar(slo.RootElement, "relayTimeoutCount"),
                ackTimeoutRate = slo is null ? null : ReadScalar(slo.RootElement, "ackTimeoutRate"),
                relayReadyLatencyP95Ms = slo is null ? null : ReadScalar(slo.RootElement, "relayReadyLatencyP95Ms"),
                reconnectFrequencyPerHour = slo is null ? null : ReadScalar(slo.RootElement, "reconnectFrequencyPerHour"),
            },
            security = new
            {
                authEnabled = runtime is null ? null : ReadScalar(runtime.RootElement, "authentication", "enabled"),
                allowHeaderFallback = runtime is null ? null : ReadScalar(runtime.RootElement, "authentication", "allowHeaderFallback"),
                bearerOnlyPrefixes = runtime is null ? Array.Empty<string>() : ReadStringArray(runtime.RootElement, "authentication", "bearerOnlyPrefixes"),
                callerContextRequiredPrefixes = runtime is null ? Array.Empty<string>() : ReadStringArray(runtime.RootElement, "authentication", "callerContextRequiredPrefixes"),
                headerFallbackDisabledPatterns = runtime is null ? Array.Empty<string>() : ReadStringArray(runtime.RootElement, "authentication", "headerFallbackDisabledPatterns"),
                auditEventCount,
            },
            checks,
        };

        if (!string.IsNullOrWhiteSpace(options.OutputJsonPath))
        {
            var outputPath = Path.IsPathRooted(options.OutputJsonPath)
                ? options.OutputJsonPath
                : Path.Combine(platformRoot, options.OutputJsonPath);
            var parent = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrWhiteSpace(parent))
            {
                Directory.CreateDirectory(parent);
            }

            await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
        }

        PrintChecksTable(options.BaseUrl, resultStatus, options.OutputJsonPath, checks);
        return failedCount > 0 ? 1 : 0;
    }

    private static void EvaluateSecurityChecks(List<OpsCheck> checks, JsonElement runtimeRoot, OpsVerifyOptions options)
    {
        var authEnabled = ReadBoolean(runtimeRoot, "authentication", "enabled");
        if (authEnabled)
        {
            AddCheck(checks, "security.auth.enabled", "PASS", "API authentication is enabled.", authEnabled, true);
        }
        else if (options.AllowAuthDisabled)
        {
            AddCheck(checks, "security.auth.enabled", "WARN", "API authentication is disabled by runtime configuration.", authEnabled, true);
        }
        else
        {
            AddCheck(checks, "security.auth.enabled", "FAIL", "API authentication is disabled and AllowAuthDisabled was not provided.", authEnabled, true);
        }

        var headerFallback = ReadBoolean(runtimeRoot, "authentication", "allowHeaderFallback");
        if (!headerFallback)
        {
            AddCheck(checks, "security.header-fallback", "PASS", "Header fallback is disabled for API auth.", headerFallback, false);
        }
        else
        {
            AddCheck(checks, "security.header-fallback", options.FailOnSecurityWarning ? "FAIL" : "WARN", "Header fallback is enabled; strict bearer posture is not fully enforced.", headerFallback, false);
        }

        var bearerPrefixes = ReadStringArray(runtimeRoot, "authentication", "bearerOnlyPrefixes");
        var requiredBearerPrefixes = new[] { "/api/v1/sessions", "/api/v1/diagnostics" };
        var missingBearer = requiredBearerPrefixes.Where(p => !bearerPrefixes.Contains(p, StringComparer.Ordinal)).ToArray();
        if (missingBearer.Length == 0)
        {
            AddCheck(checks, "security.bearer-only-coverage", "PASS", "Bearer-only coverage includes required prefixes.", string.Join(", ", bearerPrefixes), string.Join(", ", requiredBearerPrefixes));
        }
        else
        {
            AddCheck(checks, "security.bearer-only-coverage", options.FailOnSecurityWarning ? "FAIL" : "WARN", $"Missing bearer-only prefixes: {string.Join(", ", missingBearer)}.", string.Join(", ", bearerPrefixes), string.Join(", ", requiredBearerPrefixes));
        }

        var callerPrefixes = ReadStringArray(runtimeRoot, "authentication", "callerContextRequiredPrefixes");
        var hasSessionCallerContext = callerPrefixes.Contains("/api/v1/sessions", StringComparer.Ordinal);
        if (hasSessionCallerContext)
        {
            AddCheck(checks, "security.caller-context-coverage", "PASS", "Caller-context coverage includes /api/v1/sessions.", string.Join(", ", callerPrefixes), "/api/v1/sessions");
        }
        else
        {
            AddCheck(checks, "security.caller-context-coverage", options.FailOnSecurityWarning ? "FAIL" : "WARN", "Caller-context coverage does not include /api/v1/sessions.", string.Join(", ", callerPrefixes), "/api/v1/sessions");
        }

        var fallbackPatterns = ReadStringArray(runtimeRoot, "authentication", "headerFallbackDisabledPatterns");
        var hasCommands = fallbackPatterns.Any(static p => p.Contains("commands", StringComparison.OrdinalIgnoreCase));
        var hasControl = fallbackPatterns.Any(static p => p.Contains("control", StringComparison.OrdinalIgnoreCase));
        var hasInvitations = fallbackPatterns.Any(static p => p.Contains("invitations", StringComparison.OrdinalIgnoreCase));
        if (hasCommands && hasControl && hasInvitations)
        {
            AddCheck(checks, "security.fallback-disabled-patterns", "PASS", "Header-fallback disabled patterns cover commands/control/invitations mutation surfaces.", string.Join(", ", fallbackPatterns), "contains commands + control + invitations patterns");
        }
        else
        {
            var missing = new List<string>();
            if (!hasCommands) missing.Add("commands");
            if (!hasControl) missing.Add("control");
            if (!hasInvitations) missing.Add("invitations");
            AddCheck(checks, "security.fallback-disabled-patterns", options.FailOnSecurityWarning ? "FAIL" : "WARN", $"Missing fallback-disabled pattern coverage: {string.Join(", ", missing)}.", string.Join(", ", fallbackPatterns), "contains commands + control + invitations patterns");
        }
    }

    private static void EvaluateSloChecks(List<OpsCheck> checks, JsonElement sloRoot, OpsVerifyOptions options)
    {
        var sloStatus = ReadString(sloRoot, "status") ?? string.Empty;
        if (string.Equals(sloStatus, "critical", StringComparison.OrdinalIgnoreCase))
        {
            AddCheck(checks, "slo.overall-status", "FAIL", "Transport SLO status is critical.", sloStatus, "healthy|warning");
        }
        else if (string.Equals(sloStatus, "warning", StringComparison.OrdinalIgnoreCase))
        {
            AddCheck(checks, "slo.overall-status", options.FailOnSloWarning ? "FAIL" : "WARN", "Transport SLO status is warning.", sloStatus, "healthy");
        }
        else
        {
            AddCheck(checks, "slo.overall-status", "PASS", "Transport SLO status is healthy.", sloStatus, "healthy");
        }

        EvaluateThresholdCheck(checks, "slo.ack-timeout-rate", "ACK timeout rate", options,
            ReadBoolean(sloRoot, "breaches", "ackTimeoutRateCritical"),
            ReadBoolean(sloRoot, "breaches", "ackTimeoutRateWarn"),
            ReadScalar(sloRoot, "ackTimeoutRate"),
            ReadScalar(sloRoot, "thresholds", "ackTimeoutRateCritical"),
            ReadScalar(sloRoot, "thresholds", "ackTimeoutRateWarn"));

        EvaluateThresholdCheck(checks, "slo.relay-ready-latency-p95", "Relay-ready p95 latency", options,
            ReadBoolean(sloRoot, "breaches", "relayReadyLatencyP95Critical"),
            ReadBoolean(sloRoot, "breaches", "relayReadyLatencyP95Warn"),
            ReadScalar(sloRoot, "relayReadyLatencyP95Ms"),
            ReadScalar(sloRoot, "thresholds", "relayReadyLatencyCriticalMs"),
            ReadScalar(sloRoot, "thresholds", "relayReadyLatencyWarnMs"));

        EvaluateThresholdCheck(checks, "slo.reconnect-frequency", "Reconnect frequency", options,
            ReadBoolean(sloRoot, "breaches", "reconnectFrequencyCriticalPerHour"),
            ReadBoolean(sloRoot, "breaches", "reconnectFrequencyWarnPerHour"),
            ReadScalar(sloRoot, "reconnectFrequencyPerHour"),
            ReadScalar(sloRoot, "thresholds", "reconnectFrequencyCriticalPerHour"),
            ReadScalar(sloRoot, "thresholds", "reconnectFrequencyWarnPerHour"));
    }

    private static void EvaluateThresholdCheck(
        List<OpsCheck> checks,
        string checkName,
        string metricTitle,
        OpsVerifyOptions options,
        bool isCritical,
        bool isWarning,
        object? observedValue,
        object? criticalThreshold,
        object? warningThreshold)
    {
        if (isCritical)
        {
            AddCheck(checks, checkName, "FAIL", $"{metricTitle} crossed critical threshold.", observedValue, criticalThreshold is null ? "< critical" : $"< {criticalThreshold}");
            return;
        }

        if (isWarning)
        {
            AddCheck(checks, checkName, options.FailOnSloWarning ? "FAIL" : "WARN", $"{metricTitle} crossed warning threshold.", observedValue, warningThreshold is null ? "< warning" : $"< {warningThreshold}");
            return;
        }

        AddCheck(checks, checkName, "PASS", $"{metricTitle} is within threshold.", observedValue, warningThreshold is null ? "< warning" : $"< {warningThreshold}");
    }

    private static void EvaluateAuditChecks(List<OpsCheck> checks, JsonElement auditRoot, OpsVerifyOptions options)
    {
        if (auditRoot.ValueKind != JsonValueKind.Array || auditRoot.GetArrayLength() == 0)
        {
            var status = options.RequireAuditTrailCategories || options.FailOnSecurityWarning ? "FAIL" : "WARN";
            AddCheck(checks, "security.audit-trail-presence", status, "Audit event stream is empty.", 0, "> 0");
            return;
        }

        var sampleTake = Clamp(options.AuditSampleTake, 1, 2000);
        var events = auditRoot.EnumerateArray().Select(static e => e.Clone()).ToList();
        var sampled = events
            .OrderByDescending(e => ParseCreatedAtUtc(e))
            .Take(sampleTake)
            .ToArray();

        var categories = sampled
            .Select(static e => ReadString(e, "category"))
            .Where(static c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.Ordinal)
            .Select(static c => c!)
            .ToArray()!;

        var hasCommand = categories.Contains("session.command", StringComparer.Ordinal);
        var hasAck = categories.Contains("transport.ack", StringComparer.Ordinal);
        var hasLease = categories.Any(static c => c.StartsWith("session.control", StringComparison.Ordinal));

        if (hasCommand && hasAck && hasLease)
        {
            AddCheck(checks, "security.audit-trail-categories", "PASS", "Audit stream contains command/lease/ack categories.", string.Join(", ", categories), "session.command + session.control* + transport.ack");
            return;
        }

        var missing = new List<string>();
        if (!hasCommand) missing.Add("session.command");
        if (!hasLease) missing.Add("session.control*");
        if (!hasAck) missing.Add("transport.ack");
        var statusFail = options.RequireAuditTrailCategories || options.FailOnSecurityWarning ? "FAIL" : "WARN";
        AddCheck(checks, "security.audit-trail-categories", statusFail, $"Audit stream is missing categories: {string.Join(", ", missing)}.", string.Join(", ", categories), "session.command + session.control* + transport.ack");
    }

    private static DateTimeOffset ParseCreatedAtUtc(JsonElement element)
    {
        var raw = ReadString(element, "createdAtUtc");
        return DateTimeOffset.TryParse(raw, out var parsed) ? parsed : DateTimeOffset.MinValue;
    }

    private static async Task<JsonDocument> InvokeApiJsonAsync(HttpClient client, OpsVerifyOptions options, TokenState tokenState, string relativePath)
    {
        var uri = $"{options.BaseUrl.TrimEnd('/')}{relativePath}";
        var tokenAttempted = false;

        while (true)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (!string.IsNullOrWhiteSpace(tokenState.ResolvedAccessToken))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenState.ResolvedAccessToken);
            }

            using var response = await client.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                return await ReadJsonOrThrowAsync(response);
            }

            var body = await SafeReadBodyAsync(response);
            var isUnauthorized = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
            if (!isUnauthorized || tokenAttempted)
            {
                if (tokenAttempted && tokenState.MoveNextAuthority())
                {
                    tokenState.ResolvedAccessToken = null;
                    tokenAttempted = false;
                    continue;
                }

                throw new HttpRequestException(
                    $"Request to {uri} failed with status {(int)response.StatusCode} ({response.StatusCode}).{(string.IsNullOrWhiteSpace(body) ? string.Empty : $" {body}")}");
            }

            tokenState.ResolvedAccessToken = await GetResolvedAccessTokenAsync(client, options, tokenState);
            tokenAttempted = true;
        }
    }

    private static async Task<string> GetResolvedAccessTokenAsync(HttpClient client, OpsVerifyOptions options, TokenState tokenState)
    {
        if (!string.IsNullOrWhiteSpace(tokenState.ResolvedAccessToken))
        {
            return tokenState.ResolvedAccessToken;
        }

        if (string.Equals(options.AccessToken, "none", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Access token is required for this endpoint, but AccessToken=none disables automatic token acquisition.");
        }

        if (!string.IsNullOrWhiteSpace(options.AccessToken)
            && !string.Equals(options.AccessToken, "auto", StringComparison.OrdinalIgnoreCase))
        {
            tokenState.ResolvedAccessToken = options.AccessToken;
            return tokenState.ResolvedAccessToken;
        }

        var scopeCandidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(options.TokenScope))
        {
            scopeCandidates.Add(options.TokenScope);
        }
        if (!scopeCandidates.Contains("openid", StringComparer.Ordinal))
        {
            scopeCandidates.Add("openid");
        }
        if (!scopeCandidates.Contains(string.Empty, StringComparer.Ordinal))
        {
            scopeCandidates.Add(string.Empty);
        }

        string lastError = "Keycloak token request failed.";
        do
        {
            var authority = tokenState.GetCurrentAuthority();
            var tokenEndpoint = $"{authority}/protocol/openid-connect/token";

            for (var i = 0; i < scopeCandidates.Count; i++)
            {
                var scope = scopeCandidates[i];
                var formPairs = new List<KeyValuePair<string, string>>
                {
                    new("grant_type", "password"),
                    new("client_id", options.TokenClientId),
                    new("username", options.TokenUsername),
                    new("password", options.TokenPassword),
                };
                if (!string.IsNullOrWhiteSpace(options.TokenClientSecret))
                {
                    formPairs.Add(new KeyValuePair<string, string>("client_secret", options.TokenClientSecret));
                }
                if (!string.IsNullOrWhiteSpace(scope))
                {
                    formPairs.Add(new KeyValuePair<string, string>("scope", scope));
                }

                using var request = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint)
                {
                    Content = new FormUrlEncodedContent(formPairs),
                };
                using var response = await client.SendAsync(request);
                var payload = await SafeReadBodyAsync(response);
                if (response.IsSuccessStatusCode)
                {
                    using var tokenJson = JsonDocument.Parse(payload);
                    var accessToken = ReadString(tokenJson.RootElement, "access_token");
                    if (string.IsNullOrWhiteSpace(accessToken))
                    {
                        throw new InvalidOperationException("Token response did not contain access_token.");
                    }

                    tokenState.ResolvedAccessToken = accessToken;
                    return accessToken;
                }

                lastError = $"Keycloak token request failed via {tokenEndpoint}: {(int)response.StatusCode} {response.StatusCode}";
                if (!string.IsNullOrWhiteSpace(payload))
                {
                    lastError += Environment.NewLine + payload;
                }

                var hasNextScope = i < scopeCandidates.Count - 1;
                var invalidScope = payload.Contains("invalid_scope", StringComparison.OrdinalIgnoreCase);
                if (hasNextScope && (invalidScope || response.StatusCode == HttpStatusCode.BadRequest))
                {
                    continue;
                }

                break;
            }
        }
        while (tokenState.MoveNextAuthority());

        throw new InvalidOperationException(lastError);
    }

    private static async Task<JsonDocument> ReadJsonOrThrowAsync(HttpResponseMessage response)
    {
        var body = await SafeReadBodyAsync(response);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Request failed with status {(int)response.StatusCode} ({response.StatusCode}).{(string.IsNullOrWhiteSpace(body) ? string.Empty : $" {body}")}");
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            throw new InvalidOperationException("Response body is empty.");
        }

        return JsonDocument.Parse(body);
    }

    private static async Task<string> SafeReadBodyAsync(HttpResponseMessage response)
        => response.Content is null ? string.Empty : await response.Content.ReadAsStringAsync();

    private static int Clamp(int value, int min, int max) => Math.Max(min, Math.Min(max, value));

    private static void AddCheck(List<OpsCheck> checks, string name, string status, string message, object? observed, object? expected)
        => checks.Add(new OpsCheck(name, status, message, observed, expected));

    private static bool ReadBoolean(JsonElement element, params string[] path)
    {
        if (!TryGetPath(element, out var value, path))
        {
            return false;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => value.TryGetInt64(out var i) && i != 0,
            JsonValueKind.String => bool.TryParse(value.GetString(), out var parsed) && parsed,
            _ => false,
        };
    }

    private static string? ReadString(JsonElement element, params string[] path)
    {
        if (!TryGetPath(element, out var value, path))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    private static object? ReadScalar(JsonElement element, params string[] path)
    {
        if (!TryGetPath(element, out var value, path))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.TryGetInt64(out var i) ? i : value.TryGetDouble(out var d) ? d : value.GetRawText(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => value.GetRawText(),
        };
    }

    private static string[] ReadStringArray(JsonElement element, params string[] path)
    {
        if (!TryGetPath(element, out var value, path) || value.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        return value.EnumerateArray()
            .Where(static e => e.ValueKind == JsonValueKind.String)
            .Select(static e => e.GetString())
            .Where(static s => !string.IsNullOrWhiteSpace(s))
            .Select(static s => s!)
            .ToArray();
    }

    private static bool TryGetPath(JsonElement element, out JsonElement result, params string[] path)
    {
        result = element;
        foreach (var segment in path)
        {
            if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty(segment, out result))
            {
                return false;
            }
        }

        return true;
    }

    private static void PrintChecksTable(string baseUrl, string resultStatus, string outputJsonPath, IReadOnlyList<OpsCheck> checks)
    {
        Console.WriteLine();
        Console.WriteLine("=== Ops SLO + Security Verify ===");
        Console.WriteLine($"Base URL: {baseUrl}");
        Console.WriteLine($"Status:   {resultStatus}");
        if (!string.IsNullOrWhiteSpace(outputJsonPath))
        {
            Console.WriteLine($"Summary:  {outputJsonPath}");
        }
        Console.WriteLine();

        const int nameWidth = 34;
        const int statusWidth = 6;
        Console.WriteLine($"{Pad("Name", nameWidth)} {Pad("Status", statusWidth)} Observed");
        Console.WriteLine($"{new string('-', nameWidth)} {new string('-', statusWidth)} --------");
        foreach (var check in checks)
        {
            var observed = check.Observed?.ToString() ?? string.Empty;
            if (observed.Length > 90)
            {
                observed = observed[..90] + "...";
            }

            Console.WriteLine($"{Pad(check.Name, nameWidth)} {Pad(check.Status, statusWidth)} {observed}");
        }
    }

    private static string Pad(string value, int width)
        => value.Length >= width ? value[..width] : value.PadRight(width, ' ');

    private sealed class OpsVerifyOptions
    {
        public string BaseUrl { get; set; } = "http://127.0.0.1:18093";
        public int SloWindowMinutes { get; set; } = 60;
        public string AccessToken { get; set; } = "auto";
        public string AuthAuthority { get; set; } = "http://host.docker.internal:18096/realms/hidbridge-dev";
        public string TokenClientId { get; set; } = "controlplane-smoke";
        public string TokenClientSecret { get; set; } = string.Empty;
        public string TokenScope { get; set; } = "openid profile email";
        public string TokenUsername { get; set; } = "operator.smoke.admin";
        public string TokenPassword { get; set; } = "ChangeMe123!";
        public bool AllowAuthDisabled { get; set; }
        public bool FailOnSloWarning { get; set; }
        public bool FailOnSecurityWarning { get; set; }
        public bool RequireAuditTrailCategories { get; set; }
        public int AuditSampleTake { get; set; } = 250;
        public string OutputJsonPath { get; set; } = string.Empty;

        public static bool TryParse(IReadOnlyList<string> args, out OpsVerifyOptions options, out string? error)
        {
            options = new OpsVerifyOptions();
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
                    error = $"Unexpected token '{token}'. Expected PowerShell-style option (e.g. -BaseUrl).";
                    return false;
                }

                var name = token.TrimStart('-');
                var hasValue = i + 1 < args.Count && !args[i + 1].StartsWith("-", StringComparison.Ordinal);
                string? value = hasValue ? args[i + 1] : null;

                switch (name.ToLowerInvariant())
                {
                    case "baseurl": options.BaseUrl = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "slowindowminutes": options.SloWindowMinutes = ParseInt(name, value, hasValue, ref i, ref error); break;
                    case "accesstoken": options.AccessToken = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "authauthority": options.AuthAuthority = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "tokenclientid": options.TokenClientId = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "tokenclientsecret": options.TokenClientSecret = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "tokenscope": options.TokenScope = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "tokenusername": options.TokenUsername = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "tokenpassword": options.TokenPassword = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "allowauthdisabled": options.AllowAuthDisabled = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "failonslowarning": options.FailOnSloWarning = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "failonsecuritywarning": options.FailOnSecurityWarning = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "requireaudittrailcategories": options.RequireAuditTrailCategories = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "auditsampletake": options.AuditSampleTake = ParseInt(name, value, hasValue, ref i, ref error); break;
                    case "outputjsonpath": options.OutputJsonPath = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    default:
                        error = $"Unsupported ops-verify option '{token}'.";
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

        private static int ParseInt(string name, string? value, bool hasValue, ref int index, ref string? error)
        {
            var raw = RequireValue(name, value, ref index, ref hasValue, ref error);
            if (error is not null)
            {
                return 0;
            }

            if (!int.TryParse(raw, out var parsed))
            {
                error = $"Option -{name} requires an integer value.";
                return 0;
            }

            return parsed;
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

    private sealed class TokenState
    {
        private readonly List<string> _authorities;
        private int _cursor;

        private TokenState(List<string> authorities, string? resolvedAccessToken)
        {
            _authorities = authorities;
            ResolvedAccessToken = resolvedAccessToken;
            _cursor = 0;
        }

        public string? ResolvedAccessToken { get; set; }

        public string GetCurrentAuthority()
            => _authorities[Math.Clamp(_cursor, 0, _authorities.Count - 1)];

        public bool MoveNextAuthority()
        {
            if (_cursor < _authorities.Count - 1)
            {
                _cursor++;
                return true;
            }

            return false;
        }

        public static TokenState Initialize(string authAuthority, string accessToken)
        {
            var authorities = new List<string>();
            if (!string.IsNullOrWhiteSpace(authAuthority))
            {
                authorities.Add(authAuthority.TrimEnd('/'));
            }

            if (Uri.TryCreate(authAuthority, UriKind.Absolute, out var parsed))
            {
                var pathAndQuery = string.IsNullOrWhiteSpace(parsed.PathAndQuery) ? "/realms/hidbridge-dev" : parsed.PathAndQuery;
                var fallbackHosts = new List<string>();
                if (string.Equals(parsed.Host, "host.docker.internal", StringComparison.OrdinalIgnoreCase))
                {
                    fallbackHosts.Add("127.0.0.1");
                    fallbackHosts.Add("localhost");
                }
                else if (string.Equals(parsed.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
                      || string.Equals(parsed.Host, "localhost", StringComparison.OrdinalIgnoreCase))
                {
                    fallbackHosts.Add("host.docker.internal");
                }

                foreach (var fallbackHost in fallbackHosts)
                {
                    var builder = new UriBuilder(parsed)
                    {
                        Host = fallbackHost,
                        Path = pathAndQuery.TrimStart('/'),
                    };
                    var candidate = builder.Uri.AbsoluteUri.TrimEnd('/');
                    if (!authorities.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                    {
                        authorities.Add(candidate);
                    }
                }
            }

            if (authorities.Count == 0)
            {
                authorities.Add("http://127.0.0.1:18096/realms/hidbridge-dev");
            }

            string? resolved = null;
            if (!string.IsNullOrWhiteSpace(accessToken)
                && !string.Equals(accessToken, "auto", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(accessToken, "none", StringComparison.OrdinalIgnoreCase))
            {
                resolved = accessToken;
            }

            return new TokenState(authorities, resolved);
        }
    }

    private sealed record OpsCheck(string Name, string Status, string Message, object? Observed, object? Expected);
}
