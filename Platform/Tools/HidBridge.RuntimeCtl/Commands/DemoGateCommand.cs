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
/// Implements the <c>DemoGateCommand</c> RuntimeCtl lane.
/// Execution contract:
/// 1) Parse CLI arguments into strongly typed options.
/// 2) Execute lane-specific orchestration (native RuntimeCtl commands first, script bridge only when explicitly needed).
/// 3) Persist step logs/summaries under <c>Platform/.logs</c> and return process-style exit code semantics (0 = success).
/// </summary>
internal static class DemoGateCommand
{
    public static async Task<int> RunAsync(string platformRoot, IReadOnlyList<string> args)
    {
        var provider = ResolveTransportProvider(args);
        if (!string.Equals(provider, "webrtc-datachannel", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"WARNING: TransportProvider '{provider}' is delegated to legacy run_demo_gate.ps1 compatibility path.");
            return await RuntimeCtlApp.RunScriptBridgeAsync(platformRoot, Path.Combine("Scripts", "run_demo_gate.ps1"), args);
        }

        if (!DemoGateOptions.TryParse(args, out var options, out var parseError))
        {
            Console.Error.WriteLine($"demo-gate options error: {parseError}");
            return 1;
        }

        if (options.RequireDeviceAck)
        {
            Console.WriteLine("WARNING: RequireDeviceAck is ignored for TransportProvider=webrtc-datachannel.");
        }

        var smokeArgs = new List<string>
        {
            "-ApiBaseUrl", options.BaseUrl,
            "-KeycloakBaseUrl", options.KeycloakBaseUrl,
            "-RealmName", options.RealmName,
            "-ControlHealthUrl", options.ControlHealthUrl,
            "-ControlHealthAttempts", Math.Max(1, options.ControlHealthAttempts).ToString(),
            "-RequestTimeoutSec", Math.Max(1, options.RequestTimeoutSec).ToString(),
            "-TransportHealthAttempts", Math.Max(1, options.TransportHealthAttempts).ToString(),
            "-TransportHealthDelayMs", Math.Max(100, options.TransportHealthDelayMs).ToString(),
            "-TokenClientId", options.TokenClientId,
            "-TokenUsername", options.TokenUsername,
            "-TokenPassword", options.TokenPassword,
            "-PrincipalId", options.PrincipalId,
            "-TenantId", options.TenantId,
            "-OrganizationId", options.OrganizationId,
            "-TimeoutMs", Math.Max(8000, options.CommandTimeoutMs).ToString(),
        };

        if (!string.IsNullOrWhiteSpace(options.TokenClientSecret))
        {
            smokeArgs.Add("-TokenClientSecret");
            smokeArgs.Add(options.TokenClientSecret);
        }

        if (!string.IsNullOrWhiteSpace(options.TokenScope))
        {
            smokeArgs.Add("-TokenScope");
            smokeArgs.Add(options.TokenScope);
        }

        if (options.SkipTransportHealthCheck)
        {
            smokeArgs.Add("-SkipTransportHealthCheck");
        }

        if (!string.IsNullOrWhiteSpace(options.OutputJsonPath))
        {
            smokeArgs.Add("-OutputJsonPath");
            smokeArgs.Add(options.OutputJsonPath);
        }

        var exitCode = await WebRtcEdgeAgentSmokeCommand.RunAsync(platformRoot, smokeArgs);
        if (exitCode != 0)
        {
            return exitCode;
        }

        Console.WriteLine();
        Console.WriteLine("=== Demo Gate Summary ===");
        Console.WriteLine($"BaseUrl: {options.BaseUrl}");
        Console.WriteLine("Transport provider: webrtc-datachannel");
        if (!string.IsNullOrWhiteSpace(options.OutputJsonPath))
        {
            Console.WriteLine($"Summary JSON: {options.OutputJsonPath}");
        }

        return 0;
    }

