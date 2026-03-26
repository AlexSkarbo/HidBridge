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
/// Implements the <c>DemoSeedCommand</c> RuntimeCtl lane.
/// Execution contract:
/// 1) Parse CLI arguments into strongly typed options.
/// 2) Execute lane-specific orchestration (native RuntimeCtl commands first, script bridge only when explicitly needed).
/// 3) Persist step logs/summaries under <c>Platform/.logs</c> and return process-style exit code semantics (0 = success).
/// </summary>
internal static class DemoSeedCommand
{
    public static async Task<int> RunAsync(string platformRoot, IReadOnlyList<string> args)
    {
        if (!DemoSeedOptions.TryParse(args, out var options, out var parseError))
        {
            Console.Error.WriteLine($"demo-seed options error: {parseError}");
            return 1;
        }

        try
        {
            var accessToken = options.AccessToken;
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                accessToken = await RequestTokenAsync(options);
            }

            var callerHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Authorization"] = $"Bearer {accessToken}",
                ["X-HidBridge-UserId"] = options.PrincipalId,
                ["X-HidBridge-PrincipalId"] = options.PrincipalId,
                ["X-HidBridge-TenantId"] = options.TenantId,
                ["X-HidBridge-OrganizationId"] = options.OrganizationId,
                ["X-HidBridge-Role"] = "operator.admin,operator.moderator,operator.viewer",
            };

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            var sessions = await InvokeJsonArrayAsync(http, HttpMethod.Get, $"{options.BaseUrl.TrimEnd('/')}/api/v1/sessions", callerHeaders, null);
            var activeSessions = sessions.Where(IsActiveSession).ToArray();
            var sessionsToClose = sessions.Where(IsClosableSession).ToArray();

            var closedSessionIds = new List<string>();
            if (!options.SkipCloseActiveSessions)
            {
                foreach (var session in sessionsToClose)
                {
                    var sessionId = GetSessionId(session);
                    if (string.IsNullOrWhiteSpace(sessionId))
                    {
                        continue;
                    }

                    var closeBody = JsonSerializer.SerializeToElement(new
                    {
                        sessionId,
                        reason = "demo-seed cleanup",
                    });
                    _ = await InvokeJsonElementAsync(
                        http,
                        HttpMethod.Post,
                        $"{options.BaseUrl.TrimEnd('/')}/api/v1/sessions/{Uri.EscapeDataString(sessionId)}/close",
                        callerHeaders,
                        closeBody);
                    closedSessionIds.Add(sessionId);
                }
            }

            JsonElement inventory = default;
            JsonElement[] endpoints = Array.Empty<JsonElement>();
            JsonElement? readyEndpoint = null;
            var timeoutSeconds = Math.Max(1, options.ReadyEndpointTimeoutSec);
            var pollIntervalMs = Math.Max(100, options.ReadyEndpointPollIntervalMs);
            var deadline = DateTimeOffset.UtcNow.AddSeconds(timeoutSeconds);

            while (true)
            {
                inventory = await InvokeJsonElementAsync(http, HttpMethod.Get, $"{options.BaseUrl.TrimEnd('/')}/api/v1/dashboards/inventory", callerHeaders, null);
                endpoints = GetArrayProperty(inventory, "endpoints");
                readyEndpoint = endpoints.FirstOrDefault(endpoint => IsEndpointReady(endpoint, sessions));

                if (readyEndpoint.HasValue)
                {
                    break;
                }

                if (DateTimeOffset.UtcNow >= deadline)
                {
                    break;
                }

                await Task.Delay(pollIntervalMs);
            }

            if (!readyEndpoint.HasValue)
            {
                var endpointSample = string.Join(", ", endpoints.Take(5).Select(endpoint =>
                {
                    var endpointId = GetStringProperty(endpoint, "endpointId");
                    var activeSessionId = GetStringProperty(endpoint, "activeSessionId");
                    if (string.IsNullOrWhiteSpace(activeSessionId))
                    {
                        return $"{endpointId}:idle";
                    }
                    var linkedSession = sessions.FirstOrDefault(x => string.Equals(GetSessionId(x), activeSessionId, StringComparison.Ordinal));
                    var linkedState = linkedSession.ValueKind == JsonValueKind.Undefined ? "stale" : GetSessionState(linkedSession);
                    return $"{endpointId}:busy({activeSessionId},state={linkedState})";
                }));

                throw new InvalidOperationException(
                    $"Demo seed did not find any idle endpoint within {timeoutSeconds} second(s). " +
                    $"Active sessions in scope: {activeSessions.Length}; non-terminal sessions in scope: {sessionsToClose.Length}; " +
                    $"endpoints visible: {endpoints.Length}; endpoint sample: {endpointSample}.");
            }

