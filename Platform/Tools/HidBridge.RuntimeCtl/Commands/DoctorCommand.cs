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
/// Implements the <c>DoctorCommand</c> RuntimeCtl lane.
/// Execution contract:
/// 1) Parse CLI arguments into strongly typed options.
/// 2) Execute lane-specific orchestration (native RuntimeCtl commands first, script bridge only when explicitly needed).
/// 3) Persist step logs/summaries under <c>Platform/.logs</c> and return process-style exit code semantics (0 = success).
/// </summary>
internal static class DoctorCommand
{
    public static async Task<int> RunAsync(string platformRoot, IReadOnlyList<string> args)
    {
        if (!DoctorOptions.TryParse(args, out var options, out var parseError))
        {
            Console.Error.WriteLine($"doctor options error: {parseError}");
            return 1;
        }

        var repoRoot = Directory.GetParent(platformRoot)?.FullName ?? platformRoot;
        var logRoot = Path.Combine(platformRoot, ".logs", "doctor", DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(logRoot);
        var logPath = Path.Combine(logRoot, "doctor.log");
        var apiProbeStdout = Path.Combine(logRoot, "api-probe.stdout.log");
        var apiProbeStderr = Path.Combine(logRoot, "api-probe.stderr.log");

        var checks = new List<(string Name, string Status, string Message)>();
        var activity = new StringBuilder();
        Process? apiProbe = null;

        void AddCheck(string name, string status, string message)
        {
            checks.Add((name, status, message));
            activity.AppendLine($"[{status}] {name}: {message}");
        }

        try
        {
            AddCheck("dotnet", ResolveCommandPath("dotnet") is not null ? "PASS" : "FAIL", "dotnet availability check");

            var keycloakPortOk = await TestTcpPortAsync("127.0.0.1", 18096, TimeSpan.FromSeconds(2));
            AddCheck("keycloak-port", keycloakPortOk ? "PASS" : "FAIL", $"{options.KeycloakBaseUrl} reachable");

            var postgresPortOk = await TestTcpPortAsync(options.PostgresHost, options.PostgresPort, TimeSpan.FromSeconds(2));
            AddCheck("postgres-port", postgresPortOk ? "PASS" : "FAIL", $"{options.PostgresHost}:{options.PostgresPort} reachable");

            var apiPortOk = await TestTcpPortAsync("127.0.0.1", 18093, TimeSpan.FromSeconds(2));
            if (!apiPortOk && options.StartApiProbe)
            {
                var apiProjectPath = Path.Combine(repoRoot, "Platform", "Platform", "HidBridge.ControlPlane.Api", "HidBridge.ControlPlane.Api.csproj");
                if (!File.Exists(apiProjectPath))
                {
                    AddCheck("api-probe", "FAIL", $"API project not found: {apiProjectPath}");
                }
                else
                {
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
                    startInfo.ArgumentList.Add(apiProjectPath);
                    startInfo.ArgumentList.Add("-c");
                    startInfo.ArgumentList.Add(options.ApiConfiguration);
                    startInfo.ArgumentList.Add("--no-build");
                    startInfo.ArgumentList.Add("--no-launch-profile");
                    startInfo.Environment["HIDBRIDGE_PERSISTENCE_PROVIDER"] = options.ApiPersistenceProvider;
                    startInfo.Environment["HIDBRIDGE_SQL_CONNECTION"] = options.ApiConnectionString;
                    startInfo.Environment["HIDBRIDGE_SQL_SCHEMA"] = options.ApiSchema;
                    startInfo.Environment["HIDBRIDGE_SQL_APPLY_MIGRATIONS"] = "true";
                    startInfo.Environment["HIDBRIDGE_AUTH_ENABLED"] = "false";
                    apiProbe = WebRtcStackCommand.StartRedirectedProcess(startInfo, apiProbeStdout, apiProbeStderr);

                    for (var attempt = 0; attempt < 20; attempt++)
                    {
                        await Task.Delay(500);
                        if (await TestApiHealthAsync(options.ApiBaseUrl))
                        {
                            apiPortOk = true;
                            break;
                        }

                        if (apiProbe.HasExited)
                        {
                            break;
                        }
                    }
                }
            }

            var apiStatus = apiPortOk ? "PASS" : options.RequireApi ? "FAIL" : "WARN";
            var apiMessage = apiPortOk
                ? $"{options.ApiBaseUrl} reachable"
                : options.StartApiProbe
                    ? $"{options.ApiBaseUrl} not reachable after probe start attempt"
                    : $"{options.ApiBaseUrl} not reachable; API is not running";
            AddCheck("api-port", apiStatus, apiMessage);

            AddCheck(
                "realm-sync-script",
                File.Exists(Path.Combine(platformRoot, "Identity", "Keycloak", "Sync-HidBridgeDevRealm.ps1")) ? "PASS" : "FAIL",
                "realm sync script present");
            AddCheck(
                "realm-import",
                File.Exists(Path.Combine(platformRoot, "Identity", "Keycloak", "realm-import", "hidbridge-dev-realm.json")) ? "PASS" : "FAIL",
                "realm import present");
            AddCheck(
                "smoke-script",
                File.Exists(Path.Combine(platformRoot, "Scripts", "run_api_bearer_smoke.ps1")) ? "PASS" : "FAIL",
                "bearer smoke script present");

            if (keycloakPortOk)
            {
                var adminToken = await RequestTokenAsync(
                    options.KeycloakBaseUrl,
                    options.AdminRealm,
                    "admin-cli",
                    options.AdminUser,
                    options.AdminPassword,
                    string.Empty,
                    string.Empty);
                AddCheck("keycloak-admin-auth", string.IsNullOrWhiteSpace(adminToken) ? "FAIL" : "PASS", "admin-cli token acquisition");

                if (!string.IsNullOrWhiteSpace(adminToken))
                {
                    var realmStatus = await SendKeycloakAdminAsync(HttpMethod.Get, options.KeycloakBaseUrl, adminToken, $"/admin/realms/{Uri.EscapeDataString(options.Realm)}");
                    AddCheck("realm-exists", realmStatus.StatusCode == HttpStatusCode.OK ? "PASS" : "FAIL", $"realm {options.Realm} lookup");

                    await AddSmokeUserCheckAsync(
                        checks,
                        "smoke-client",
                        options.KeycloakBaseUrl,
                        options.Realm,
                        options.SmokeClientId,
                        options.SmokeAdminUser,
                        options.SmokeAdminPassword,
                        includeClaimsCheck: true);

                    await AddSmokeUserCheckAsync(
                        checks,
                        $"user-{options.SmokeViewerUser}",
                        options.KeycloakBaseUrl,
                        options.Realm,
                        options.SmokeClientId,
                        options.SmokeViewerUser,
                        options.SmokeViewerPassword,
                        includeClaimsCheck: false);

                    await AddSmokeUserCheckAsync(
                        checks,
                        $"user-{options.SmokeForeignUser}",
                        options.KeycloakBaseUrl,
                        options.Realm,
                        options.SmokeClientId,
                        options.SmokeForeignUser,
                        options.SmokeForeignPassword,
                        includeClaimsCheck: false);
                }
            }
        }
        catch (Exception ex)
        {
            activity.AppendLine(ex.ToString());
            AddCheck("doctor.exception", "FAIL", ex.Message);
        }
        finally
        {
            if (apiProbe is not null && !apiProbe.HasExited)
            {
                try
                {
                    apiProbe.Kill(entireProcessTree: true);
                }
                catch
                {
                    // best effort
                }
            }
        }

        foreach (var check in checks)
        {
            activity.AppendLine($"[{check.Status}] {check.Name}: {check.Message}");
        }

        await File.WriteAllTextAsync(logPath, activity.ToString());

        Console.WriteLine();
        Console.WriteLine("=== Doctor Summary ===");
        Console.WriteLine();
        Console.WriteLine($"{"Name",-28} {"Status",-6} Message");
        Console.WriteLine($"{new string('-', 28)} {new string('-', 6)} {new string('-', 60)}");
        foreach (var check in checks)
        {
            Console.WriteLine($"{check.Name,-28} {check.Status,-6} {check.Message}");
        }

        Console.WriteLine();
        Console.WriteLine($"Logs root: {logRoot}");
        Console.WriteLine($"Doctor: {logPath}");

        return checks.Any(static x => string.Equals(x.Status, "FAIL", StringComparison.OrdinalIgnoreCase)) ? 1 : 0;
    }

    private static async Task AddSmokeUserCheckAsync(
        List<(string Name, string Status, string Message)> checks,
        string checkName,
        string keycloakBaseUrl,
        string realm,
        string clientId,
        string username,
        string password,
        bool includeClaimsCheck)
    {
        try
        {
            var token = await RequestTokenAsync(keycloakBaseUrl, realm, clientId, username, password, string.Empty, "openid profile email");
            var pass = !string.IsNullOrWhiteSpace(token);
            checks.Add((checkName, pass ? "PASS" : "FAIL", $"direct-grant token acquisition for {username}"));
            if (includeClaimsCheck)
            {
                checks.Add(("smoke-claims", TestCallerContextReady(token) ? "PASS" : "WARN", "caller-context claims present in smoke access token"));
            }
        }
        catch (Exception ex)
        {
            checks.Add((checkName, "FAIL", ex.Message));
            if (includeClaimsCheck)
            {
                checks.Add(("smoke-claims", "FAIL", "caller-context claims could not be inspected"));
            }
        }
    }

    private static async Task<bool> TestApiHealthAsync(string baseUrl)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
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

    private static async Task<bool> TestTcpPortAsync(string host, int port, TimeSpan timeout)
    {
        using var tcp = new TcpClient();
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await tcp.ConnectAsync(host, port, cts.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TestCallerContextReady(string? accessToken)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return false;
        }

        var parts = accessToken.Split('.');
        if (parts.Length < 2)
        {
            return false;
        }

        try
        {
            var payloadBytes = DecodeBase64Url(parts[1]);
            using var payloadDoc = JsonDocument.Parse(payloadBytes);
            var payload = payloadDoc.RootElement;
            var hasPreferredUsername = payload.TryGetProperty("preferred_username", out var preferredUsername) && !string.IsNullOrWhiteSpace(preferredUsername.GetString());
            var hasTenant = payload.TryGetProperty("tenant_id", out var tenant) && !string.IsNullOrWhiteSpace(tenant.GetString());
            var hasOrg = payload.TryGetProperty("org_id", out var org) && !string.IsNullOrWhiteSpace(org.GetString());
            var hasRole = false;
            if (payload.TryGetProperty("role", out var role))
            {
                if (role.ValueKind == JsonValueKind.Array)
                {
                    hasRole = role.EnumerateArray().Any(static x => x.ValueKind == JsonValueKind.String && x.GetString()!.StartsWith("operator.", StringComparison.OrdinalIgnoreCase));
                }
                else if (role.ValueKind == JsonValueKind.String)
                {
                    var raw = role.GetString() ?? string.Empty;
                    hasRole = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Any(static x => x.StartsWith("operator.", StringComparison.OrdinalIgnoreCase));
                }
            }

            return hasPreferredUsername && hasTenant && hasOrg && hasRole;
        }
        catch
        {
            return false;
        }
    }

    private static byte[] DecodeBase64Url(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        while (normalized.Length % 4 != 0)
        {
            normalized += "=";
        }

        return Convert.FromBase64String(normalized);
    }

    private static string? ResolveCommandPath(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var entries = path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var entry in entries)
        {
            var candidate = Path.Combine(entry, OperatingSystem.IsWindows() ? $"{name}.exe" : name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static async Task<(HttpStatusCode StatusCode, string Content)> SendKeycloakAdminAsync(HttpMethod method, string baseUrl, string accessToken, string path, string? jsonBody = null)
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

    private sealed class DoctorOptions
    {
        public string KeycloakBaseUrl { get; set; } = "http://127.0.0.1:18096";
        public string Realm { get; set; } = "hidbridge-dev";
        public string AdminRealm { get; set; } = "master";
        public string AdminUser { get; set; } = "admin";
        public string AdminPassword { get; set; } = "1q-p2w0o";
        public string PostgresHost { get; set; } = "127.0.0.1";
        public int PostgresPort { get; set; } = 5434;
        public string ApiBaseUrl { get; set; } = "http://127.0.0.1:18093";
        public bool RequireApi { get; set; }
        public bool StartApiProbe { get; set; }
        public string ApiConfiguration { get; set; } = "Debug";
        public string ApiPersistenceProvider { get; set; } = "Sql";
        public string ApiConnectionString { get; set; } = "Host=127.0.0.1;Port=5434;Database=hidbridge;Username=hidbridge;Password=hidbridge";
        public string ApiSchema { get; set; } = "hidbridge";
        public string SmokeClientId { get; set; } = "controlplane-smoke";
        public string SmokeAdminUser { get; set; } = "operator.smoke.admin";
        public string SmokeAdminPassword { get; set; } = "ChangeMe123!";
        public string SmokeViewerUser { get; set; } = "operator.smoke.viewer";
        public string SmokeViewerPassword { get; set; } = "ChangeMe123!";
        public string SmokeForeignUser { get; set; } = "operator.smoke.foreign";
        public string SmokeForeignPassword { get; set; } = "ChangeMe123!";

        public static bool TryParse(IReadOnlyList<string> args, out DoctorOptions options, out string? error)
        {
            options = new DoctorOptions();
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
                    case "realm": options.Realm = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "adminrealm": options.AdminRealm = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "adminuser": options.AdminUser = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "adminpassword": options.AdminPassword = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "postgreshost": options.PostgresHost = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "postgresport": options.PostgresPort = ParseInt(name, value, hasValue, ref i, ref error); break;
                    case "apibaseurl": options.ApiBaseUrl = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "requireapi": options.RequireApi = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "startapiprobe": options.StartApiProbe = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "apiconfiguration": options.ApiConfiguration = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "apipersistenceprovider": options.ApiPersistenceProvider = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "apiconnectionstring": options.ApiConnectionString = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "apischema": options.ApiSchema = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "smokeclientid": options.SmokeClientId = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "smokeadminuser": options.SmokeAdminUser = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "smokeadminpassword": options.SmokeAdminPassword = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "smokevieweruser": options.SmokeViewerUser = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "smokeviewerpassword": options.SmokeViewerPassword = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "smokeforeignuser": options.SmokeForeignUser = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "smokeforeignpassword": options.SmokeForeignPassword = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    default:
                        error = $"Unsupported doctor option '{token}'.";
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
}
