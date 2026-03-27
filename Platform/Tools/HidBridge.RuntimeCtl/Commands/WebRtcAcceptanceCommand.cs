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
/// Implements the <c>WebRtcAcceptanceCommand</c> RuntimeCtl lane.
/// Execution contract:
/// 1) Parse CLI arguments into strongly typed options.
/// 2) Execute lane-specific orchestration (native RuntimeCtl commands first, script bridge only when explicitly needed).
/// 3) Persist step logs/summaries under <c>Platform/.logs</c> and return process-style exit code semantics (0 = success).
/// </summary>
internal static class WebRtcAcceptanceCommand
{
    public static async Task<int> RunAsync(string platformRoot, IReadOnlyList<string> args)
    {
        if (!WebRtcAcceptanceOptions.TryParse(args, out var options, out var parseError))
        {
            Console.Error.WriteLine($"webrtc-acceptance options error: {parseError}");
            return 1;
        }

        var runnerProject = Path.Combine(platformRoot, "Tools", "HidBridge.Acceptance.Runner", "HidBridge.Acceptance.Runner.csproj");
        if (!File.Exists(runnerProject))
        {
            Console.Error.WriteLine($"Acceptance runner project not found: {runnerProject}");
            return 1;
        }
        var runnerDll = Path.Combine(
            Path.GetDirectoryName(runnerProject)!,
            "bin",
            "Debug",
            "net10.0",
            "HidBridge.Acceptance.Runner.dll");

        if (options.StopExisting || options.AutoCleanupStaleProcesses)
        {
            await ProcessCleanup.CleanupStaleEdgeProcessesAsync();
        }

        var runnerArgs = new List<string>
        {
            "--platform-root", platformRoot,
            "--runtimectl-dll", Path.Combine(AppContext.BaseDirectory, "HidBridge.RuntimeCtl.dll"),
            "--api-base-url", options.ApiBaseUrl,
            "--web-base-url", options.WebBaseUrl,
            "--command-executor", options.CommandExecutor,
            "--control-health-url", options.ControlHealthUrl,
            "--keycloak-base-url", options.KeycloakBaseUrl,
            "--realm-name", options.RealmName,
            "--token-client-id", options.TokenClientId,
            "--token-username", options.TokenUsername,
            "--token-password", options.TokenPassword,
            "--request-timeout-sec", options.RequestTimeoutSec.ToString(),
            "--control-health-attempts", options.ControlHealthAttempts.ToString(),
            "--keycloak-health-attempts", options.KeycloakHealthAttempts.ToString(),
            "--keycloak-health-delay-ms", options.KeycloakHealthDelayMs.ToString(),
            "--token-request-attempts", options.TokenRequestAttempts.ToString(),
            "--token-request-delay-ms", options.TokenRequestDelayMs.ToString(),
            "--transport-health-attempts", options.TransportHealthAttempts.ToString(),
            "--transport-health-delay-ms", options.TransportHealthDelayMs.ToString(),
            "--media-health-attempts", options.MediaHealthAttempts.ToString(),
            "--media-health-delay-ms", options.MediaHealthDelayMs.ToString(),
            "--media-endpoint-preflight-timeout-ms", options.MediaEndpointPreflightTimeoutMs.ToString(),
            "--uart-port", options.UartPort,
            "--uart-baud", options.UartBaud.ToString(),
            "--uart-hmac-key", options.UartHmacKey,
            "--principal-id", options.PrincipalId,
            "--peer-ready-timeout-sec", options.PeerReadyTimeoutSec.ToString(),
            "--stack-bootstrap-timeout-sec", options.StackBootstrapTimeoutSec.ToString(),
            "--output-json-path", options.OutputJsonPath,
        };

        if (options.AllowLegacyControlWs)
        {
            runnerArgs.Add("--allow-legacy-controlws");
        }

        if (options.SkipTransportHealthCheck)
        {
            runnerArgs.Add("--skip-transport-health-check");
        }

        if (options.RequireMediaReady)
        {
            runnerArgs.Add("--require-media-ready");
        }

        if (options.RequireMediaPlaybackUrl)
        {
            runnerArgs.Add("--require-media-playback-url");
        }

        if (options.SkipMediaEndpointPreflight)
        {
            runnerArgs.Add("--skip-media-endpoint-preflight");
        }

        if (!string.IsNullOrWhiteSpace(options.TokenClientSecret))
        {
            runnerArgs.Add("--token-client-secret");
            runnerArgs.Add(options.TokenClientSecret);
        }

        if (!string.IsNullOrWhiteSpace(options.TokenScope))
        {
            runnerArgs.Add("--token-scope");
            runnerArgs.Add(options.TokenScope);
        }

        if (options.SkipRuntimeBootstrap)
        {
            runnerArgs.Add("--skip-runtime-bootstrap");
        }

        if (options.StopExisting)
        {
            runnerArgs.Add("--stop-existing");
        }

        if (options.StopStackAfter)
        {
            runnerArgs.Add("--stop-stack-after");
        }

        if (!string.IsNullOrWhiteSpace(options.ControlWsUrl))
        {
            runnerArgs.Add("--control-ws-url");
            runnerArgs.Add(options.ControlWsUrl);
        }

        if (!string.IsNullOrWhiteSpace(options.MediaHealthUrl))
        {
            runnerArgs.Add("--media-health-url");
            runnerArgs.Add(options.MediaHealthUrl);
        }

        if (!string.IsNullOrWhiteSpace(options.MediaWhipUrl))
        {
            runnerArgs.Add("--media-whip-url");
            runnerArgs.Add(options.MediaWhipUrl);
        }

        if (!string.IsNullOrWhiteSpace(options.MediaWhepUrl))
        {
            runnerArgs.Add("--media-whep-url");
            runnerArgs.Add(options.MediaWhepUrl);
        }

        if (!string.IsNullOrWhiteSpace(options.MediaPlaybackUrl))
        {
            runnerArgs.Add("--media-playback-url");
            runnerArgs.Add(options.MediaPlaybackUrl);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = platformRoot,
            UseShellExecute = false,
        };

        var buildExitCode = await RunDotnetAsync(
            platformRoot,
            [
                "build",
                runnerProject,
                "-c", "Debug",
                "-v", "minimal",
                "-nologo",
            ]);
        if (buildExitCode != 0)
        {
            Console.Error.WriteLine($"Failed to build acceptance runner (exit code {buildExitCode}).");
            return 1;
        }

        if (!File.Exists(runnerDll))
        {
            Console.Error.WriteLine($"Acceptance runner build output not found: {runnerDll}");
            return 1;
        }

        startInfo.ArgumentList.Add(runnerDll);
        foreach (var arg in runnerArgs)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            Console.Error.WriteLine("Failed to start acceptance runner process.");
            return 1;
        }

