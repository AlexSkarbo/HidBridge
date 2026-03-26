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
/// Implements the <c>TokenDebugCommand</c> RuntimeCtl lane.
/// Execution contract:
/// 1) Parse CLI arguments into strongly typed options.
/// 2) Execute lane-specific orchestration (native RuntimeCtl commands first, script bridge only when explicitly needed).
/// 3) Persist step logs/summaries under <c>Platform/.logs</c> and return process-style exit code semantics (0 = success).
/// </summary>
internal static class TokenDebugCommand
{
    public static async Task<int> RunAsync(string platformRoot, IReadOnlyList<string> args)
    {
        if (!TokenDebugOptions.TryParse(args, out var options, out var parseError))
        {
            Console.Error.WriteLine($"token-debug options error: {parseError}");
            return 1;
        }

        var logRoot = Path.Combine(platformRoot, ".logs", "token-debug", DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(logRoot);
        var summaryPath = Path.Combine(logRoot, "token.summary.txt");
        var headerPath = Path.Combine(logRoot, "token.header.json");
        var payloadPath = Path.Combine(logRoot, "token.payload.json");
        var rawPath = Path.Combine(logRoot, "token.raw.txt");
        var rawResponsePath = Path.Combine(logRoot, "token.response.json");
        var idTokenHeaderPath = Path.Combine(logRoot, "id_token.header.json");
        var idTokenPayloadPath = Path.Combine(logRoot, "id_token.payload.json");
        var idTokenRawPath = Path.Combine(logRoot, "id_token.raw.txt");
        var userinfoPath = Path.Combine(logRoot, "userinfo.json");

        try
        {
            var source = "provided-token";
            var accessToken = options.AccessToken;
            JsonDocument? tokenResponse = null;

            if (string.IsNullOrWhiteSpace(accessToken)
                || string.Equals(accessToken, "auto", StringComparison.OrdinalIgnoreCase)
                || string.Equals(accessToken, "<token>", StringComparison.OrdinalIgnoreCase))
            {
                tokenResponse = await RequestTokenResponseAsync(options);
                if (!tokenResponse.RootElement.TryGetProperty("access_token", out var accessTokenElement))
                {
                    throw new InvalidOperationException("OIDC token response did not contain access_token.");
                }
                accessToken = accessTokenElement.GetString() ?? string.Empty;
                source = "oidc-password-grant";
                await File.WriteAllTextAsync(rawResponsePath, JsonSerializer.Serialize(tokenResponse.RootElement, new JsonSerializerOptions { WriteIndented = true }));
            }

            var parts = accessToken.Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                throw new InvalidOperationException("Access token is not a JWT.");
            }

            var headerJson = DecodeJwtSegment(parts[0]);
            var payloadJson = DecodeJwtSegment(parts[1]);
            await File.WriteAllTextAsync(rawPath, accessToken);
            await File.WriteAllTextAsync(headerPath, headerJson);
            await File.WriteAllTextAsync(payloadPath, payloadJson);

            using var headerDoc = JsonDocument.Parse(headerJson);
            using var payloadDoc = JsonDocument.Parse(payloadJson);

            string? idTokenSubject = null;
            if (string.Equals(source, "oidc-password-grant", StringComparison.OrdinalIgnoreCase)
                && tokenResponse is not null
                && tokenResponse.RootElement.TryGetProperty("id_token", out var idTokenElement)
                && !string.IsNullOrWhiteSpace(idTokenElement.GetString()))
            {
                var idToken = idTokenElement.GetString() ?? string.Empty;
                var idParts = idToken.Split('.', StringSplitOptions.RemoveEmptyEntries);
                if (idParts.Length >= 2)
                {
                    var idHeader = DecodeJwtSegment(idParts[0]);
                    var idPayload = DecodeJwtSegment(idParts[1]);
                    await File.WriteAllTextAsync(idTokenRawPath, idToken);
                    await File.WriteAllTextAsync(idTokenHeaderPath, idHeader);
                    await File.WriteAllTextAsync(idTokenPayloadPath, idPayload);

                    using var idPayloadDoc = JsonDocument.Parse(idPayload);
                    idTokenSubject = GetStringClaim(idPayloadDoc.RootElement, "sub");
                }
            }

            JsonDocument? userinfoDoc = null;
            if (string.Equals(source, "oidc-password-grant", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    userinfoDoc = await RequestUserInfoAsync(options, accessToken);
                    await File.WriteAllTextAsync(userinfoPath, JsonSerializer.Serialize(userinfoDoc.RootElement, new JsonSerializerOptions { WriteIndented = true }));
                }
                catch (Exception ex)
                {
                    await File.WriteAllTextAsync(userinfoPath, ex.Message);
                }
            }

            var roles = GetRoles(payloadDoc.RootElement);
            var preferredUsername = GetStringClaim(payloadDoc.RootElement, "preferred_username");
            var tenantId = GetStringClaim(payloadDoc.RootElement, "tenant_id");
            var organizationId = GetStringClaim(payloadDoc.RootElement, "org_id");
            var callerContextReady = !string.IsNullOrWhiteSpace(preferredUsername)
                                     && !string.IsNullOrWhiteSpace(tenantId)
                                     && !string.IsNullOrWhiteSpace(organizationId)
                                     && roles.Any(static x => x.StartsWith("operator.", StringComparison.Ordinal));

            var userinfoRoot = userinfoDoc?.RootElement;
            var issuedAt = GetLongClaim(payloadDoc.RootElement, "iat");
            var expiresAt = GetLongClaim(payloadDoc.RootElement, "exp");

            var summary = new List<string>
            {
                "=== Token Debug Summary ===",
                $"Realm: {options.Realm}",
                $"ClientId: {options.ClientId}",
                $"Username: {options.Username}",
                $"Source: {source}",
                $"Subject: {GetStringClaim(payloadDoc.RootElement, "sub")}",
                $"PreferredUsername: {preferredUsername}",
                $"Email: {GetStringClaim(payloadDoc.RootElement, "email")}",
                $"TenantId: {tenantId}",
                $"OrganizationId: {organizationId}",
                $"IdTokenSubject: {idTokenSubject}",
                $"UserInfoSubject: {GetStringClaim(userinfoRoot, "sub")}",
                $"UserInfoPreferredUsername: {GetStringClaim(userinfoRoot, "preferred_username")}",
                $"UserInfoEmail: {GetStringClaim(userinfoRoot, "email")}",
                $"UserInfoTenantId: {GetStringClaim(userinfoRoot, "tenant_id")}",
                $"UserInfoOrganizationId: {GetStringClaim(userinfoRoot, "org_id")}",
                $"CallerContextReady: {(callerContextReady ? "true" : "false")}",
                $"Audience: {string.Join(", ", GetAudience(payloadDoc.RootElement))}",
                $"IssuedAtUtc: {FormatUnix(issuedAt)}",
                $"ExpiresAtUtc: {FormatUnix(expiresAt)}",
                $"Roles: {string.Join(", ", roles)}",
                $"RawTokenResponse: {rawResponsePath}",
                $"HeaderJson: {headerPath}",
                $"PayloadJson: {payloadPath}",
                $"IdTokenHeaderJson: {idTokenHeaderPath}",
                $"IdTokenPayloadJson: {idTokenPayloadPath}",
                $"IdTokenRaw: {idTokenRawPath}",
                $"UserInfoJson: {userinfoPath}",
                $"RawToken: {rawPath}",
            };

            await File.WriteAllLinesAsync(summaryPath, summary);
            foreach (var line in summary)
            {
                Console.WriteLine(line);
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static string DecodeJwtSegment(string segment)
    {
        var padded = segment.Replace('-', '+').Replace('_', '/');
        var remainder = padded.Length % 4;
        if (remainder == 2)
        {
            padded += "==";
        }
        else if (remainder == 3)
        {
            padded += "=";
        }
        var bytes = Convert.FromBase64String(padded);
        using var doc = JsonDocument.Parse(bytes);
        return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string? GetStringClaim(JsonElement? element, string name)
    {
        if (element is null || element.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        if (!element.Value.TryGetProperty(name, out var claim))
        {
            return null;
        }
        return claim.ValueKind switch
        {
            JsonValueKind.String => claim.GetString(),
            JsonValueKind.Number => claim.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => claim.GetRawText(),
        };
    }

    private static long? GetLongClaim(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var claim))
        {
            return null;
        }
        if (claim.ValueKind == JsonValueKind.Number && claim.TryGetInt64(out var value))
        {
            return value;
        }
        if (claim.ValueKind == JsonValueKind.String && long.TryParse(claim.GetString(), out var parsed))
        {
            return parsed;
        }
        return null;
    }

    private static IReadOnlyList<string> GetRoles(JsonElement payload)
    {
        var roles = new HashSet<string>(StringComparer.Ordinal);
        if (payload.TryGetProperty("realm_access", out var realmAccess)
            && realmAccess.ValueKind == JsonValueKind.Object
            && realmAccess.TryGetProperty("roles", out var roleArray)
            && roleArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in roleArray.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                {
                    roles.Add(item.GetString()!);
                }
            }
        }

        if (payload.TryGetProperty("role", out var topLevelRoles))
        {
            if (topLevelRoles.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(topLevelRoles.GetString()))
            {
                roles.Add(topLevelRoles.GetString()!);
            }
            else if (topLevelRoles.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in topLevelRoles.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                    {
                        roles.Add(item.GetString()!);
                    }
                }
            }
        }

        return roles.ToArray();
    }

    private static IReadOnlyList<string> GetAudience(JsonElement payload)
    {
        if (!payload.TryGetProperty("aud", out var aud))
        {
            return Array.Empty<string>();
        }
        if (aud.ValueKind == JsonValueKind.String)
        {
            var value = aud.GetString();
            return string.IsNullOrWhiteSpace(value) ? Array.Empty<string>() : new[] { value };
        }
        if (aud.ValueKind == JsonValueKind.Array)
        {
            return aud.EnumerateArray()
                .Where(static x => x.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(x.GetString()))
                .Select(static x => x.GetString()!)
                .ToArray();
        }
        return Array.Empty<string>();
    }

    private static string FormatUnix(long? unixSeconds)
    {
        if (!unixSeconds.HasValue)
        {
            return string.Empty;
        }
        return DateTimeOffset.FromUnixTimeSeconds(unixSeconds.Value).UtcDateTime.ToString("O");
    }

    private static async Task<JsonDocument> RequestTokenResponseAsync(TokenDebugOptions options)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var scopeCandidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(options.Scope))
        {
            scopeCandidates.Add(options.Scope);
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
                ["client_id"] = options.ClientId,
                ["username"] = options.Username,
                ["password"] = options.Password,
            };
            if (!string.IsNullOrWhiteSpace(options.ClientSecret))
            {
                form["client_secret"] = options.ClientSecret;
            }
            if (!string.IsNullOrWhiteSpace(scope))
            {
                form["scope"] = scope;
            }