    private static string ResolveTransportProvider(IReadOnlyList<string> args)
    {
        const string defaultProvider = "uart";
        for (var i = 0; i < args.Count; i++)
        {
            var token = args[i];
            if (string.IsNullOrWhiteSpace(token) || !token.StartsWith("-", StringComparison.Ordinal))
            {
                continue;
            }

            var name = token.TrimStart('-');
            if (!string.Equals(name, "transportprovider", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var hasValue = i + 1 < args.Count && !args[i + 1].StartsWith("-", StringComparison.Ordinal);
            if (!hasValue || string.IsNullOrWhiteSpace(args[i + 1]))
            {
                return defaultProvider;
            }

            return args[i + 1];
        }

        return defaultProvider;
    }

    private sealed class DemoGateOptions
    {
        public string BaseUrl { get; set; } = "http://127.0.0.1:18093";
        public string TransportProvider { get; set; } = "webrtc-datachannel";
        public string KeycloakBaseUrl { get; set; } = "http://127.0.0.1:18096";
        public string RealmName { get; set; } = "hidbridge-dev";
        public string TokenClientId { get; set; } = "controlplane-smoke";
        public string TokenClientSecret { get; set; } = string.Empty;
        public string TokenScope { get; set; } = "openid profile email";
        public string TokenUsername { get; set; } = "operator.smoke.admin";
        public string TokenPassword { get; set; } = "ChangeMe123!";
        public string PrincipalId { get; set; } = "demo-gate-admin";
        public string TenantId { get; set; } = "local-tenant";
        public string OrganizationId { get; set; } = "local-org";
        public int CommandTimeoutMs { get; set; } = 500;
        public bool RequireDeviceAck { get; set; }
        public string ControlHealthUrl { get; set; } = "http://127.0.0.1:28092/health";
        public int RequestTimeoutSec { get; set; } = 15;
        public int ControlHealthAttempts { get; set; } = 20;
        public bool SkipTransportHealthCheck { get; set; }
        public int TransportHealthAttempts { get; set; } = 20;
        public int TransportHealthDelayMs { get; set; } = 500;
        public string OutputJsonPath { get; set; } = string.Empty;

        public static bool TryParse(IReadOnlyList<string> args, out DemoGateOptions options, out string? error)
        {
            options = new DemoGateOptions();
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
                    case "baseurl":
                    case "apibaseurl":
                        options.BaseUrl = RequireValue(name, value, ref i, ref hasValue, ref error);
                        break;
                    case "transportprovider":
                        options.TransportProvider = RequireValue(name, value, ref i, ref hasValue, ref error);
                        break;
                    case "keycloakbaseurl":
                        options.KeycloakBaseUrl = RequireValue(name, value, ref i, ref hasValue, ref error);
                        break;
                    case "realmname":
                        options.RealmName = RequireValue(name, value, ref i, ref hasValue, ref error);
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
                    case "commandtimeoutms":
                        options.CommandTimeoutMs = ParseInt(name, value, hasValue, ref i, ref error);
                        break;
                    case "requiredeviceack":
                        options.RequireDeviceAck = ParseSwitch(name, value, hasValue, ref i, ref error);
                        break;
                    case "controlhealthurl":
                        options.ControlHealthUrl = RequireValue(name, value, ref i, ref hasValue, ref error);
                        break;
                    case "requesttimeoutsec":
                        options.RequestTimeoutSec = ParseInt(name, value, hasValue, ref i, ref error);
                        break;
                    case "controlhealthattempts":
                        options.ControlHealthAttempts = ParseInt(name, value, hasValue, ref i, ref error);
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
                    case "outputjsonpath":
                        options.OutputJsonPath = RequireValue(name, value, ref i, ref hasValue, ref error);
                        break;
                    default:
                        // Keep compatibility with legacy demo-gate parameters that are ignored
                        // by the WebRTC branch (e.g. profile/device ack selectors).
                        if (hasValue)
                        {
                            i++;
                        }
                        break;
                }

                if (error is not null)
                {
                    return false;
                }
            }

            if (!string.Equals(options.TransportProvider, "webrtc-datachannel", StringComparison.OrdinalIgnoreCase))
            {
                error = "TransportProvider must be 'webrtc-datachannel' for native demo-gate mode.";
                return false;
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