            Console.WriteLine();
            Console.WriteLine("=== Demo Seed Summary ===");
            Console.WriteLine($"BaseUrl: {options.BaseUrl}");
            Console.WriteLine($"Active sessions found: {activeSessions.Length}");
            Console.WriteLine($"Non-terminal sessions found: {sessionsToClose.Length}");
            Console.WriteLine($"Closed sessions: {closedSessionIds.Count}");
            Console.WriteLine($"Endpoints visible: {endpoints.Length}");
            if (closedSessionIds.Count > 0)
            {
                Console.WriteLine($"Closed session IDs: {string.Join(", ", closedSessionIds)}");
            }
            Console.WriteLine($"Ready endpoint: {GetStringProperty(readyEndpoint.Value, "endpointId")}");
            Console.WriteLine($"Ready endpoint agent: {GetStringProperty(readyEndpoint.Value, "agentId")}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static bool IsActiveSession(JsonElement session) =>
        string.Equals(GetSessionState(session), "Active", StringComparison.OrdinalIgnoreCase);

    private static bool IsClosableSession(JsonElement session)
    {
        var sessionId = GetSessionId(session);
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        var state = GetSessionState(session);
        if (string.IsNullOrWhiteSpace(state))
        {
            return true;
        }

        return !string.Equals(state, "Ended", StringComparison.OrdinalIgnoreCase)
               && !string.Equals(state, "Failed", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsEndpointReady(JsonElement endpoint, IReadOnlyList<JsonElement> sessions)
    {
        var activeSessionId = GetStringProperty(endpoint, "activeSessionId");
        if (string.IsNullOrWhiteSpace(activeSessionId))
        {
            return true;
        }

        var linkedSession = sessions.FirstOrDefault(x => string.Equals(GetSessionId(x), activeSessionId, StringComparison.Ordinal));
        if (linkedSession.ValueKind == JsonValueKind.Undefined)
        {
            return true;
        }

        var state = GetSessionState(linkedSession);
        return string.Equals(state, "Ended", StringComparison.OrdinalIgnoreCase)
               || string.Equals(state, "Failed", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetSessionId(JsonElement session)
    {
        var sessionId = GetStringProperty(session, "sessionId");
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            return sessionId;
        }
        return GetStringProperty(session, "SessionId");
    }

    private static string GetSessionState(JsonElement session)
    {
        if (session.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }
        if (!session.TryGetProperty("state", out var state) || state.ValueKind == JsonValueKind.Null)
        {
            return string.Empty;
        }
        return state.ValueKind == JsonValueKind.String ? (state.GetString() ?? string.Empty) : state.GetRawText();
    }

    private static string GetStringProperty(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return string.Empty;
        }
        return property.ValueKind == JsonValueKind.String ? (property.GetString() ?? string.Empty) : property.GetRawText();
    }

    private static JsonElement[] GetArrayProperty(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<JsonElement>();
        }
        return property.EnumerateArray().ToArray();
    }

    private static async Task<JsonElement[]> InvokeJsonArrayAsync(
        HttpClient http,
        HttpMethod method,
        string uri,
        IReadOnlyDictionary<string, string> headers,
        JsonElement? body)
    {
        var element = await InvokeJsonElementAsync(http, method, uri, headers, body);
        if (element.ValueKind == JsonValueKind.Array)
        {
            return element.EnumerateArray().ToArray();
        }
        return Array.Empty<JsonElement>();
    }

    private static async Task<JsonElement> InvokeJsonElementAsync(
        HttpClient http,
        HttpMethod method,
        string uri,
        IReadOnlyDictionary<string, string> headers,
        JsonElement? body)
    {
        using var request = new HttpRequestMessage(method, uri);
        foreach (var header in headers)
        {
            if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value) && request.Content is not null)
            {
                request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        if (body.HasValue)
        {
            request.Content = new StringContent(body.Value.GetRawText(), Encoding.UTF8, "application/json");
        }

        using var response = await http.SendAsync(request);
        var payload = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"HTTP {method.Method} {uri} failed ({(int)response.StatusCode}): {payload}");
        }
        using var doc = JsonDocument.Parse(payload);
        return doc.RootElement.Clone();
    }

