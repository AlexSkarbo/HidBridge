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
/// Implements the <c>RoomCleanupCommand</c> RuntimeCtl lane.
/// Execution contract:
/// 1) Parse CLI arguments into strongly typed options.
/// 2) Execute lane-specific orchestration (native RuntimeCtl commands first, script bridge only when explicitly needed).
/// 3) Persist step logs/summaries under <c>Platform/.logs</c> and return process-style exit code semantics (0 = success).
/// </summary>
internal static class RoomCleanupCommand
{
    internal sealed record Mode(
        string SummaryTitle,
        string FoundLabel,
        string EndpointPath,
        string DefaultPrincipalId,
        string DefaultReason,
        bool SupportsStaleAfterMinutes);

    public static async Task<int> RunAsync(IReadOnlyList<string> args, Mode mode)
    {
        if (!CleanupOptions.TryParse(args, mode, out var options, out var parseError))
        {
            Console.Error.WriteLine($"{mode.EndpointPath} options error: {parseError}");
            return 1;
        }

        try
        {
            var token = options.AccessToken;
            if (string.IsNullOrWhiteSpace(token))
            {
                token = await RequestTokenAsync(options);
            }

            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{options.BaseUrl.TrimEnd('/')}{mode.EndpointPath}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Add("X-HidBridge-UserId", options.PrincipalId);
            request.Headers.Add("X-HidBridge-PrincipalId", options.PrincipalId);
            request.Headers.Add("X-HidBridge-TenantId", options.TenantId);
            request.Headers.Add("X-HidBridge-OrganizationId", options.OrganizationId);
            request.Headers.Add("X-HidBridge-Role", "operator.admin,operator.moderator,operator.viewer");

            var payload = new Dictionary<string, object?>
            {
                ["dryRun"] = options.DryRun,
                ["reason"] = options.Reason,
            };
            if (mode.SupportsStaleAfterMinutes)
            {
                payload["staleAfterMinutes"] = options.StaleAfterMinutes;
            }

            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(request);
            var responseBody = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"HTTP POST {mode.EndpointPath} failed ({(int)response.StatusCode}): {responseBody}");
            }

            using var resultDoc = JsonDocument.Parse(responseBody);
            var root = resultDoc.RootElement;
            var scanned = GetInt(root, "scannedSessions");
            var matched = GetInt(root, "matchedSessions");
            var closed = GetInt(root, "closedSessions");
            var skipped = GetInt(root, "skippedSessions");
            var failedClosures = GetInt(root, "failedClosures");
            var matchedIds = GetStringArray(root, "matchedSessionIds");
            var closedIds = GetStringArray(root, "closedSessionIds");
            var errors = GetStringArray(root, "errors");

            Console.WriteLine();
            Console.WriteLine($"=== {mode.SummaryTitle} Summary ===");
            Console.WriteLine($"BaseUrl: {options.BaseUrl}");
            if (mode.SupportsStaleAfterMinutes)
            {
                Console.WriteLine($"Stale after minutes: {options.StaleAfterMinutes}");
            }
            Console.WriteLine($"Sessions visible: {scanned}");
            Console.WriteLine($"{mode.FoundLabel}: {matched}");
            Console.WriteLine($"Dry run: {options.DryRun}");
            Console.WriteLine($"Closed sessions: {closed}");
            Console.WriteLine($"Skipped sessions: {skipped}");
            Console.WriteLine($"Close failures: {failedClosures}");
            if (matchedIds.Count > 0)
            {
                Console.WriteLine($"{mode.FoundLabel.Replace(" found", " IDs")}: {string.Join(", ", matchedIds)}");
            }
            if (closedIds.Count > 0)
            {
                Console.WriteLine($"Closed session IDs: {string.Join(", ", closedIds)}");
            }
            if (errors.Count > 0)
            {
                Console.WriteLine("Close errors:");
                foreach (var error in errors)
                {
                    Console.WriteLine($"  - {error}");
                }
            }

            return errors.Count > 0 ? 1 : 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static int GetInt(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            return 0;
        }
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var numeric))
        {
            return numeric;
        }
        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out var parsed))
        {
            return parsed;
        }
        return 0;
    }

    private static List<string> GetStringArray(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            return new List<string>();
        }
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            return string.IsNullOrWhiteSpace(text) ? new List<string>() : new List<string> { text };
        }
        if (value.ValueKind != JsonValueKind.Array)
        {
            return new List<string>();
        }
        return value.EnumerateArray()
            .Where(static x => x.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(x.GetString()))
            .Select(static x => x.GetString()!)
            .ToList();
    }

    private static async Task<string> RequestTokenAsync(CleanupOptions options)
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
                    throw new InvalidOperationException("OIDC token response did not contain access_token.");
                }
                return token.GetString() ?? string.Empty;
            }

            lastError = $"Token request failed ({(int)response.StatusCode}): {payload}";
            var invalidScope = response.StatusCode == HttpStatusCode.BadRequest
                               && payload.Contains("invalid_scope", StringComparison.OrdinalIgnoreCase);
            var hasMore = !string.Equals(scope, scopeCandidates[^1], StringComparison.Ordinal);
            if (invalidScope && hasMore)
            {
                continue;
            }
            throw new InvalidOperationException(lastError);
        }

        throw new InvalidOperationException(lastError ?? "Token request failed.");
    }

    private sealed class CleanupOptions
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
        public string PrincipalId { get; set; } = string.Empty;
        public string TenantId { get; set; } = "local-tenant";
        public string OrganizationId { get; set; } = "local-org";
        public string Reason { get; set; } = string.Empty;
        public int StaleAfterMinutes { get; set; } = 30;
        public bool DryRun { get; set; }

        public static bool TryParse(IReadOnlyList<string> args, Mode mode, out CleanupOptions options, out string? error)
        {
            options = new CleanupOptions
            {
                PrincipalId = mode.DefaultPrincipalId,
                Reason = mode.DefaultReason,
            };
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
                    case "reason": options.Reason = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "dryrun": options.DryRun = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "staleafterminutes":
                        if (!mode.SupportsStaleAfterMinutes)
                        {
                            if (hasValue)
                            {
                                i++;
                            }
                            break;
                        }
                        options.StaleAfterMinutes = ParseInt(name, value, hasValue, ref i, ref error);
                        break;
                    default:
                        error = $"Unsupported cleanup option '{token}'.";
                        return false;
                }

                if (error is not null)
                {
                    return false;
                }
            }

            if (mode.SupportsStaleAfterMinutes && options.StaleAfterMinutes < 1)
            {
                error = "StaleAfterMinutes must be greater than 0.";
                return false;
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
