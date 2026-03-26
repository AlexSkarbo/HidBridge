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
/// Implements the <c>SmokeCommand</c> RuntimeCtl lane.
/// Execution contract:
/// 1) Parse CLI arguments into strongly typed options.
/// 2) Execute lane-specific orchestration (native RuntimeCtl commands first, script bridge only when explicitly needed).
/// 3) Persist step logs/summaries under <c>Platform/.logs</c> and return process-style exit code semantics (0 = success).
/// </summary>
internal static class SmokeCommand
{
    internal enum SmokeMode
    {
        ProviderAuto,
        ForceFile,
        ForceSql,
    }

    public static async Task<int> RunAsync(string platformRoot, IReadOnlyList<string> args, SmokeMode mode)
    {
        if (!SmokeOptions.TryParse(args, out var options, out var parseError))
        {
            Console.Error.WriteLine($"smoke options error: {parseError}");
            return 1;
        }

        var effectiveProvider = mode switch
        {
            SmokeMode.ForceFile => "File",
            SmokeMode.ForceSql => "Sql",
            _ => options.Provider,
        };

        var bearerArgs = new List<string>
        {
            "-Configuration", options.Configuration,
            "-BaseUrl", options.BaseUrl,
            "-EnableApiAuth", options.EnableApiAuth ? "true" : "false",
            "-DisableHeaderFallback", options.DisableHeaderFallback ? "true" : "false",
            "-BearerOnly", options.BearerOnly ? "true" : "false",
            "-AuthAuthority", options.AuthAuthority,
            "-TokenClientId", options.TokenClientId,
            "-TokenScope", options.TokenScope,
            "-TokenUsername", options.TokenUsername,
            "-TokenPassword", options.TokenPassword,
            "-ViewerTokenUsername", options.ViewerTokenUsername,
            "-ViewerTokenPassword", options.ViewerTokenPassword,
            "-ForeignTokenUsername", options.ForeignTokenUsername,
            "-ForeignTokenPassword", options.ForeignTokenPassword,
            "-Provider", effectiveProvider,
        };

        if (!string.IsNullOrWhiteSpace(options.AuthAudience))
        {
            bearerArgs.Add("-AuthAudience");
            bearerArgs.Add(options.AuthAudience);
        }

        if (!string.IsNullOrWhiteSpace(options.TokenClientSecret))
        {
            bearerArgs.Add("-TokenClientSecret");
            bearerArgs.Add(options.TokenClientSecret);
        }

        if (!string.IsNullOrWhiteSpace(options.AccessToken))
        {
            bearerArgs.Add("-AccessToken");
            bearerArgs.Add(options.AccessToken);
        }

        if (string.Equals(effectiveProvider, "Sql", StringComparison.OrdinalIgnoreCase))
        {
            bearerArgs.Add("-ConnectionString");
            bearerArgs.Add(options.ConnectionString);
            bearerArgs.Add("-Schema");
            bearerArgs.Add(options.Schema);
        }

        if (options.NoBuild)
        {
            bearerArgs.Add("-NoBuild");
        }

        if (string.Equals(effectiveProvider, "File", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(options.DataRoot))
        {
            Console.WriteLine($"WARNING: -DataRoot is currently ignored by native smoke mode ({options.DataRoot}).");
        }

        return await BearerSmokeCommand.RunAsync(platformRoot, bearerArgs);
    }

    private sealed class SmokeOptions
    {
        public string Provider { get; set; } = "File";
        public string Configuration { get; set; } = "Debug";
        public string ConnectionString { get; set; } = "Host=127.0.0.1;Port=5434;Database=hidbridge;Username=hidbridge;Password=hidbridge";
        public string Schema { get; set; } = "hidbridge";
        public string BaseUrl { get; set; } = "http://127.0.0.1:18093";
        public string DataRoot { get; set; } = string.Empty;
        public string AccessToken { get; set; } = string.Empty;
        public bool EnableApiAuth { get; set; }
        public string AuthAuthority { get; set; } = "http://127.0.0.1:18096/realms/hidbridge-dev";
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
        public bool DisableHeaderFallback { get; set; }
        public bool BearerOnly { get; set; }
        public bool NoBuild { get; set; }

        public static bool TryParse(IReadOnlyList<string> args, out SmokeOptions options, out string? error)
        {
            options = new SmokeOptions();
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
                    case "dataroot": options.DataRoot = RequireValueAllowEmpty(name, value, ref i, ref hasValue, ref error); break;
                    case "accesstoken": options.AccessToken = RequireValueAllowEmpty(name, value, ref i, ref hasValue, ref error); break;
                    case "enableapiauth": options.EnableApiAuth = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "authauthority": options.AuthAuthority = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "authaudience": options.AuthAudience = RequireValueAllowEmpty(name, value, ref i, ref hasValue, ref error); break;
                    case "tokenclientid": options.TokenClientId = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "tokenclientsecret": options.TokenClientSecret = RequireValueAllowEmpty(name, value, ref i, ref hasValue, ref error); break;
                    case "tokenscope": options.TokenScope = RequireValueAllowEmpty(name, value, ref i, ref hasValue, ref error); break;
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
                        error = $"Unsupported smoke option '{token}'.";
                        break;
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

        private static string RequireValueAllowEmpty(string name, string? value, ref int index, ref bool hasValue, ref string? error)
        {
            if (!hasValue)
            {
                error = $"Option -{name} requires a value.";
                return string.Empty;
            }
            index++;
            return value ?? string.Empty;
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