    private static async Task<string> RequestTokenAsync(DemoSeedOptions options)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
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

        string? lastError = null;
        foreach (var scope in scopeCandidates)
        {
            var form = new Dictionary<string, string>
            {
                ["grant_type"] = "password",
                ["client_id"] = options.TokenClientId,
                ["username"] = options.TokenUsername,
                ["password"] = options.TokenPassword,
            };
            if (!string.IsNullOrWhiteSpace(options.TokenClientSecret))
            {
                form["client_secret"] = options.TokenClientSecret;
            }
            if (!string.IsNullOrWhiteSpace(scope))
            {
                form["scope"] = scope;
            }

            using var response = await client.PostAsync(
                $"{options.KeycloakBaseUrl.TrimEnd('/')}/realms/{Uri.EscapeDataString(options.RealmName)}/protocol/openid-connect/token",
                new FormUrlEncodedContent(form));
            var payload = await response.Content.ReadAsStringAsync();
            if (response.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(payload);
                if (!doc.RootElement.TryGetProperty("access_token", out var token))
                {
                    throw new InvalidOperationException("Demo seed token response did not contain access_token.");
                }
                return token.GetString() ?? string.Empty;
            }

            lastError = $"Demo seed could not acquire bearer token for '{options.TokenUsername}'. Token request failed ({(int)response.StatusCode}): {payload}";
            var invalidScope = response.StatusCode == HttpStatusCode.BadRequest
                               && payload.Contains("invalid_scope", StringComparison.OrdinalIgnoreCase);
            var hasMore = !string.Equals(scope, scopeCandidates[^1], StringComparison.Ordinal);
            if (invalidScope && hasMore)
            {
                continue;
            }

            throw new InvalidOperationException(lastError);
        }

        throw new InvalidOperationException(lastError ?? "Demo seed token request failed.");
    }

    private sealed class DemoSeedOptions
    {
        public string BaseUrl { get; set; } = "http://127.0.0.1:18093";
        public string KeycloakBaseUrl { get; set; } = "http://127.0.0.1:18096";
        public string RealmName { get; set; } = "hidbridge-dev";
        public string TokenClientId { get; set; } = "controlplane-smoke";
        public string TokenClientSecret { get; set; } = string.Empty;
        public string TokenScope { get; set; } = "openid profile email";
        public string TokenUsername { get; set; } = "operator.smoke.admin";
        public string TokenPassword { get; set; } = "ChangeMe123!";
        public string AccessToken { get; set; } = string.Empty;
        public string PrincipalId { get; set; } = "demo-seed-admin";
        public string TenantId { get; set; } = "local-tenant";
        public string OrganizationId { get; set; } = "local-org";
        public bool SkipCloseActiveSessions { get; set; }
        public int ReadyEndpointTimeoutSec { get; set; } = 60;
        public int ReadyEndpointPollIntervalMs { get; set; } = 500;

        public static bool TryParse(IReadOnlyList<string> args, out DemoSeedOptions options, out string? error)
        {
            options = new DemoSeedOptions();
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
                    case "baseurl": options.BaseUrl = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "keycloakbaseurl": options.KeycloakBaseUrl = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "realmname": options.RealmName = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "tokenclientid": options.TokenClientId = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "tokenclientsecret": options.TokenClientSecret = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "tokenscope": options.TokenScope = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "tokenusername": options.TokenUsername = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "tokenpassword": options.TokenPassword = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "accesstoken": options.AccessToken = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "principalid": options.PrincipalId = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "tenantid": options.TenantId = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "organizationid": options.OrganizationId = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "skipcloseactivesessions": options.SkipCloseActiveSessions = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "readyendpointtimeoutsec": options.ReadyEndpointTimeoutSec = ParseInt(name, value, hasValue, ref i, ref error); break;
                    case "readyendpointpollintervalms": options.ReadyEndpointPollIntervalMs = ParseInt(name, value, hasValue, ref i, ref error); break;
                    default:
                        error = $"Unsupported demo-seed option '{token}'.";
                        return false;
                }

                if (error is not null)
                {
                    return false;
                }
            }

            return true;
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
