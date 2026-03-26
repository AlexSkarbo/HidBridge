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
/// Implements the <c>WebRtcEdgeAgentSmokeCommand</c> RuntimeCtl lane.
/// Execution contract:
/// 1) Parse CLI arguments into strongly typed options.
/// 2) Execute lane-specific orchestration (native RuntimeCtl commands first, script bridge only when explicitly needed).
/// 3) Persist step logs/summaries under <c>Platform/.logs</c> and return process-style exit code semantics (0 = success).
/// </summary>
internal static class WebRtcEdgeAgentSmokeCommand
{
    public static async Task<int> RunAsync(string platformRoot, IReadOnlyList<string> args)
    {
        if (!WebRtcEdgeAgentSmokeOptions.TryParse(args, out var options, out var parseError))
        {
            Console.Error.WriteLine($"webrtc-edge-agent-smoke options error: {parseError}");
            return 1;
        }

        var runnerProject = Path.Combine(platformRoot, "Tools", "HidBridge.Acceptance.Runner", "HidBridge.Acceptance.Runner.csproj");
        if (!File.Exists(runnerProject))
        {
            Console.Error.WriteLine($"Acceptance runner project not found: {runnerProject}");
            return 1;
        }

        var sessionEnvAbsolutePath = Path.IsPathRooted(options.SessionEnvPath)
            ? options.SessionEnvPath
            : Path.Combine(platformRoot, options.SessionEnvPath);
        var sessionEnv = ReadSessionEnv(sessionEnvAbsolutePath);
        var resolvedExecutor = string.IsNullOrWhiteSpace(options.CommandExecutor)
            ? sessionEnv.GetValueOrDefault("COMMAND_EXECUTOR") ?? "uart"
            : options.CommandExecutor;
        if (!string.Equals(resolvedExecutor, "uart", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(resolvedExecutor, "controlws", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"Unsupported command executor '{resolvedExecutor}'. Expected 'uart' or 'controlws'.");
            return 1;
        }

        var effectivePrincipal = !string.IsNullOrWhiteSpace(sessionEnv.GetValueOrDefault("PRINCIPAL_ID"))
            ? sessionEnv["PRINCIPAL_ID"]
            : options.PrincipalId;
        var effectiveTenant = !string.IsNullOrWhiteSpace(sessionEnv.GetValueOrDefault("TENANT_ID"))
            ? sessionEnv["TENANT_ID"]
            : options.TenantId;
        var effectiveOrganization = !string.IsNullOrWhiteSpace(sessionEnv.GetValueOrDefault("ORGANIZATION_ID"))
            ? sessionEnv["ORGANIZATION_ID"]
            : options.OrganizationId;
        var effectiveControlHealthUrl = !options.ControlHealthUrlSpecified
                                         && !string.IsNullOrWhiteSpace(sessionEnv.GetValueOrDefault("CONTROL_HEALTH_URL"))
            ? sessionEnv["CONTROL_HEALTH_URL"]
            : options.ControlHealthUrl;

        var runnerArgs = new List<string>
        {
            "--platform-root", platformRoot,
            "--smoke-only",
            "--skip-runtime-bootstrap",
            "--api-base-url", options.ApiBaseUrl,
            "--command-executor", resolvedExecutor,
            "--control-health-url", effectiveControlHealthUrl,
            "--request-timeout-sec", Math.Max(1, options.RequestTimeoutSec).ToString(),
            "--control-health-attempts", Math.Max(1, options.ControlHealthAttempts).ToString(),
            "--control-health-delay-ms", Math.Max(100, options.ControlHealthDelayMs).ToString(),
            "--keycloak-health-attempts", Math.Max(1, options.KeycloakHealthAttempts).ToString(),
            "--keycloak-health-delay-ms", Math.Max(100, options.KeycloakHealthDelayMs).ToString(),
            "--token-request-attempts", Math.Max(1, options.TokenRequestAttempts).ToString(),
            "--token-request-delay-ms", Math.Max(100, options.TokenRequestDelayMs).ToString(),
            "--transport-health-attempts", Math.Max(1, options.TransportHealthAttempts).ToString(),
            "--transport-health-delay-ms", Math.Max(100, options.TransportHealthDelayMs).ToString(),
            "--keycloak-base-url", options.KeycloakBaseUrl,
            "--realm-name", options.RealmName,
            "--token-client-id", options.TokenClientId,
            "--token-scope", options.TokenScope,
            "--token-username", options.TokenUsername,
            "--token-password", options.TokenPassword,
            "--principal-id", effectivePrincipal,
            "--tenant-id", effectiveTenant,
            "--organization-id", effectiveOrganization,
            "--command-action", options.CommandAction,
            "--timeout-ms", Math.Max(1000, options.TimeoutMs).ToString(),
            "--command-attempts", Math.Max(1, options.CommandAttempts).ToString(),
            "--command-retry-delay-ms", Math.Max(100, options.CommandRetryDelayMs).ToString(),
            "--lease-seconds", Math.Max(30, options.LeaseSeconds).ToString(),
            "--session-env-path", options.SessionEnvPath,
            "--output-json-path", options.OutputJsonPath,
        };

        if (!string.IsNullOrWhiteSpace(options.TokenClientSecret))
        {
            runnerArgs.Add("--token-client-secret");
            runnerArgs.Add(options.TokenClientSecret);
        }

        if (!string.IsNullOrWhiteSpace(options.CommandText))
        {
            runnerArgs.Add("--command-text");
            runnerArgs.Add(options.CommandText);
        }

        if (options.SkipControlHealthCheck || string.Equals(resolvedExecutor, "uart", StringComparison.OrdinalIgnoreCase))
        {
            runnerArgs.Add("--skip-control-health-check");
        }

        if (options.SkipTransportHealthCheck)
        {
            runnerArgs.Add("--skip-transport-health-check");
        }

        if (options.SkipControlLeaseRequest)
        {
            runnerArgs.Add("--skip-control-lease-request");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = platformRoot,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--project");
        startInfo.ArgumentList.Add(runnerProject);
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("Debug");
        startInfo.ArgumentList.Add("--");
        foreach (var item in runnerArgs)
        {
            startInfo.ArgumentList.Add(item);
        }

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            Console.Error.WriteLine("Failed to start smoke runner process.");
            return 1;
        }

        await process.WaitForExitAsync();
        return process.ExitCode;
    }

    private static Dictionary<string, string> ReadSessionEnv(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path))
        {
            return result;
        }

