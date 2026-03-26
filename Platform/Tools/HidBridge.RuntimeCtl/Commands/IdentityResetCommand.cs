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
/// Implements the <c>IdentityResetCommand</c> RuntimeCtl lane.
/// Execution contract:
/// 1) Parse CLI arguments into strongly typed options.
/// 2) Execute lane-specific orchestration (native RuntimeCtl commands first, script bridge only when explicitly needed).
/// 3) Persist step logs/summaries under <c>Platform/.logs</c> and return process-style exit code semantics (0 = success).
/// </summary>
internal static class IdentityResetCommand
{
    public static async Task<int> RunAsync(string platformRoot, IReadOnlyList<string> args)
    {
        if (!IdentityResetOptions.TryParse(args, out var options, out var parseError))
        {
            Console.Error.WriteLine($"identity-reset options error: {parseError}");
            return 1;
        }

        if (string.IsNullOrWhiteSpace(options.AdminUser))
        {
            options.AdminUser = Environment.GetEnvironmentVariable("HIDBRIDGE_KEYCLOAK_ADMIN_USER")
                                ?? Environment.GetEnvironmentVariable("KEYCLOAK_ADMIN")
                                ?? "admin";
        }

        if (string.IsNullOrWhiteSpace(options.AdminPassword))
        {
            options.AdminPassword = Environment.GetEnvironmentVariable("HIDBRIDGE_KEYCLOAK_ADMIN_PASSWORD")
                                    ?? Environment.GetEnvironmentVariable("KEYCLOAK_ADMIN_PASSWORD")
                                    ?? "1q-p2w0o";
        }

        if (string.IsNullOrWhiteSpace(options.ImportPath))
        {
            options.ImportPath = Path.Combine(platformRoot, "Identity", "Keycloak", "realm-import", "hidbridge-dev-realm.json");
        }
        else if (!Path.IsPathRooted(options.ImportPath))
        {
            options.ImportPath = Path.GetFullPath(Path.Combine(platformRoot, options.ImportPath));
        }

        if (string.IsNullOrWhiteSpace(options.BackupRoot))
        {
            options.BackupRoot = Path.Combine(platformRoot, "Identity", "Keycloak", "backups");
        }
        else if (!Path.IsPathRooted(options.BackupRoot))
        {
            options.BackupRoot = Path.GetFullPath(Path.Combine(platformRoot, options.BackupRoot));
        }

        if (!File.Exists(options.ImportPath))
        {
            Console.Error.WriteLine($"Realm import file not found: {options.ImportPath}");
            return 1;
        }

        Directory.CreateDirectory(options.BackupRoot);
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        var backupPath = Path.Combine(options.BackupRoot, $"{options.RealmName}-reset-backup-{stamp}.json");
        var activityLogPath = Path.Combine(options.BackupRoot, $"{options.RealmName}-reset-{stamp}.log");

        var importJson = await File.ReadAllTextAsync(options.ImportPath);
        using var importDoc = JsonDocument.Parse(importJson);

        var desiredIdentityProvidersCount = importDoc.RootElement.TryGetProperty("identityProviders", out var idps)
                                           && idps.ValueKind == JsonValueKind.Array
            ? idps.GetArrayLength()
            : 0;
        var externalProviderPaths = ResolveExternalProviderConfigPaths(options, platformRoot);
        var hasProviderRestoreInputs = desiredIdentityProvidersCount > 0 || externalProviderPaths.Count > 0;
        if (!hasProviderRestoreInputs && !options.AllowIdentityProviderLoss)
        {
            Console.Error.WriteLine("Identity reset blocked: import has no identity providers and no external provider configs were supplied. Use -AllowIdentityProviderLoss to override.");
            return 1;
        }

        var activity = new List<string>();
        var steps = new List<(string Name, string Status, double Seconds, int ExitCode, string LogPath)>();
        string? adminToken = null;

        async Task RunStepAsync(string name, Func<Task> action)
        {
            var stepLogPath = Path.Combine(options.BackupRoot, $"{name.Replace(' ', '-').ToLowerInvariant()}-{stamp}.log");
            var sw = Stopwatch.StartNew();
            try
            {
                await action();
                sw.Stop();
                await File.WriteAllLinesAsync(stepLogPath, activity);
                steps.Add((name, "PASS", sw.Elapsed.TotalSeconds, 0, stepLogPath));
                Console.WriteLine();
                Console.WriteLine($"=== {name} ===");
                Console.WriteLine($"PASS  {name}");
                Console.WriteLine($"Log:   {stepLogPath}");
            }
            catch (Exception ex)
            {
                sw.Stop();
                activity.Add(ex.ToString());
                await File.WriteAllLinesAsync(stepLogPath, activity);
                steps.Add((name, "FAIL", sw.Elapsed.TotalSeconds, 1, stepLogPath));
                Console.WriteLine();
                Console.WriteLine($"=== {name} ===");
                Console.WriteLine($"FAIL  {name}");
                Console.WriteLine($"Log:   {stepLogPath}");
                throw;
            }
        }

        try
        {
            await RunStepAsync("Admin Auth", async () =>
            {
                adminToken = await RequestTokenAsync(
                    options.KeycloakBaseUrl,
                    options.AdminRealm,
                    "admin-cli",
                    options.AdminUser,
                    options.AdminPassword,
                    string.Empty,
                    string.Empty);
                activity.Add("Authenticated against Keycloak admin API.");
            });

            await RunStepAsync("Realm Backup", async () =>
            {
                var existing = await SendAdminAsync(HttpMethod.Get, options.KeycloakBaseUrl, adminToken!, $"/admin/realms/{Uri.EscapeDataString(options.RealmName)}");
                if (existing.StatusCode == HttpStatusCode.OK)
                {
                    await File.WriteAllTextAsync(backupPath, existing.Content);
                    activity.Add($"Backup written: {backupPath}");
                }
                else if (existing.StatusCode == HttpStatusCode.NotFound)
                {
                    activity.Add($"Realm '{options.RealmName}' not found during backup step; continuing.");
                }
                else
                {
                    throw new InvalidOperationException($"Realm backup failed ({(int)existing.StatusCode}): {existing.Content}");
                }
            });

            await RunStepAsync("Realm Delete", async () =>
            {
                var deleted = await SendAdminAsync(HttpMethod.Delete, options.KeycloakBaseUrl, adminToken!, $"/admin/realms/{Uri.EscapeDataString(options.RealmName)}");
                if (deleted.StatusCode == HttpStatusCode.NoContent || deleted.StatusCode == HttpStatusCode.NotFound)
                {
                    activity.Add(deleted.StatusCode == HttpStatusCode.NotFound
                        ? $"Realm '{options.RealmName}' does not exist; nothing to delete."
                        : $"Deleted realm: {options.RealmName}");
                    return;
                }

                throw new InvalidOperationException($"Realm delete failed ({(int)deleted.StatusCode}): {deleted.Content}");
            });

            await RunStepAsync("Realm Recreate", async () =>
            {
                var created = await SendAdminAsync(HttpMethod.Post, options.KeycloakBaseUrl, adminToken!, "/admin/realms", importJson);
                if (created.StatusCode is not HttpStatusCode.Created and not HttpStatusCode.NoContent)
                {
                    throw new InvalidOperationException($"Realm recreate failed ({(int)created.StatusCode}): {created.Content}");
                }

                var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
                while (DateTimeOffset.UtcNow < deadline)
                {
                    var realm = await SendAdminAsync(HttpMethod.Get, options.KeycloakBaseUrl, adminToken!, $"/admin/realms/{Uri.EscapeDataString(options.RealmName)}");
                    if (realm.StatusCode == HttpStatusCode.OK)
                    {
                        activity.Add($"Recreated realm from import: {options.ImportPath}");
                        return;
                    }

                    await Task.Delay(500);
                }

                throw new InvalidOperationException($"Realm '{options.RealmName}' did not become available after recreate.");
            });

            if (externalProviderPaths.Count > 0)
            {
                await RunStepAsync("External Providers", async () =>
                {
                    foreach (var providerPath in externalProviderPaths)
                    {
                        var providerJson = await File.ReadAllTextAsync(providerPath);
                        using var providerDoc = JsonDocument.Parse(providerJson);
                        if (!providerDoc.RootElement.TryGetProperty("alias", out var aliasEl)
                            || string.IsNullOrWhiteSpace(aliasEl.GetString()))
                        {
                            throw new InvalidOperationException($"Provider config '{providerPath}' does not define alias.");
                        }

                        if (!providerDoc.RootElement.TryGetProperty("providerId", out var providerIdEl)
                            || string.IsNullOrWhiteSpace(providerIdEl.GetString()))
                        {
                            throw new InvalidOperationException($"Provider config '{providerPath}' does not define providerId.");
                        }

                        var alias = aliasEl.GetString()!;
                        var escapedAlias = Uri.EscapeDataString(alias);
                        var existing = await SendAdminAsync(
                            HttpMethod.Get,
                            options.KeycloakBaseUrl,
                            adminToken!,
                            $"/admin/realms/{Uri.EscapeDataString(options.RealmName)}/identity-provider/instances/{escapedAlias}");

                        if (existing.StatusCode == HttpStatusCode.NotFound)
                        {
                            var created = await SendAdminAsync(
                                HttpMethod.Post,
                                options.KeycloakBaseUrl,
                                adminToken!,
                                $"/admin/realms/{Uri.EscapeDataString(options.RealmName)}/identity-provider/instances",
                                providerJson);
                            if (created.StatusCode is not HttpStatusCode.Created and not HttpStatusCode.NoContent)
                            {
                                throw new InvalidOperationException($"Create identity provider '{alias}' failed ({(int)created.StatusCode}): {created.Content}");
                            }

                            activity.Add($"Created identity provider: {alias} (from {providerPath})");
                        }
                        else if (existing.StatusCode == HttpStatusCode.OK)
                        {
                            var updated = await SendAdminAsync(
                                HttpMethod.Put,
                                options.KeycloakBaseUrl,
                                adminToken!,
                                $"/admin/realms/{Uri.EscapeDataString(options.RealmName)}/identity-provider/instances/{escapedAlias}",
                                providerJson);
                            if (updated.StatusCode is not HttpStatusCode.NoContent)
                            {
                                throw new InvalidOperationException($"Update identity provider '{alias}' failed ({(int)updated.StatusCode}): {updated.Content}");
                            }

                            activity.Add($"Updated identity provider: {alias} (from {providerPath})");
                        }
                        else
                        {
                            throw new InvalidOperationException($"Identity provider lookup failed ({(int)existing.StatusCode}): {existing.Content}");
                        }
                    }
                });
            }

            if (!options.SkipVerification)
            {
                await RunStepAsync("Verify Smoke Admin Token", async () =>
                {
                    var token = await RequestTokenAsync(options.KeycloakBaseUrl, options.RealmName, "controlplane-smoke", "operator.smoke.admin", "ChangeMe123!", string.Empty, "openid profile email");
                    if (string.IsNullOrWhiteSpace(token))
                    {
                        throw new InvalidOperationException("Smoke admin token response did not contain access_token.");
                    }

                    activity.Add("Verified smoke admin token issuance.");
                });

                await RunStepAsync("Verify Smoke Viewer Token", async () =>
                {
                    var token = await RequestTokenAsync(options.KeycloakBaseUrl, options.RealmName, "controlplane-smoke", "operator.smoke.viewer", "ChangeMe123!", string.Empty, "openid profile email");
                    if (string.IsNullOrWhiteSpace(token))
                    {
                        throw new InvalidOperationException("Smoke viewer token response did not contain access_token.");
                    }

                    activity.Add("Verified smoke viewer token issuance.");
                });
            }

            await File.WriteAllLinesAsync(activityLogPath, activity);

            Console.WriteLine();
            Console.WriteLine("=== Keycloak Realm Reset Summary ===");
            Console.WriteLine($"Realm: {options.RealmName}");
            Console.WriteLine($"Import: {options.ImportPath}");
            Console.WriteLine($"Backup: {backupPath}");
            Console.WriteLine($"Activity log: {activityLogPath}");
            Console.WriteLine();
            Console.WriteLine("=== Reset Steps ===");
            Console.WriteLine();
            Console.WriteLine($"{"Name",-30} {"Status",-6} {"Seconds",7} {"ExitCode",8}");
            Console.WriteLine($"{new string('-', 30)} {new string('-', 6)} {new string('-', 7)} {new string('-', 8)}");
            foreach (var step in steps)
            {
                Console.WriteLine($"{step.Name,-30} {step.Status,-6} {step.Seconds,7:F2} {step.ExitCode,8}");
            }

            return steps.Any(static x => string.Equals(x.Status, "FAIL", StringComparison.OrdinalIgnoreCase)) ? 1 : 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            activity.Add(ex.ToString());
            await File.WriteAllLinesAsync(activityLogPath, activity);
            return 1;
        }
    }

