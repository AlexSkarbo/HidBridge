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
/// Implements the <c>FullCommand</c> RuntimeCtl lane.
/// Execution contract:
/// 1) Parse CLI arguments into strongly typed options.
/// 2) Execute lane-specific orchestration (native RuntimeCtl commands first, script bridge only when explicitly needed).
/// 3) Persist step logs/summaries under <c>Platform/.logs</c> and return process-style exit code semantics (0 = success).
/// </summary>
internal static class FullCommand
{
    public static async Task<int> RunAsync(string platformRoot, IReadOnlyList<string> args)
    {
        if (!FullOptions.TryParse(args, out var options, out var parseError))
        {
            Console.Error.WriteLine($"full options error: {parseError}");
            return 1;
        }

        var logRoot = Path.Combine(platformRoot, ".logs", "full", DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(logRoot);

        var results = new List<FullStepResult>();
        string artifactExportPath = string.Empty;

        try
        {
            if (!options.SkipRealmSync)
            {
                var (keycloakBaseUrl, realmName) = ResolveKeycloakFromAuthority(options.AuthAuthority);
                var identityResetArgs = new List<string>
                {
                    "-KeycloakBaseUrl", keycloakBaseUrl,
                    "-RealmName", realmName,
                    "-AllowIdentityProviderLoss", "false",
                };

                await RunRuntimeCtlStepAsync(
                    platformRoot,
                    logRoot,
                    results,
                    options,
                    "Realm Sync",
                    "identity-reset",
                    identityResetArgs);
            }

            if (!options.SkipCiLocal)
            {
                var ciArgs = new List<string>
                {
                    "-Configuration", options.Configuration,
                    "-ConnectionString", options.ConnectionString,
                    "-Schema", options.Schema,
                    "-AuthAuthority", options.AuthAuthority,
                    "-IncludeWebRtcEdgeAgentAcceptance", ToBoolLiteral(options.IncludeWebRtcEdgeAgentAcceptance),
                    "-SkipWebRtcEdgeAgentAcceptance", ToBoolLiteral(options.SkipWebRtcEdgeAgentAcceptance),
                    "-IncludeWebRtcMediaE2EGate", ToBoolLiteral(options.IncludeWebRtcMediaE2EGate),
                    "-SkipWebRtcMediaE2EGate", ToBoolLiteral(options.SkipWebRtcMediaE2EGate),
                    "-WebRtcMediaHealthAttempts", Math.Max(1, options.WebRtcMediaHealthAttempts).ToString(),
                    "-WebRtcMediaHealthDelayMs", Math.Max(100, options.WebRtcMediaHealthDelayMs).ToString(),
                    "-IncludeOpsSloSecurityVerify", ToBoolLiteral(options.IncludeOpsSloSecurityVerify),
                    "-SkipOpsSloSecurityVerify", ToBoolLiteral(options.SkipOpsSloSecurityVerify),
                    "-OpsSloWindowMinutes", Math.Max(5, options.OpsSloWindowMinutes).ToString(),
                    "-OpsFailOnSloWarning", ToBoolLiteral(options.OpsFailOnSloWarning),
                    "-OpsFailOnSecurityWarning", ToBoolLiteral(options.OpsFailOnSecurityWarning),
                    "-WebRtcCommandExecutor", options.WebRtcCommandExecutor,
                    "-AllowLegacyControlWs", ToBoolLiteral(options.AllowLegacyControlWs),
                    "-WebRtcControlHealthUrl", options.WebRtcControlHealthUrl,
                    "-WebRtcPeerReadyTimeoutSec", Math.Max(5, options.WebRtcPeerReadyTimeoutSec).ToString(),
                    "-StopOnFailure", ToBoolLiteral(options.StopOnFailure),
                    "-ExportArtifactsOnFailure", ToBoolLiteral(options.ExportArtifactsOnFailure),
                    "-IncludeSmokeDataOnFailure", ToBoolLiteral(options.IncludeSmokeDataOnFailure),
                    "-IncludeBackupsOnFailure", ToBoolLiteral(options.IncludeBackupsOnFailure),
                };

                await RunRuntimeCtlStepAsync(
                    platformRoot,
                    logRoot,
                    results,
                    options,
                    "CI Local",
                    "ci-local",
                    ciArgs);
            }
        }
        catch (Exception ex)
        {
            var abortPath = Path.Combine(logRoot, "full.abort.log");
            await File.WriteAllTextAsync(abortPath, ex.ToString());
            results.Add(new FullStepResult("Full orchestration", "FAIL", 0, 1, abortPath));
            Console.WriteLine();
            Console.WriteLine($"Full run aborted: {ex.Message}");
        }

        var failed = results.Where(static x => string.Equals(x.Status, "FAIL", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (options.ExportArtifactsOnFailure && failed.Length > 0)
        {
            artifactExportPath = Path.Combine(platformRoot, "Artifacts", $"full-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}");
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
        return failed.Length > 0 ? 1 : 0;
    }

    private static async Task RunRuntimeCtlStepAsync(
        string platformRoot,
        string logRoot,
        List<FullStepResult> results,
        FullOptions options,
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
        results.Add(new FullStepResult(stepName, status, stopwatch.Elapsed.TotalSeconds, process.ExitCode, logPath));

        Console.WriteLine();
        Console.WriteLine($"=== {stepName} ===");
        Console.WriteLine($"{status}  {stepName}");
        Console.WriteLine($"Log:   {logPath}");

        if (process.ExitCode != 0 && options.StopOnFailure)
        {
            throw new InvalidOperationException($"{stepName} failed with exit code {process.ExitCode}");
        }
    }

    private static (string KeycloakBaseUrl, string RealmName) ResolveKeycloakFromAuthority(string authAuthority)
    {
        var authority = string.IsNullOrWhiteSpace(authAuthority)
            ? "http://127.0.0.1:18096/realms/hidbridge-dev"
            : authAuthority;
        var uri = new Uri(authority);

        var keycloakBaseUrl = uri.IsDefaultPort
            ? $"{uri.Scheme}://{uri.Host}"
            : $"{uri.Scheme}://{uri.Host}:{uri.Port}";
        var realmName = "hidbridge-dev";

        if (uri.AbsolutePath.StartsWith("/realms/", StringComparison.OrdinalIgnoreCase))
        {
            var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && !string.IsNullOrWhiteSpace(parts[1]))
            {
                realmName = parts[1];
            }
        }

        return (keycloakBaseUrl, realmName);
    }

    private static string ToBoolLiteral(bool value) => value ? "true" : "false";

    private static void PrintSummary(IReadOnlyList<FullStepResult> results, string logRoot, string artifactExportPath)
    {
        Console.WriteLine();
        Console.WriteLine("=== Full Run Summary ===");
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

    private sealed record FullStepResult(string Name, string Status, double Seconds, int ExitCode, string LogPath);

    private sealed class FullOptions
    {
        public string Configuration { get; set; } = "Debug";
        public string ConnectionString { get; set; } = "Host=127.0.0.1;Port=5434;Database=hidbridge;Username=hidbridge;Password=hidbridge";
        public string Schema { get; set; } = "hidbridge";
        public string AuthAuthority { get; set; } = "http://127.0.0.1:18096/realms/hidbridge-dev";
        public bool StopOnFailure { get; set; }
        public bool SkipRealmSync { get; set; }
        public bool SkipCiLocal { get; set; }
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
        public bool ExportArtifactsOnFailure { get; set; } = true;
        public bool IncludeSmokeDataOnFailure { get; set; } = true;
        public bool IncludeBackupsOnFailure { get; set; } = true;

        public static bool TryParse(IReadOnlyList<string> args, out FullOptions options, out string? error)
        {
            options = new FullOptions();
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
                    case "stoponfailure": options.StopOnFailure = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "skiprealmsync": options.SkipRealmSync = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "skipcilocal": options.SkipCiLocal = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "includewebrtcedgeagentacceptance": options.IncludeWebRtcEdgeAgentAcceptance = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "skipwebrtcedgeagentacceptance": options.SkipWebRtcEdgeAgentAcceptance = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "includewebrtcmediae2egate": options.IncludeWebRtcMediaE2EGate = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "skipwebrtcmediae2egate": options.SkipWebRtcMediaE2EGate = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "webrtcmediahealthattempts": options.WebRtcMediaHealthAttempts = ParseInt(name, value, hasValue, ref i, ref error); break;
                    case "webrtcmediahealthdelayms": options.WebRtcMediaHealthDelayMs = ParseInt(name, value, hasValue, ref i, ref error); break;
                    case "includeopsslosecurityverify": options.IncludeOpsSloSecurityVerify = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "skipopsslosecurityverify": options.SkipOpsSloSecurityVerify = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "opsslowindowminutes": options.OpsSloWindowMinutes = ParseInt(name, value, hasValue, ref i, ref error); break;
                    case "opsfailonslowarning": options.OpsFailOnSloWarning = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "opsfailonsecuritywarning": options.OpsFailOnSecurityWarning = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "webrtccommandexecutor": options.WebRtcCommandExecutor = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "allowlegacycontrolws": options.AllowLegacyControlWs = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "webrtccontrolhealthurl": options.WebRtcControlHealthUrl = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "webrtcpeerreadytimeoutsec": options.WebRtcPeerReadyTimeoutSec = ParseInt(name, value, hasValue, ref i, ref error); break;
                    case "exportartifactsonfailure": options.ExportArtifactsOnFailure = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "includesmokedataonfailure": options.IncludeSmokeDataOnFailure = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "includebackupsonfailure": options.IncludeBackupsOnFailure = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    default:
                        error = $"Unsupported full option '{token}'.";
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