        foreach (var line in File.ReadAllLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var idx = line.IndexOf('=');
            if (idx <= 0)
            {
                continue;
            }

            var key = line[..idx].Trim();
            var value = line[(idx + 1)..].Trim();
            if (!string.IsNullOrWhiteSpace(key))
            {
                result[key] = value;
            }
        }

        return result;
    }

    private sealed class WebRtcEdgeAgentSmokeOptions
    {
        public string ApiBaseUrl { get; set; } = "http://127.0.0.1:18093";
        public string KeycloakBaseUrl { get; set; } = "http://127.0.0.1:18096";
        public string RealmName { get; set; } = "hidbridge-dev";
        public string ControlHealthUrl { get; set; } = "http://127.0.0.1:28092/health";
        public bool ControlHealthUrlSpecified { get; set; }
        public int RequestTimeoutSec { get; set; } = 15;
        public int ControlHealthAttempts { get; set; } = 20;
        public int ControlHealthDelayMs { get; set; } = 500;
        public int KeycloakHealthAttempts { get; set; } = 60;
        public int KeycloakHealthDelayMs { get; set; } = 500;
        public int TokenRequestAttempts { get; set; } = 5;
        public int TokenRequestDelayMs { get; set; } = 500;
        public bool SkipControlHealthCheck { get; set; }
        public bool SkipTransportHealthCheck { get; set; }
        public int TransportHealthAttempts { get; set; } = 20;
        public int TransportHealthDelayMs { get; set; } = 500;
        public string TokenClientId { get; set; } = "controlplane-smoke";
        public string TokenClientSecret { get; set; } = string.Empty;
        public string TokenScope { get; set; } = "openid profile email";
        public string TokenUsername { get; set; } = "operator.smoke.admin";
        public string TokenPassword { get; set; } = "ChangeMe123!";
        public string PrincipalId { get; set; } = "smoke-runner";
        public string TenantId { get; set; } = "local-tenant";
        public string OrganizationId { get; set; } = "local-org";
        public string CommandExecutor { get; set; } = string.Empty;
        public string CommandAction { get; set; } = "keyboard.text";
        public string CommandText { get; set; } = string.Empty;
        public int TimeoutMs { get; set; } = 8000;
        public int CommandAttempts { get; set; } = 3;
        public int CommandRetryDelayMs { get; set; } = 750;
        public bool SkipControlLeaseRequest { get; set; }
        public int LeaseSeconds { get; set; } = 120;
        public string SessionEnvPath { get; set; } = ".logs/webrtc-peer-adapter.session.env";
        public string OutputJsonPath { get; set; } = "Platform/.logs/webrtc-edge-agent-smoke.result.json";

        public static bool TryParse(IReadOnlyList<string> args, out WebRtcEdgeAgentSmokeOptions options, out string? error)
        {
            options = new WebRtcEdgeAgentSmokeOptions();
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
                    case "apibaseurl":
                    case "baseurl":
                        options.ApiBaseUrl = RequireValue(name, value, ref i, ref hasValue, ref error);
                        break;
                    case "keycloakbaseurl":
                        options.KeycloakBaseUrl = RequireValue(name, value, ref i, ref hasValue, ref error);
                        break;
                    case "realmname":
                        options.RealmName = RequireValue(name, value, ref i, ref hasValue, ref error);
                        break;
                    case "controlhealthurl":
                        options.ControlHealthUrl = RequireValue(name, value, ref i, ref hasValue, ref error);
                        options.ControlHealthUrlSpecified = true;
                        break;
                    case "requesttimeoutsec":
                        options.RequestTimeoutSec = ParseInt(name, value, hasValue, ref i, ref error);
                        break;
                    case "controlhealthattempts":
                        options.ControlHealthAttempts = ParseInt(name, value, hasValue, ref i, ref error);
                        break;
                    case "controlhealthdelayms":
                        options.ControlHealthDelayMs = ParseInt(name, value, hasValue, ref i, ref error);
                        break;
                    case "keycloakhealthattempts":
                        options.KeycloakHealthAttempts = ParseInt(name, value, hasValue, ref i, ref error);
                        break;
                    case "keycloakhealthdelayms":
                        options.KeycloakHealthDelayMs = ParseInt(name, value, hasValue, ref i, ref error);
                        break;
                    case "tokenrequestattempts":
                        options.TokenRequestAttempts = ParseInt(name, value, hasValue, ref i, ref error);
                        break;
                    case "tokenrequestdelayms":
                        options.TokenRequestDelayMs = ParseInt(name, value, hasValue, ref i, ref error);
                        break;
                    case "skipcontrolhealthcheck":
                        options.SkipControlHealthCheck = ParseSwitch(name, value, hasValue, ref i, ref error);
                        break;
                    case "skiptransporthealthcheck":
                        options.SkipTransportHealthCheck = ParseSwitch(name, value, hasValue, ref i, ref error);
                        break;
                    case "transporthealthattempts":
                        options.TransportHealthAttempts = ParseInt(name, value, hasValue, ref i, ref error);
                        break;
                    case "transporthealthdelayms":
                        options.TransportHealthDelayMs = ParseInt(name, value, hasValue, ref i, ref error);
                        break;
                    case "tokenclientid":
                        options.TokenClientId = RequireValue(name, value, ref i, ref hasValue, ref error);
                        break;
                    case "tokenclientsecret":
                        options.TokenClientSecret = RequireValue(name, value, ref i, ref hasValue, ref error);
                        break;
                    case "tokenscope":
                        options.TokenScope = RequireValue(name, value, ref i, ref hasValue, ref error);
                        break;
                    case "tokenusername":
                        options.TokenUsername = RequireValue(name, value, ref i, ref hasValue, ref error);
                        break;
                    case "tokenpassword":
                        options.TokenPassword = RequireValue(name, value, ref i, ref hasValue, ref error);
                        break;
                    case "principalid":
                        options.PrincipalId = RequireValue(name, value, ref i, ref hasValue, ref error);
                        break;
                    case "tenantid":
                        options.TenantId = RequireValue(name, value, ref i, ref hasValue, ref error);
                        break;
                    case "organizationid":
                        options.OrganizationId = RequireValue(name, value, ref i, ref hasValue, ref error);
                        break;
                    case "commandexecutor":
                        options.CommandExecutor = RequireValue(name, value, ref i, ref hasValue, ref error);
                        break;
                    case "commandaction":
                        options.CommandAction = RequireValue(name, value, ref i, ref hasValue, ref error);
                        break;
                    case "commandtext":
                        options.CommandText = RequireValue(name, value, ref i, ref hasValue, ref error);
                        break;
                    case "timeoutms":
                        options.TimeoutMs = ParseInt(name, value, hasValue, ref i, ref error);
                        break;
                    case "commandattempts":
                        options.CommandAttempts = ParseInt(name, value, hasValue, ref i, ref error);
                        break;
                    case "commandretrydelayms":
                        options.CommandRetryDelayMs = ParseInt(name, value, hasValue, ref i, ref error);
                        break;
                    case "skipcontrolleaserequest":
                        options.SkipControlLeaseRequest = ParseSwitch(name, value, hasValue, ref i, ref error);
                        break;
                    case "leaseseconds":
                        options.LeaseSeconds = ParseInt(name, value, hasValue, ref i, ref error);
                        break;
                    case "sessionenvpath":
                        options.SessionEnvPath = RequireValue(name, value, ref i, ref hasValue, ref error);
                        break;
                    case "outputjsonpath":
                        options.OutputJsonPath = RequireValue(name, value, ref i, ref hasValue, ref error);
                        break;
                    default:
                        error = $"Unsupported webrtc-edge-agent-smoke option '{token}'.";
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