            using var response = await client.PostAsync(
                $"{options.KeycloakBaseUrl.TrimEnd('/')}/realms/{Uri.EscapeDataString(options.Realm)}/protocol/openid-connect/token",
                new FormUrlEncodedContent(form));
            var payload = await response.Content.ReadAsStringAsync();
            if (response.IsSuccessStatusCode)
            {
                return JsonDocument.Parse(payload);
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

    private static async Task<JsonDocument> RequestUserInfoAsync(TokenDebugOptions options, string accessToken)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{options.KeycloakBaseUrl.TrimEnd('/')}/realms/{Uri.EscapeDataString(options.Realm)}/protocol/openid-connect/userinfo");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await client.SendAsync(request);
        var payload = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"UserInfo request failed ({(int)response.StatusCode}): {payload}");
        }
        return JsonDocument.Parse(payload);
    }

    private sealed class TokenDebugOptions
    {
        public string KeycloakBaseUrl { get; set; } = "http://127.0.0.1:18096";
        public string Realm { get; set; } = "hidbridge-dev";
        public string ClientId { get; set; } = "controlplane-smoke";
        public string ClientSecret { get; set; } = string.Empty;
        public string Username { get; set; } = "operator.smoke.admin";
        public string Password { get; set; } = "ChangeMe123!";
        public string Scope { get; set; } = "openid profile email";
        public string AccessToken { get; set; } = "auto";

        public static bool TryParse(IReadOnlyList<string> args, out TokenDebugOptions options, out string? error)
        {
            options = new TokenDebugOptions();
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
                    case "keycloakbaseurl": options.KeycloakBaseUrl = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "realm": options.Realm = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "clientid": options.ClientId = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "clientsecret": options.ClientSecret = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "username": options.Username = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "password": options.Password = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "scope": options.Scope = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "accesstoken": options.AccessToken = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    default:
                        error = $"Unsupported token-debug option '{token}'.";
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
    }
}