    private static async Task<(HttpStatusCode StatusCode, string Content)> SendAdminAsync(HttpMethod method, string baseUrl, string accessToken, string path, string? jsonBody = null)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        using var request = new HttpRequestMessage(method, $"{baseUrl.TrimEnd('/')}{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        if (jsonBody is not null)
        {
            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        }

        using var response = await client.SendAsync(request);
        var content = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, content);
    }

    private static async Task<string> RequestTokenAsync(
        string keycloakBaseUrl,
        string realm,
        string clientId,
        string username,
        string password,
        string clientSecret,
        string scope)
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
        foreach (var scopeCandidate in scopeCandidates)
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
            if (!string.IsNullOrWhiteSpace(scopeCandidate))
            {
                form["scope"] = scopeCandidate;
            }

            using var response = await client.PostAsync(
                $"{keycloakBaseUrl.TrimEnd('/')}/realms/{Uri.EscapeDataString(realm)}/protocol/openid-connect/token",
                new FormUrlEncodedContent(form));
            var content = await response.Content.ReadAsStringAsync();
            if (response.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(content);
                if (!doc.RootElement.TryGetProperty("access_token", out var accessToken))
                {
                    throw new InvalidOperationException("Token response did not contain access_token.");
                }

                return accessToken.GetString() ?? string.Empty;
            }

            lastError = $"Token request failed ({(int)response.StatusCode}): {content}";
            var invalidScope = response.StatusCode == HttpStatusCode.BadRequest
                               && content.Contains("invalid_scope", StringComparison.OrdinalIgnoreCase);
            var hasMore = !string.Equals(scopeCandidate, scopeCandidates[^1], StringComparison.Ordinal);
            if (invalidScope && hasMore)
            {
                continue;
            }

            throw new InvalidOperationException(lastError);
        }

        throw new InvalidOperationException(lastError ?? "Token request failed.");
    }

    private static List<string> ResolveExternalProviderConfigPaths(IdentityResetOptions options, string platformRoot)
    {
        var allCandidates = new List<string>();

        foreach (var candidate in options.ExternalProviderConfigPaths)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            allCandidates.AddRange(candidate.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        var envPaths = Environment.GetEnvironmentVariable("HIDBRIDGE_KEYCLOAK_EXTERNAL_PROVIDER_CONFIGS");
        if (!string.IsNullOrWhiteSpace(envPaths))
        {
            allCandidates.AddRange(envPaths.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        var defaultGoogleLocal = Path.Combine(platformRoot, "Identity", "Keycloak", "providers", "google-oidc.local.json");
        if (File.Exists(defaultGoogleLocal))
        {
            allCandidates.Add(defaultGoogleLocal);
        }

        var resolved = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in allCandidates)
        {
            var path = Path.IsPathRooted(candidate)
                ? candidate
                : Path.GetFullPath(Path.Combine(platformRoot, candidate));
            if (!File.Exists(path))
            {
                throw new InvalidOperationException($"External provider config file not found: {path}");
            }

            if (seen.Add(path))
            {
                resolved.Add(path);
            }
        }

        return resolved;
    }

    private sealed class IdentityResetOptions
    {
        public string KeycloakBaseUrl { get; set; } = "http://127.0.0.1:18096";
        public string AdminRealm { get; set; } = "master";
        public string AdminUser { get; set; } = string.Empty;
        public string AdminPassword { get; set; } = string.Empty;
        public string RealmName { get; set; } = "hidbridge-dev";
        public string ImportPath { get; set; } = string.Empty;
        public string BackupRoot { get; set; } = string.Empty;
        public bool SkipVerification { get; set; }
        public List<string> ExternalProviderConfigPaths { get; } = new();
        public bool AllowIdentityProviderLoss { get; set; }

        public static bool TryParse(IReadOnlyList<string> args, out IdentityResetOptions options, out string? error)
        {
            options = new IdentityResetOptions();
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
                string? value = hasValue ? args[i + 1] : null;

                switch (name.ToLowerInvariant())
                {
                    case "keycloakbaseurl": options.KeycloakBaseUrl = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "adminrealm": options.AdminRealm = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "adminuser": options.AdminUser = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "adminpassword": options.AdminPassword = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "realmname": options.RealmName = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "importpath": options.ImportPath = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "backuproot": options.BackupRoot = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "skipverification": options.SkipVerification = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "allowidentityproviderloss": options.AllowIdentityProviderLoss = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "externalproviderconfigpaths":
                        options.ExternalProviderConfigPaths.Add(RequireValue(name, value, ref i, ref hasValue, ref error));
                        break;
                    default:
                        error = $"Unsupported identity-reset option '{token}'.";
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