        var maxDuration = TimeSpan.FromSeconds(Math.Max(60, options.MaxDurationSec));
        try
        {
            await process.WaitForExitAsync().WaitAsync(maxDuration);
        }
        catch (TimeoutException)
        {
            await TerminateAcceptanceProcessAsync(process);
            if (options.AutoCleanupStaleProcesses)
            {
                await ProcessCleanup.CleanupStaleEdgeProcessesAsync();
            }

            Console.Error.WriteLine($"WebRTC acceptance exceeded timeout ({(int)maxDuration.TotalSeconds}s) and was terminated.");
            return 1;
        }

        var exitCode = process.ExitCode;
        if (exitCode != 0 && options.AutoCleanupStaleProcesses)
        {
            await ProcessCleanup.CleanupStaleEdgeProcessesAsync();
        }

        return exitCode;
    }

    private static async Task TerminateAcceptanceProcessAsync(Process process)
    {
        if (process.HasExited)
        {
            return;
        }

        try
        {
            process.Kill(entireProcessTree: false);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            return;
        }
        catch
        {
            // Escalate to tree kill.
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // best-effort kill
        }
    }

    private static async Task<int> RunDotnetAsync(string workingDirectory, IReadOnlyList<string> args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
        };

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return 1;
        }

        await process.WaitForExitAsync();
        return process.ExitCode;
    }

    private sealed class WebRtcAcceptanceOptions
    {
        public string ApiBaseUrl { get; set; } = "http://127.0.0.1:18093";
        public string WebBaseUrl { get; set; } = "http://127.0.0.1:18110";
        public string CommandExecutor { get; set; } = "uart";
        public bool AllowLegacyControlWs { get; set; }
        public string ControlHealthUrl { get; set; } = "http://127.0.0.1:28092/health";
        public string ControlWsUrl { get; set; } = string.Empty;
        public string MediaHealthUrl { get; set; } = Environment.GetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_MEDIAHEALTHURL") ?? string.Empty;
        public string MediaWhipUrl { get; set; } = Environment.GetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_MEDIAWHIPURL") ?? string.Empty;
        public string MediaWhepUrl { get; set; } = Environment.GetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_MEDIAWHEPURL") ?? string.Empty;
        public string MediaPlaybackUrl { get; set; } = Environment.GetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_MEDIAPLAYBACKURL") ?? string.Empty;
        public string KeycloakBaseUrl { get; set; } = "http://host.docker.internal:18096";
        public string RealmName { get; set; } = "hidbridge-dev";
        public string TokenClientId { get; set; } = "controlplane-smoke";
        public string TokenClientSecret { get; set; } = string.Empty;
        public string TokenScope { get; set; } = "openid";
        public string TokenUsername { get; set; } = "operator.smoke.admin";
        public string TokenPassword { get; set; } = "ChangeMe123!";
        public int RequestTimeoutSec { get; set; } = 15;
        public int ControlHealthAttempts { get; set; } = 20;
        public int KeycloakHealthAttempts { get; set; } = 60;
        public int KeycloakHealthDelayMs { get; set; } = 500;
        public int TokenRequestAttempts { get; set; } = 5;
        public int TokenRequestDelayMs { get; set; } = 500;
        public bool SkipTransportHealthCheck { get; set; }
        public int TransportHealthAttempts { get; set; } = 20;
        public int TransportHealthDelayMs { get; set; } = 500;
        public bool RequireMediaReady { get; set; }
        public bool RequireMediaPlaybackUrl { get; set; }
        public int MediaHealthAttempts { get; set; } = 20;
        public int MediaHealthDelayMs { get; set; } = 500;
        public bool SkipMediaEndpointPreflight { get; set; } =
            ParseBoolEnv("HIDBRIDGE_EDGE_PROXY_MEDIABACKENDAUTOSTART");
        public int MediaEndpointPreflightTimeoutMs { get; set; } = 1500;
        public string UartPort { get; set; } = "COM6";
        public int UartBaud { get; set; } = 3000000;
        public string UartHmacKey { get; set; } = "your-master-secret";
        public string PrincipalId { get; set; } = "smoke-runner";
        public int PeerReadyTimeoutSec { get; set; } = 45;
        public bool SkipRuntimeBootstrap { get; set; }
        public bool StopExisting { get; set; }
        public bool StopStackAfter { get; set; }
        public bool AutoCleanupStaleProcesses { get; set; } = true;
        public int StackBootstrapTimeoutSec { get; set; } = 90;
        public int MaxDurationSec { get; set; } = 600;
        public string OutputJsonPath { get; set; } = ".logs/webrtc-edge-agent-acceptance.result.json";

        public static bool TryParse(IReadOnlyList<string> args, out WebRtcAcceptanceOptions options, out string? error)
        {
            options = new WebRtcAcceptanceOptions();
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
                    error = $"Unexpected token '{token}'. Expected PowerShell-style option (e.g. -CommandExecutor).";
                    return false;
                }

                var name = token.TrimStart('-');
                var hasValue = i + 1 < args.Count && !args[i + 1].StartsWith("-", StringComparison.Ordinal);
                string? value = hasValue ? args[i + 1] : null;

                switch (name.ToLowerInvariant())
                {
                    case "apibaseurl": options.ApiBaseUrl = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "baseurl": options.ApiBaseUrl = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "webbaseurl": options.WebBaseUrl = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "commandexecutor": options.CommandExecutor = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "allowlegacycontrolws": options.AllowLegacyControlWs = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "controlhealthurl": options.ControlHealthUrl = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "controlwsurl": options.ControlWsUrl = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "mediahealthurl": options.MediaHealthUrl = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "mediawhipurl": options.MediaWhipUrl = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "mediawhepurl": options.MediaWhepUrl = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "mediaplaybackurl": options.MediaPlaybackUrl = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "keycloakbaseurl": options.KeycloakBaseUrl = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "realmname": options.RealmName = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "tokenclientid": options.TokenClientId = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "tokenclientsecret": options.TokenClientSecret = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "tokenscope": options.TokenScope = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "tokenusername": options.TokenUsername = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "tokenpassword": options.TokenPassword = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "requesttimeoutsec": options.RequestTimeoutSec = ParseInt(name, value, hasValue, ref i, ref error); break;
                    case "controlhealthattempts": options.ControlHealthAttempts = ParseInt(name, value, hasValue, ref i, ref error); break;
                    case "keycloakhealthattempts": options.KeycloakHealthAttempts = ParseInt(name, value, hasValue, ref i, ref error); break;
                    case "keycloakhealthdelayms": options.KeycloakHealthDelayMs = ParseInt(name, value, hasValue, ref i, ref error); break;
                    case "tokenrequestattempts": options.TokenRequestAttempts = ParseInt(name, value, hasValue, ref i, ref error); break;
                    case "tokenrequestdelayms": options.TokenRequestDelayMs = ParseInt(name, value, hasValue, ref i, ref error); break;
                    case "skiptransporthealthcheck": options.SkipTransportHealthCheck = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "transporthealthattempts": options.TransportHealthAttempts = ParseInt(name, value, hasValue, ref i, ref error); break;
                    case "transporthealthdelayms": options.TransportHealthDelayMs = ParseInt(name, value, hasValue, ref i, ref error); break;
                    case "requiremediaready": options.RequireMediaReady = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "requiremediaplaybackurl": options.RequireMediaPlaybackUrl = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "mediahealthattempts": options.MediaHealthAttempts = ParseInt(name, value, hasValue, ref i, ref error); break;
                    case "mediahealthdelayms": options.MediaHealthDelayMs = ParseInt(name, value, hasValue, ref i, ref error); break;
                    case "skipmediaendpointpreflight": options.SkipMediaEndpointPreflight = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "mediaendpointpreflighttimeoutms": options.MediaEndpointPreflightTimeoutMs = ParseInt(name, value, hasValue, ref i, ref error); break;
                    case "uartport": options.UartPort = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "uartbaud": options.UartBaud = ParseInt(name, value, hasValue, ref i, ref error); break;
                    case "uarthmackey": options.UartHmacKey = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "principalid": options.PrincipalId = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "peerreadytimeoutsec": options.PeerReadyTimeoutSec = ParseInt(name, value, hasValue, ref i, ref error); break;
                    case "skipruntimebootstrap": options.SkipRuntimeBootstrap = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "stopexisting": options.StopExisting = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "stopstackafter": options.StopStackAfter = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "autocleanupstaleprocesses": options.AutoCleanupStaleProcesses = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "stackbootstraptimeoutsec": options.StackBootstrapTimeoutSec = ParseInt(name, value, hasValue, ref i, ref error); break;
                    case "maxdurationsec": options.MaxDurationSec = ParseInt(name, value, hasValue, ref i, ref error); break;
                    case "outputjsonpath": options.OutputJsonPath = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    default:
                        error = $"Unsupported webrtc-acceptance option '{token}'.";
                        return false;
                }

                if (error is not null)
                {
                    return false;
                }
            }

            if (!string.Equals(options.CommandExecutor, "uart", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(options.CommandExecutor, "controlws", StringComparison.OrdinalIgnoreCase))
            {
                error = "CommandExecutor must be 'uart' or 'controlws'.";
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

        private static bool ParseBoolEnv(string envName)
        {
            var value = Environment.GetEnvironmentVariable(envName);
            return TryParseBool(value, out var parsed) && parsed;
        }
    }
}
