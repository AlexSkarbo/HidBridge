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
/// Implements the <c>CiLocalCommand</c> RuntimeCtl lane.
/// Execution contract:
/// 1) Parse CLI arguments into strongly typed options.
/// 2) Execute lane-specific orchestration (native RuntimeCtl commands first, script bridge only when explicitly needed).
/// 3) Persist step logs/summaries under <c>Platform/.logs</c> and return process-style exit code semantics (0 = success).
/// </summary>
internal static class CiLocalCommand
{
    public static async Task<int> RunAsync(string platformRoot, IReadOnlyList<string> args)
    {
        if (!CiLocalOptions.TryParse(args, out var options, out var parseError))
        {
            Console.Error.WriteLine($"ci-local options error: {parseError}");
            return 1;
        }

        var logRoot = Path.Combine(platformRoot, ".logs", "ci-local", DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(logRoot);

        var results = new List<CiStepResult>();
        string artifactExportPath = string.Empty;

        try
        {
            if (!options.SkipDoctor)
            {
                await RunRuntimeCtlStepAsync(
                    platformRoot,
                    logRoot,
                    results,
                    options,
                    "Doctor",
                    "doctor",
                    [
                        "-StartApiProbe",
                        "-RequireApi",
                        "-KeycloakBaseUrl", options.KeycloakBaseUrl,
                        "-SmokeClientId", options.TokenClientId,
                        "-SmokeAdminUser", options.TokenUsername,
                        "-SmokeAdminPassword", options.TokenPassword,
                        "-SmokeViewerUser", options.ViewerTokenUsername,
                        "-SmokeViewerPassword", options.ViewerTokenPassword,
                        "-SmokeForeignUser", options.ForeignTokenUsername,
                        "-SmokeForeignPassword", options.ForeignTokenPassword,
                    ]);
            }

            if (!options.SkipChecks)
            {
                var checksArgs = new List<string>
                {
                    "-Provider", "Sql",
                    "-Configuration", options.Configuration,
                    "-ConnectionString", options.ConnectionString,
                    "-Schema", options.Schema,
                    "-EnableApiAuth",
                    "-AuthAuthority", options.AuthAuthority,
                    "-AuthAudience", "",
                    "-TokenClientId", options.TokenClientId,
                    "-TokenUsername", options.TokenUsername,
                    "-TokenPassword", options.TokenPassword,
                    "-ViewerTokenUsername", options.ViewerTokenUsername,
                    "-ViewerTokenPassword", options.ViewerTokenPassword,
                    "-ForeignTokenUsername", options.ForeignTokenUsername,
                    "-ForeignTokenPassword", options.ForeignTokenPassword,
                    "-DisableHeaderFallback",
                    "-BearerOnly",
                };

                if (!string.IsNullOrWhiteSpace(options.TokenClientSecret))
                {
                    checksArgs.Add("-TokenClientSecret");
                    checksArgs.Add(options.TokenClientSecret);
                }

                await RunRuntimeCtlStepAsync(
                    platformRoot,
                    logRoot,
                    results,
                    options,
                    "Checks (Sql)",
                    "checks",
                    checksArgs);
            }

            if (!options.SkipBearerSmoke)
            {
                var bearerArgs = new List<string>
                {
                    "-Configuration", options.Configuration,
                    "-ConnectionString", options.ConnectionString,
                    "-Schema", options.Schema,
                    "-AuthAuthority", options.AuthAuthority,
                    "-TokenClientId", options.TokenClientId,
                    "-TokenScope", options.TokenScope,
                    "-TokenUsername", options.TokenUsername,
                    "-TokenPassword", options.TokenPassword,
                    "-ViewerTokenUsername", options.ViewerTokenUsername,
                    "-ViewerTokenPassword", options.ViewerTokenPassword,
                    "-ForeignTokenUsername", options.ForeignTokenUsername,
                    "-ForeignTokenPassword", options.ForeignTokenPassword,
                };

                if (!string.IsNullOrWhiteSpace(options.TokenClientSecret))
                {
                    bearerArgs.Add("-TokenClientSecret");
                    bearerArgs.Add(options.TokenClientSecret);
                }

                await RunRuntimeCtlStepAsync(
                    platformRoot,
                    logRoot,
                    results,
                    options,
                    "Bearer Smoke",
                    "bearer-smoke",
                    bearerArgs);
            }

            if (!options.IncludeWebRtcEdgeAgentAcceptance && !options.SkipWebRtcEdgeAgentAcceptance)
            {
                throw new InvalidOperationException("WebRTC edge-agent acceptance lane is required for CI gate. Set -SkipWebRtcEdgeAgentAcceptance only for emergency/local override.");
            }

            if (options.IncludeWebRtcEdgeAgentAcceptance && !options.SkipWebRtcEdgeAgentAcceptance)
            {
                if (!options.IncludeWebRtcMediaE2EGate && !options.SkipWebRtcMediaE2EGate)
                {
                    throw new InvalidOperationException("WebRTC media E2E gate is required for CI gate. Set -SkipWebRtcMediaE2EGate only for emergency/local override.");
                }

                if (string.Equals(options.WebRtcCommandExecutor, "controlws", StringComparison.OrdinalIgnoreCase)
                    && !options.AllowLegacyControlWs)
                {
                    throw new InvalidOperationException("WebRtcCommandExecutor 'controlws' is legacy compatibility mode. Use 'uart' for production path, or pass -AllowLegacyControlWs explicitly.");
                }

                var acceptanceArgs = new List<string>
                {
                    "-ApiBaseUrl", "http://127.0.0.1:18093",
                    "-CommandExecutor", options.WebRtcCommandExecutor,
                    "-ControlHealthUrl", options.WebRtcControlHealthUrl,
                    "-KeycloakBaseUrl", options.KeycloakBaseUrl,
                    "-TokenClientId", options.TokenClientId,
                    "-TokenScope", options.TokenScope,
                    "-TokenUsername", options.TokenUsername,
                    "-TokenPassword", options.TokenPassword,
                    "-PeerReadyTimeoutSec", Math.Max(5, options.WebRtcPeerReadyTimeoutSec).ToString(),
                    "-SkipRuntimeBootstrap",
                };

                if (!string.IsNullOrWhiteSpace(options.TokenClientSecret))
                {
                    acceptanceArgs.Add("-TokenClientSecret");
                    acceptanceArgs.Add(options.TokenClientSecret);
                }

                if (options.AllowLegacyControlWs)
                {
                    acceptanceArgs.Add("-AllowLegacyControlWs");
                }

                if (options.WebRtcStopExistingBeforeAcceptance)
                {
                    acceptanceArgs.Add("-StopExisting");
                }

                if (options.WebRtcStopStackAfterAcceptance)
                {
                    acceptanceArgs.Add("-StopStackAfter");
                }

                if (options.IncludeWebRtcMediaE2EGate && !options.SkipWebRtcMediaE2EGate)
                {
                    acceptanceArgs.Add("-RequireMediaReady");
                    acceptanceArgs.Add("-RequireMediaPlaybackUrl");
                    acceptanceArgs.Add("-MediaHealthAttempts");
                    acceptanceArgs.Add(Math.Max(1, options.WebRtcMediaHealthAttempts).ToString());
                    acceptanceArgs.Add("-MediaHealthDelayMs");
                    acceptanceArgs.Add(Math.Max(100, options.WebRtcMediaHealthDelayMs).ToString());
                }

                await RunRuntimeCtlStepAsync(
                    platformRoot,
                    logRoot,
                    results,
                    options,
                    "WebRTC Edge Agent Acceptance",
                    "webrtc-acceptance",
                    acceptanceArgs);
            }

            if (!options.IncludeOpsSloSecurityVerify && !options.SkipOpsSloSecurityVerify)
            {
                throw new InvalidOperationException("Ops SLO + Security verify lane is required for CI gate. Set -SkipOpsSloSecurityVerify only for emergency/local override.");
            }

            if (options.IncludeOpsSloSecurityVerify && !options.SkipOpsSloSecurityVerify)
            {
                var verifyArgs = new List<string>
                {
                    "-BaseUrl", "http://127.0.0.1:18093",
                    "-SloWindowMinutes", Math.Max(5, options.OpsSloWindowMinutes).ToString(),
                    "-AuthAuthority", options.AuthAuthority,
                    "-TokenClientId", options.TokenClientId,
                    "-TokenScope", options.TokenScope,
                    "-TokenUsername", options.TokenUsername,
                    "-TokenPassword", options.TokenPassword,
                };

                if (!string.IsNullOrWhiteSpace(options.TokenClientSecret))
                {
                    verifyArgs.Add("-TokenClientSecret");
                    verifyArgs.Add(options.TokenClientSecret);
                }

                if (options.IncludeWebRtcEdgeAgentAcceptance && !options.SkipWebRtcEdgeAgentAcceptance)
                {
                    verifyArgs.Add("-RequireAuditTrailCategories");
                }

                if (options.OpsFailOnSloWarning)
                {
                    verifyArgs.Add("-FailOnSloWarning");
                }

                if (options.OpsFailOnSecurityWarning)
                {
                    verifyArgs.Add("-FailOnSecurityWarning");
                }

                await RunRuntimeCtlStepAsync(
                    platformRoot,
                    logRoot,
                    results,
                    options,
                    "Ops SLO + Security Verify",
                    "ops-verify",
                    verifyArgs);
            }
        }
        catch (Exception ex)
        {
            var abortPath = Path.Combine(logRoot, "ci-local.abort.log");
            await File.WriteAllTextAsync(abortPath, ex.ToString());
            results.Add(new CiStepResult("CI Local orchestration", "FAIL", 0, 1, abortPath));
            Console.WriteLine();
            Console.WriteLine($"Local CI aborted: {ex.Message}");
        }

        var failed = results.Where(static x => string.Equals(x.Status, "FAIL", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (options.ExportArtifactsOnFailure && failed.Length > 0)
        {
            artifactExportPath = Path.Combine(platformRoot, "Artifacts", $"ci-local-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}");

            try
            {
                await ArtifactExportCommand.ExportAsync(
                    platformRoot,
                    artifactExportPath,
                    options.IncludeSmokeDataOnFailure,
                    options.IncludeBackupsOnFailure,
                    Path.Combine(logRoot, "export-artifacts.log"));
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                Console.WriteLine($"Artifact export failed: {ex.Message}");
            }
        }

        PrintSummary(results, logRoot, artifactExportPath);
        return results.Any(static x => string.Equals(x.Status, "FAIL", StringComparison.OrdinalIgnoreCase)) ? 1 : 0;
    }

    private static async Task RunRuntimeCtlStepAsync(
        string platformRoot,
        string logRoot,
        List<CiStepResult> results,
        CiLocalOptions options,
        string stepName,
        string command,
        IReadOnlyList<string> commandArgs)
    {
        var logPath = Path.Combine(logRoot, stepName.Replace(' ', '-').Replace("(", string.Empty).Replace(")", string.Empty) + ".log");
        var runtimeCtlProject = Path.Combine(platformRoot, "Tools", "HidBridge.RuntimeCtl", "HidBridge.RuntimeCtl.csproj");

        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = platformRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--project");
        startInfo.ArgumentList.Add(runtimeCtlProject);
        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add("--platform-root");
        startInfo.ArgumentList.Add(platformRoot);
        startInfo.ArgumentList.Add(command);
        foreach (var arg in commandArgs)
        {
            startInfo.ArgumentList.Add(arg);
        }

        var stopwatch = Stopwatch.StartNew();
        using var process = Process.Start(startInfo);
        if (process is null)
        {
            throw new InvalidOperationException($"Failed to start RuntimeCtl command '{command}'.");
        }
        var stdOutTask = process.StandardOutput.ReadToEndAsync();
        var stdErrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var stdOut = await stdOutTask;
        var stdErr = await stdErrTask;
        stopwatch.Stop();

        await File.WriteAllTextAsync(logPath, $"{stdOut}{Environment.NewLine}{stdErr}".Trim());
        var status = process.ExitCode == 0 ? "PASS" : "FAIL";
        results.Add(new CiStepResult(stepName, status, stopwatch.Elapsed.TotalSeconds, process.ExitCode, logPath));

        Console.WriteLine();
        Console.WriteLine($"=== {stepName} ===");
        Console.WriteLine($"{status}  {stepName}");
        Console.WriteLine($"Log:   {logPath}");

        if (process.ExitCode != 0 && options.StopOnFailure)
        {
            throw new InvalidOperationException($"{stepName} failed with exit code {process.ExitCode}");
        }
    }

    private static void PrintSummary(IReadOnlyList<CiStepResult> results, string logRoot, string artifactExportPath)
    {
        Console.WriteLine();
        Console.WriteLine("=== CI Local Summary ===");
        Console.WriteLine();
        Console.WriteLine("Name                         Status Seconds ExitCode");
        Console.WriteLine("----                         ------ ------- --------");

        foreach (var step in results)
        {
            Console.WriteLine($"{step.Name,-28} {step.Status,-6} {step.Seconds,7:0.##} {step.ExitCode,8}");
        }

        Console.WriteLine();
        Console.WriteLine($"Logs root: {logRoot}");
        foreach (var step in results)
        {
            Console.WriteLine($"{step.Name}: {step.LogPath}");
        }

        if (!string.IsNullOrWhiteSpace(artifactExportPath))
        {
            Console.WriteLine();
            Console.WriteLine($"Artifacts: {artifactExportPath}");
        }
    }

    private sealed record CiStepResult(string Name, string Status, double Seconds, int ExitCode, string LogPath);

    private sealed class CiLocalOptions
    {
        public string Configuration { get; set; } = "Debug";
        public string ConnectionString { get; set; } = "Host=127.0.0.1;Port=5434;Database=hidbridge;Username=hidbridge;Password=hidbridge";
        public string Schema { get; set; } = "hidbridge";
        public string AuthAuthority { get; set; } = "http://host.docker.internal:18096/realms/hidbridge-dev";
        public string KeycloakBaseUrl { get; set; } = "http://host.docker.internal:18096";
        public string TokenClientId { get; set; } = "controlplane-smoke";
        public string TokenClientSecret { get; set; } = string.Empty;
        public string TokenScope { get; set; } = "openid";
        public string TokenUsername { get; set; } = "operator.smoke.admin";
        public string TokenPassword { get; set; } = "ChangeMe123!";
        public string ViewerTokenUsername { get; set; } = "operator.smoke.viewer";
        public string ViewerTokenPassword { get; set; } = "ChangeMe123!";
        public string ForeignTokenUsername { get; set; } = "operator.smoke.foreign";
        public string ForeignTokenPassword { get; set; } = "ChangeMe123!";
        public bool SkipDoctor { get; set; }
        public bool SkipChecks { get; set; }
        public bool SkipBearerSmoke { get; set; }
        public bool IncludeWebRtcEdgeAgentAcceptance { get; set; } = true;
        public bool SkipWebRtcEdgeAgentAcceptance { get; set; }
        public bool IncludeWebRtcMediaE2EGate { get; set; } = true;
        public bool SkipWebRtcMediaE2EGate { get; set; }
        public int WebRtcMediaHealthAttempts { get; set; } = 20;
        public int WebRtcMediaHealthDelayMs { get; set; } = 500;
        public bool IncludeOpsSloSecurityVerify { get; set; } = true;
        public bool SkipOpsSloSecurityVerify { get; set; }
        public int OpsSloWindowMinutes { get; set; } = 60;
        public bool OpsFailOnSloWarning { get; set; }
        public bool OpsFailOnSecurityWarning { get; set; }
        public string WebRtcCommandExecutor { get; set; } = "uart";
        public bool AllowLegacyControlWs { get; set; }
        public string WebRtcControlHealthUrl { get; set; } = "http://127.0.0.1:28092/health";
        public int WebRtcPeerReadyTimeoutSec { get; set; } = 45;
        public bool WebRtcStopExistingBeforeAcceptance { get; set; } = true;
        public bool WebRtcStopStackAfterAcceptance { get; set; } = true;
        public bool StopOnFailure { get; set; }
        public bool ExportArtifactsOnFailure { get; set; } = true;
        public bool IncludeSmokeDataOnFailure { get; set; } = true;
        public bool IncludeBackupsOnFailure { get; set; }

        public static bool TryParse(IReadOnlyList<string> args, out CiLocalOptions options, out string? error)
        {
            options = new CiLocalOptions();
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
                    error = $"Unexpected token '{token}'. Expected PowerShell-style option (e.g. -StopOnFailure).";
                    return false;
                }

                var name = token.TrimStart('-');
                var hasValue = i + 1 < args.Count && !args[i + 1].StartsWith("-", StringComparison.Ordinal);
                string? value = hasValue ? args[i + 1] : null;

                switch (name.ToLowerInvariant())
                {
                    case "configuration": options.Configuration = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "connectionstring": options.ConnectionString = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "schema": options.Schema = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "authauthority": options.AuthAuthority = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "keycloakbaseurl": options.KeycloakBaseUrl = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "tokenclientid": options.TokenClientId = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "tokenclientsecret": options.TokenClientSecret = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "tokenscope": options.TokenScope = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "tokenusername": options.TokenUsername = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "tokenpassword": options.TokenPassword = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "viewertokenusername": options.ViewerTokenUsername = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "viewertokenpassword": options.ViewerTokenPassword = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "foreigntokenusername": options.ForeignTokenUsername = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "foreigntokenpassword": options.ForeignTokenPassword = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "webrtccommandeexecutor": options.WebRtcCommandExecutor = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "webrtccommandexecutor": options.WebRtcCommandExecutor = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "webrtccontrolhealthurl": options.WebRtcControlHealthUrl = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "webrtcpeerreadytimeoutsec": options.WebRtcPeerReadyTimeoutSec = ParseInt(name, value, hasValue, ref i, ref error); break;
                    case "webrtcmediahealthattempts": options.WebRtcMediaHealthAttempts = ParseInt(name, value, hasValue, ref i, ref error); break;
                    case "webrtcmediahealthdelayms": options.WebRtcMediaHealthDelayMs = ParseInt(name, value, hasValue, ref i, ref error); break;
                    case "opsslowindowminutes": options.OpsSloWindowMinutes = ParseInt(name, value, hasValue, ref i, ref error); break;
                    case "skipdoctor": options.SkipDoctor = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "skipchecks": options.SkipChecks = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "skipbearersmoke": options.SkipBearerSmoke = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "includewebrtcedgeagentacceptance": options.IncludeWebRtcEdgeAgentAcceptance = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "skipwebrtcedgeagentacceptance": options.SkipWebRtcEdgeAgentAcceptance = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "includewebrtcmediae2egate": options.IncludeWebRtcMediaE2EGate = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "skipwebrtcmediae2egate": options.SkipWebRtcMediaE2EGate = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "includeopsslosecurityverify": options.IncludeOpsSloSecurityVerify = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "skipopsslosecurityverify": options.SkipOpsSloSecurityVerify = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "opsfailonslowarning": options.OpsFailOnSloWarning = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "opsfailonsecuritywarning": options.OpsFailOnSecurityWarning = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "allowlegacycontrolws": options.AllowLegacyControlWs = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "webrtcstopexistingbeforeacceptance": options.WebRtcStopExistingBeforeAcceptance = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "webrtcstopstackafteracceptance": options.WebRtcStopStackAfterAcceptance = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "stoponfailure": options.StopOnFailure = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "exportartifactsonfailure": options.ExportArtifactsOnFailure = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "includesmokedataonfailure": options.IncludeSmokeDataOnFailure = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "includebackupsonfailure": options.IncludeBackupsOnFailure = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    default:
                        error = $"Unsupported ci-local option '{token}'.";
                        return false;
                }

                if (error is not null)
                {
                    return false;
                }
            }

            if (!string.Equals(options.WebRtcCommandExecutor, "uart", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(options.WebRtcCommandExecutor, "controlws", StringComparison.OrdinalIgnoreCase))
            {
                error = "WebRtcCommandExecutor must be 'uart' or 'controlws'.";
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
