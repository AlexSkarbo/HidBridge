using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text;

var app = RuntimeCtlApp.Parse(args);
return await app.RunAsync();

internal sealed class RuntimeCtlApp
{
    private static readonly IReadOnlyDictionary<string, CommandSpec> CommandAliases =
        new Dictionary<string, CommandSpec>(StringComparer.OrdinalIgnoreCase)
        {
            ["doctor"] = new("doctor", "Runs environment and API health checks.", CommandKind.ScriptBridge, "run_doctor.ps1"),
            ["ci-local"] = new("ci-local", "Runs local CI gate lane (native C# orchestration).", CommandKind.CiLocal, null),
            ["full"] = new("full", "Runs full local gate lane (native C# orchestration).", CommandKind.Full, null),
            ["webrtc-stack"] = new("webrtc-stack", "Bootstraps WebRTC stack and edge adapter.", CommandKind.ScriptBridge, "run_webrtc_stack.ps1"),
            ["webrtc-acceptance"] = new("webrtc-acceptance", "Runs WebRTC edge-agent acceptance (native C# orchestration).", CommandKind.WebRtcAcceptance, null),
            ["ops-verify"] = new("ops-verify", "Runs ops SLO/security verification (native C# orchestration).", CommandKind.OpsVerify, null),
            ["identity-reset"] = new("identity-reset", "Resets Keycloak realm and smoke principals.", CommandKind.ScriptBridge, "run_identity_reset.ps1"),
            ["platform-runtime"] = new("platform-runtime", "Controls docker runtime profile.", CommandKind.ScriptBridge, "Scripts/run_platform_runtime_profile.ps1"),
        };

    private static readonly IReadOnlyDictionary<string, string> TaskMap =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["checks"] = "run_checks.ps1",
            ["tests"] = "run_all_tests.ps1",
            ["smoke"] = "run_smoke.ps1",
            ["smoke-file"] = "run_file_smoke.ps1",
            ["smoke-sql"] = "run_sql_smoke.ps1",
            ["smoke-bearer"] = "run_api_bearer_smoke.ps1",
            ["doctor"] = "run_doctor.ps1",
            ["clean-logs"] = "run_clean_logs.ps1",
            ["ci-local"] = "run_ci_local.ps1",
            ["full"] = "run_full.ps1",
            ["export-artifacts"] = "run_export_artifacts.ps1",
            ["token-debug"] = "run_token_debug.ps1",
            ["bearer-rollout"] = "run_bearer_rollout_phase.ps1",
            ["identity-reset"] = "run_identity_reset.ps1",
            ["identity-onboard"] = "run_identity_onboard.ps1",
            ["demo-flow"] = "run_demo_flow.ps1",
            ["demo-seed"] = "run_demo_seed.ps1",
            ["demo-gate"] = "run_demo_gate.ps1",
            ["uart-diagnostics"] = "run_uart_diagnostics.ps1",
            ["webrtc-relay-smoke"] = "run_webrtc_relay_smoke.ps1",
            ["webrtc-peer-adapter"] = "run_webrtc_peer_adapter.ps1",
            ["webrtc-stack"] = "run_webrtc_stack.ps1",
            ["webrtc-stack-terminal-b"] = "run_webrtc_stack_terminal_b.ps1",
            ["webrtc-edge-agent-smoke"] = "run_webrtc_edge_agent_smoke.ps1",
            ["webrtc-edge-agent-acceptance"] = "run_webrtc_edge_agent_acceptance.ps1",
            ["ops-slo-security-verify"] = "run_ops_slo_security_verify.ps1",
            ["close-failed-rooms"] = "run_close_failed_rooms.ps1",
            ["close-stale-rooms"] = "run_close_stale_rooms.ps1",
            ["platform-runtime"] = "Scripts/run_platform_runtime_profile.ps1",
        };

    private readonly string _platformRoot;
    private readonly CommandSpec? _command;
    private readonly IReadOnlyList<string> _forwardArgs;
    private readonly bool _showHelp;
    private readonly string? _error;

    private RuntimeCtlApp(
        string platformRoot,
        CommandSpec? command,
        IReadOnlyList<string> forwardArgs,
        bool showHelp,
        string? error)
    {
        _platformRoot = platformRoot;
        _command = command;
        _forwardArgs = forwardArgs;
        _showHelp = showHelp;
        _error = error;
    }

    public static RuntimeCtlApp Parse(string[] args)
    {
        var remaining = new List<string>(args);
        string? explicitPlatformRoot = null;

        while (remaining.Count > 0)
        {
            if (!string.Equals(remaining[0], "--platform-root", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            if (remaining.Count < 2 || string.IsNullOrWhiteSpace(remaining[1]))
            {
                return new RuntimeCtlApp(
                    platformRoot: string.Empty,
                    command: null,
                    forwardArgs: Array.Empty<string>(),
                    showHelp: true,
                    error: "--platform-root requires a non-empty path value.");
            }

            explicitPlatformRoot = remaining[1];
            remaining.RemoveRange(0, 2);
        }

        var platformRoot = ResolvePlatformRoot(explicitPlatformRoot);
        if (string.IsNullOrWhiteSpace(platformRoot))
        {
            return new RuntimeCtlApp(
                platformRoot: string.Empty,
                command: null,
                forwardArgs: Array.Empty<string>(),
                showHelp: true,
                error: "Unable to resolve Platform root. Pass --platform-root <path-to-Platform>.\n");
        }

        if (remaining.Count == 0
            || string.Equals(remaining[0], "-h", StringComparison.OrdinalIgnoreCase)
            || string.Equals(remaining[0], "--help", StringComparison.OrdinalIgnoreCase)
            || string.Equals(remaining[0], "help", StringComparison.OrdinalIgnoreCase))
        {
            return new RuntimeCtlApp(platformRoot, null, Array.Empty<string>(), showHelp: true, error: null);
        }

        var commandToken = remaining[0];
        remaining.RemoveAt(0);

        if (string.Equals(commandToken, "task", StringComparison.OrdinalIgnoreCase))
        {
            if (remaining.Count == 0)
            {
                return new RuntimeCtlApp(platformRoot, null, Array.Empty<string>(), showHelp: true, error: "task requires <task-name>.");
            }

            var taskName = remaining[0];
            remaining.RemoveAt(0);
            if (!TaskMap.TryGetValue(taskName, out var taskScript))
            {
                return new RuntimeCtlApp(platformRoot, null, Array.Empty<string>(), showHelp: true, error: $"Unsupported task '{taskName}'.");
            }

            var taskSpec = new CommandSpec($"task {taskName}", "run.ps1-compatible task invocation.", CommandKind.ScriptBridge, taskScript);
            return new RuntimeCtlApp(platformRoot, taskSpec, NormalizeForwardArgs(remaining), showHelp: false, error: null);
        }

        if (!CommandAliases.TryGetValue(commandToken, out var commandSpec))
        {
            return new RuntimeCtlApp(platformRoot, null, Array.Empty<string>(), showHelp: true, error: $"Unsupported command '{commandToken}'.");
        }

        return new RuntimeCtlApp(platformRoot, commandSpec, NormalizeForwardArgs(remaining), showHelp: false, error: null);
    }

    public async Task<int> RunAsync()
    {
        if (_showHelp || _command is null)
        {
            PrintHelp(_error);
            return string.IsNullOrWhiteSpace(_error) ? 0 : 1;
        }

        return _command.Kind switch
        {
            CommandKind.CiLocal => await CiLocalCommand.RunAsync(_platformRoot, _forwardArgs),
            CommandKind.Full => await FullCommand.RunAsync(_platformRoot, _forwardArgs),
            CommandKind.WebRtcAcceptance => await WebRtcAcceptanceCommand.RunAsync(_platformRoot, _forwardArgs),
            CommandKind.OpsVerify => await OpsVerifyCommand.RunAsync(_platformRoot, _forwardArgs),
            CommandKind.ScriptBridge => await RunScriptBridgeAsync(_platformRoot, _command.ScriptRelativePath!, _forwardArgs),
            _ => throw new InvalidOperationException($"Unsupported command kind '{_command.Kind}'."),
        };
    }

    private static IReadOnlyList<string> NormalizeForwardArgs(List<string> args)
    {
        if (args.Count > 0 && string.Equals(args[0], "--", StringComparison.Ordinal))
        {
            args.RemoveAt(0);
        }

        return args;
    }

    internal static async Task<int> RunScriptBridgeAsync(string platformRoot, string scriptRelativePath, IReadOnlyList<string> forwardArgs)
    {
        var scriptPath = Path.Combine(platformRoot, scriptRelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(scriptPath))
        {
            Console.Error.WriteLine($"Script not found: {scriptPath}");
            return 1;
        }

        var startInfo = CreatePowerShellStartInfo(scriptPath, forwardArgs, platformRoot, redirectOutput: false);
        using var process = Process.Start(startInfo);
        if (process is null)
        {
            Console.Error.WriteLine("Failed to start command process.");
            return 1;
        }

        await process.WaitForExitAsync();
        return process.ExitCode;
    }

    internal static async Task<ScriptInvocationResult> RunScriptToLogAsync(
        string platformRoot,
        string scriptRelativePath,
        IReadOnlyList<string> forwardArgs,
        string logPath)
    {
        var scriptPath = Path.Combine(platformRoot, scriptRelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(scriptPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logPath) ?? platformRoot);
            await File.WriteAllTextAsync(logPath, $"Script not found: {scriptPath}{Environment.NewLine}");
            return new ScriptInvocationResult(1, string.Empty, $"Script not found: {scriptPath}");
        }

        var startInfo = CreatePowerShellStartInfo(scriptPath, forwardArgs, platformRoot, redirectOutput: true);
        using var process = Process.Start(startInfo);
        if (process is null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logPath) ?? platformRoot);
            await File.WriteAllTextAsync(logPath, "Failed to start command process." + Environment.NewLine);
            return new ScriptInvocationResult(1, string.Empty, "Failed to start command process.");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        Directory.CreateDirectory(Path.GetDirectoryName(logPath) ?? platformRoot);
        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(stdout))
        {
            builder.AppendLine(stdout.TrimEnd());
        }

        if (!string.IsNullOrWhiteSpace(stderr))
        {
            if (builder.Length > 0)
            {
                builder.AppendLine();
            }

            builder.AppendLine(stderr.TrimEnd());
        }

        await File.WriteAllTextAsync(logPath, builder.ToString());
        return new ScriptInvocationResult(process.ExitCode, stdout, stderr);
    }

    private static ProcessStartInfo CreatePowerShellStartInfo(
        string scriptPath,
        IReadOnlyList<string> forwardArgs,
        string workingDirectory,
        bool redirectOutput)
    {
        var (shell, shellPrefixArgs) = BuildShellInvocation(scriptPath);
        var startInfo = new ProcessStartInfo
        {
            FileName = shell,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = redirectOutput,
            RedirectStandardError = redirectOutput,
        };

        foreach (var argument in shellPrefixArgs)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var argument in forwardArgs)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private static (string Shell, IReadOnlyList<string> PrefixArgs) BuildShellInvocation(string scriptPath)
    {
        if (OperatingSystem.IsWindows())
        {
            var shell = ResolveFirstExistingCommand(["powershell.exe", "pwsh.exe"])
                ?? throw new InvalidOperationException("Unable to locate powershell.exe or pwsh.exe in PATH.");

            return (shell, ["-ExecutionPolicy", "Bypass", "-File", scriptPath]);
        }

        var unixShell = ResolveFirstExistingCommand(["pwsh"])
            ?? throw new InvalidOperationException("Unable to locate pwsh in PATH.");

        return (unixShell, ["-NoProfile", "-File", scriptPath]);
    }

    private static string? ResolveFirstExistingCommand(IReadOnlyList<string> commandNames)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var pathEntries = path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var commandName in commandNames)
        {
            if (Path.IsPathRooted(commandName) && File.Exists(commandName))
            {
                return commandName;
            }

            foreach (var directory in pathEntries)
            {
                var candidate = Path.Combine(directory, commandName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static string ResolvePlatformRoot(string? explicitPlatformRoot)
    {
        if (!string.IsNullOrWhiteSpace(explicitPlatformRoot))
        {
            var absolute = Path.GetFullPath(explicitPlatformRoot);
            return Directory.Exists(absolute) ? absolute : string.Empty;
        }

        var cwd = Directory.GetCurrentDirectory();
        var probe = new DirectoryInfo(cwd);
        while (probe is not null)
        {
            var directPlatform = Path.Combine(probe.FullName, "Platform");
            if (File.Exists(Path.Combine(directPlatform, "run.ps1")))
            {
                return directPlatform;
            }

            if (File.Exists(Path.Combine(probe.FullName, "run.ps1"))
                && string.Equals(probe.Name, "Platform", StringComparison.OrdinalIgnoreCase))
            {
                return probe.FullName;
            }

            probe = probe.Parent;
        }

        return string.Empty;
    }

    private static void PrintHelp(string? error)
    {
        if (!string.IsNullOrWhiteSpace(error))
        {
            Console.Error.WriteLine($"error: {error}\n");
        }

        Console.WriteLine("HidBridge.RuntimeCtl - CLI-first entrypoint (PowerShell compatibility bridge)\n");
        Console.WriteLine("Usage:");
        Console.WriteLine("  runtimectl [--platform-root <path>] <command> [-- <command-args...>]");
        Console.WriteLine("  runtimectl [--platform-root <path>] task <run.ps1-task> [-- <task-args...>]\n");

        Console.WriteLine("Commands:");
        foreach (var command in CommandAliases.Values.OrderBy(static x => x.Name, StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine($"  {command.Name,-18} {command.Description}");
        }

        Console.WriteLine("\nTask aliases:");
        foreach (var task in TaskMap.Keys.OrderBy(static x => x, StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine($"  task {task}");
        }
    }

    private sealed record CommandSpec(
        string Name,
        string Description,
        CommandKind Kind,
        string? ScriptRelativePath);

    private enum CommandKind
    {
        ScriptBridge,
        CiLocal,
        Full,
        WebRtcAcceptance,
        OpsVerify,
    }

    internal sealed record ScriptInvocationResult(int ExitCode, string StdOut, string StdErr);
}

internal static class CiLocalCommand
{
    public static async Task<int> RunAsync(string platformRoot, IReadOnlyList<string> args)
    {
        if (!CiLocalOptions.TryParse(args, out var options, out var parseError))
        {
            Console.Error.WriteLine($"ci-local options error: {parseError}");
            return 1;
        }

        var scriptsRoot = Path.Combine(platformRoot, "Scripts");
        var logRoot = Path.Combine(platformRoot, ".logs", "ci-local", DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(logRoot);

        var results = new List<CiStepResult>();
        string artifactExportPath = string.Empty;

        try
        {
            if (!options.SkipDoctor)
            {
                await RunStepAsync(
                    platformRoot,
                    scriptsRoot,
                    logRoot,
                    results,
                    options,
                    "Doctor",
                    "run_doctor.ps1",
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

                await RunStepAsync(
                    platformRoot,
                    scriptsRoot,
                    logRoot,
                    results,
                    options,
                    "Checks (Sql)",
                    "run_checks.ps1",
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

                await RunStepAsync(
                    platformRoot,
                    scriptsRoot,
                    logRoot,
                    results,
                    options,
                    "Bearer Smoke",
                    "run_api_bearer_smoke.ps1",
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

                await RunStepAsync(
                    platformRoot,
                    scriptsRoot,
                    logRoot,
                    results,
                    options,
                    "WebRTC Edge Agent Acceptance",
                    "run_webrtc_edge_agent_acceptance.ps1",
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

                await RunStepAsync(
                    platformRoot,
                    scriptsRoot,
                    logRoot,
                    results,
                    options,
                    "Ops SLO + Security Verify",
                    "run_ops_slo_security_verify.ps1",
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
            var exportArgs = new List<string>
            {
                "-OutputRoot", artifactExportPath,
            };

            if (options.IncludeSmokeDataOnFailure)
            {
                exportArgs.Add("-IncludeSmokeData");
            }

            if (options.IncludeBackupsOnFailure)
            {
                exportArgs.Add("-IncludeBackups");
            }

            try
            {
                await RuntimeCtlApp.RunScriptToLogAsync(platformRoot, Path.Combine("Scripts", "run_export_artifacts.ps1"), exportArgs, Path.Combine(logRoot, "export-artifacts.log"));
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

    private static async Task RunStepAsync(
        string platformRoot,
        string scriptsRoot,
        string logRoot,
        List<CiStepResult> results,
        CiLocalOptions options,
        string stepName,
        string scriptFile,
        IReadOnlyList<string> scriptArgs)
    {
        var logPath = Path.Combine(logRoot, stepName.Replace(' ', '-').Replace("(", string.Empty).Replace(")", string.Empty) + ".log");

        var stopwatch = Stopwatch.StartNew();
        var invocation = await RuntimeCtlApp.RunScriptToLogAsync(platformRoot, Path.Combine("Scripts", scriptFile), scriptArgs, logPath);
        stopwatch.Stop();

        var status = invocation.ExitCode == 0 ? "PASS" : "FAIL";
        results.Add(new CiStepResult(stepName, status, stopwatch.Elapsed.TotalSeconds, invocation.ExitCode, logPath));

        Console.WriteLine();
        Console.WriteLine($"=== {stepName} ===");
        Console.WriteLine($"{status}  {stepName}");
        Console.WriteLine($"Log:   {logPath}");

        if (invocation.ExitCode != 0 && options.StopOnFailure)
        {
            throw new InvalidOperationException($"{stepName} failed with exit code {invocation.ExitCode}");
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

internal static class OpsVerifyCommand
{
    public static async Task<int> RunAsync(string platformRoot, IReadOnlyList<string> args)
    {
        if (!OpsVerifyOptions.TryParse(args, out var options, out var parseError))
        {
            Console.Error.WriteLine($"ops-verify options error: {parseError}");
            return 1;
        }

        if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out _))
        {
            Console.Error.WriteLine("ops-verify options error: BaseUrl must be an absolute URL.");
            return 1;
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var checks = new List<OpsCheck>();
        var tokenState = TokenState.Initialize(options.AuthAuthority, options.AccessToken);

        JsonDocument? runtime = null;
        JsonDocument? slo = null;
        JsonDocument? audit = null;
        var auditEventCount = 0;

        try
        {
            using var healthClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var healthUri = $"{options.BaseUrl.TrimEnd('/')}/health";
            using var healthResponse = await healthClient.GetAsync(healthUri);
            var healthJson = await ReadJsonOrThrowAsync(healthResponse);
            var healthStatus = ReadString(healthJson.RootElement, "status") ?? string.Empty;
            var isHealthy = string.Equals(healthStatus, "ok", StringComparison.OrdinalIgnoreCase);
            AddCheck(checks, "api.health", isHealthy ? "PASS" : "FAIL",
                isHealthy ? "API health endpoint is reachable." : "API health endpoint returned non-ok status.",
                healthStatus, "ok");

            var windowMinutes = Clamp(options.SloWindowMinutes, 5, 24 * 60);
            runtime = await InvokeApiJsonAsync(http, options, tokenState, "/api/v1/runtime/uart");
            slo = await InvokeApiJsonAsync(http, options, tokenState, $"/api/v1/diagnostics/transport/slo?windowMinutes={windowMinutes}");
            audit = await InvokeApiJsonAsync(http, options, tokenState, "/api/v1/events/audit");

            EvaluateSecurityChecks(checks, runtime.RootElement, options);
            EvaluateSloChecks(checks, slo.RootElement, options);

            auditEventCount = audit.RootElement.ValueKind == JsonValueKind.Array ? audit.RootElement.GetArrayLength() : 0;
            EvaluateAuditChecks(checks, audit.RootElement, options);
        }
        catch (Exception ex)
        {
            AddCheck(checks, "ops.verify.exception", "FAIL", ex.Message, null, null);
        }

        var failedCount = checks.Count(static c => string.Equals(c.Status, "FAIL", StringComparison.OrdinalIgnoreCase));
        var warnCount = checks.Count(static c => string.Equals(c.Status, "WARN", StringComparison.OrdinalIgnoreCase));
        var passCount = checks.Count(static c => string.Equals(c.Status, "PASS", StringComparison.OrdinalIgnoreCase));
        var resultStatus = failedCount > 0 ? "FAIL" : warnCount > 0 ? "WARN" : "PASS";

        var summary = new
        {
            generatedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
            baseUrl = options.BaseUrl,
            status = resultStatus,
            checkCounts = new
            {
                pass = passCount,
                warn = warnCount,
                fail = failedCount,
            },
            slo = new
            {
                status = slo is null ? null : ReadString(slo.RootElement, "status"),
                relayCommandCount = slo is null ? null : ReadScalar(slo.RootElement, "relayCommandCount"),
                relayTimeoutCount = slo is null ? null : ReadScalar(slo.RootElement, "relayTimeoutCount"),
                ackTimeoutRate = slo is null ? null : ReadScalar(slo.RootElement, "ackTimeoutRate"),
                relayReadyLatencyP95Ms = slo is null ? null : ReadScalar(slo.RootElement, "relayReadyLatencyP95Ms"),
                reconnectFrequencyPerHour = slo is null ? null : ReadScalar(slo.RootElement, "reconnectFrequencyPerHour"),
            },
            security = new
            {
                authEnabled = runtime is null ? null : ReadScalar(runtime.RootElement, "authentication", "enabled"),
                allowHeaderFallback = runtime is null ? null : ReadScalar(runtime.RootElement, "authentication", "allowHeaderFallback"),
                bearerOnlyPrefixes = runtime is null ? Array.Empty<string>() : ReadStringArray(runtime.RootElement, "authentication", "bearerOnlyPrefixes"),
                callerContextRequiredPrefixes = runtime is null ? Array.Empty<string>() : ReadStringArray(runtime.RootElement, "authentication", "callerContextRequiredPrefixes"),
                headerFallbackDisabledPatterns = runtime is null ? Array.Empty<string>() : ReadStringArray(runtime.RootElement, "authentication", "headerFallbackDisabledPatterns"),
                auditEventCount,
            },
            checks,
        };

        if (!string.IsNullOrWhiteSpace(options.OutputJsonPath))
        {
            var outputPath = Path.IsPathRooted(options.OutputJsonPath)
                ? options.OutputJsonPath
                : Path.Combine(platformRoot, options.OutputJsonPath);
            var parent = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrWhiteSpace(parent))
            {
                Directory.CreateDirectory(parent);
            }

            await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
        }

        PrintChecksTable(options.BaseUrl, resultStatus, options.OutputJsonPath, checks);
        return failedCount > 0 ? 1 : 0;
    }

    private static void EvaluateSecurityChecks(List<OpsCheck> checks, JsonElement runtimeRoot, OpsVerifyOptions options)
    {
        var authEnabled = ReadBoolean(runtimeRoot, "authentication", "enabled");
        if (authEnabled)
        {
            AddCheck(checks, "security.auth.enabled", "PASS", "API authentication is enabled.", authEnabled, true);
        }
        else if (options.AllowAuthDisabled)
        {
            AddCheck(checks, "security.auth.enabled", "WARN", "API authentication is disabled by runtime configuration.", authEnabled, true);
        }
        else
        {
            AddCheck(checks, "security.auth.enabled", "FAIL", "API authentication is disabled and AllowAuthDisabled was not provided.", authEnabled, true);
        }

        var headerFallback = ReadBoolean(runtimeRoot, "authentication", "allowHeaderFallback");
        if (!headerFallback)
        {
            AddCheck(checks, "security.header-fallback", "PASS", "Header fallback is disabled for API auth.", headerFallback, false);
        }
        else
        {
            AddCheck(checks, "security.header-fallback", options.FailOnSecurityWarning ? "FAIL" : "WARN", "Header fallback is enabled; strict bearer posture is not fully enforced.", headerFallback, false);
        }

        var bearerPrefixes = ReadStringArray(runtimeRoot, "authentication", "bearerOnlyPrefixes");
        var requiredBearerPrefixes = new[] { "/api/v1/sessions", "/api/v1/diagnostics" };
        var missingBearer = requiredBearerPrefixes.Where(p => !bearerPrefixes.Contains(p, StringComparer.Ordinal)).ToArray();
        if (missingBearer.Length == 0)
        {
            AddCheck(checks, "security.bearer-only-coverage", "PASS", "Bearer-only coverage includes required prefixes.", string.Join(", ", bearerPrefixes), string.Join(", ", requiredBearerPrefixes));
        }
        else
        {
            AddCheck(checks, "security.bearer-only-coverage", options.FailOnSecurityWarning ? "FAIL" : "WARN", $"Missing bearer-only prefixes: {string.Join(", ", missingBearer)}.", string.Join(", ", bearerPrefixes), string.Join(", ", requiredBearerPrefixes));
        }

        var callerPrefixes = ReadStringArray(runtimeRoot, "authentication", "callerContextRequiredPrefixes");
        var hasSessionCallerContext = callerPrefixes.Contains("/api/v1/sessions", StringComparer.Ordinal);
        if (hasSessionCallerContext)
        {
            AddCheck(checks, "security.caller-context-coverage", "PASS", "Caller-context coverage includes /api/v1/sessions.", string.Join(", ", callerPrefixes), "/api/v1/sessions");
        }
        else
        {
            AddCheck(checks, "security.caller-context-coverage", options.FailOnSecurityWarning ? "FAIL" : "WARN", "Caller-context coverage does not include /api/v1/sessions.", string.Join(", ", callerPrefixes), "/api/v1/sessions");
        }

        var fallbackPatterns = ReadStringArray(runtimeRoot, "authentication", "headerFallbackDisabledPatterns");
        var hasCommands = fallbackPatterns.Any(static p => p.Contains("commands", StringComparison.OrdinalIgnoreCase));
        var hasControl = fallbackPatterns.Any(static p => p.Contains("control", StringComparison.OrdinalIgnoreCase));
        var hasInvitations = fallbackPatterns.Any(static p => p.Contains("invitations", StringComparison.OrdinalIgnoreCase));
        if (hasCommands && hasControl && hasInvitations)
        {
            AddCheck(checks, "security.fallback-disabled-patterns", "PASS", "Header-fallback disabled patterns cover commands/control/invitations mutation surfaces.", string.Join(", ", fallbackPatterns), "contains commands + control + invitations patterns");
        }
        else
        {
            var missing = new List<string>();
            if (!hasCommands) missing.Add("commands");
            if (!hasControl) missing.Add("control");
            if (!hasInvitations) missing.Add("invitations");
            AddCheck(checks, "security.fallback-disabled-patterns", options.FailOnSecurityWarning ? "FAIL" : "WARN", $"Missing fallback-disabled pattern coverage: {string.Join(", ", missing)}.", string.Join(", ", fallbackPatterns), "contains commands + control + invitations patterns");
        }
    }

    private static void EvaluateSloChecks(List<OpsCheck> checks, JsonElement sloRoot, OpsVerifyOptions options)
    {
        var sloStatus = ReadString(sloRoot, "status") ?? string.Empty;
        if (string.Equals(sloStatus, "critical", StringComparison.OrdinalIgnoreCase))
        {
            AddCheck(checks, "slo.overall-status", "FAIL", "Transport SLO status is critical.", sloStatus, "healthy|warning");
        }
        else if (string.Equals(sloStatus, "warning", StringComparison.OrdinalIgnoreCase))
        {
            AddCheck(checks, "slo.overall-status", options.FailOnSloWarning ? "FAIL" : "WARN", "Transport SLO status is warning.", sloStatus, "healthy");
        }
        else
        {
            AddCheck(checks, "slo.overall-status", "PASS", "Transport SLO status is healthy.", sloStatus, "healthy");
        }

        EvaluateThresholdCheck(checks, "slo.ack-timeout-rate", "ACK timeout rate", options,
            ReadBoolean(sloRoot, "breaches", "ackTimeoutRateCritical"),
            ReadBoolean(sloRoot, "breaches", "ackTimeoutRateWarn"),
            ReadScalar(sloRoot, "ackTimeoutRate"),
            ReadScalar(sloRoot, "thresholds", "ackTimeoutRateCritical"),
            ReadScalar(sloRoot, "thresholds", "ackTimeoutRateWarn"));

        EvaluateThresholdCheck(checks, "slo.relay-ready-latency-p95", "Relay-ready p95 latency", options,
            ReadBoolean(sloRoot, "breaches", "relayReadyLatencyP95Critical"),
            ReadBoolean(sloRoot, "breaches", "relayReadyLatencyP95Warn"),
            ReadScalar(sloRoot, "relayReadyLatencyP95Ms"),
            ReadScalar(sloRoot, "thresholds", "relayReadyLatencyCriticalMs"),
            ReadScalar(sloRoot, "thresholds", "relayReadyLatencyWarnMs"));

        EvaluateThresholdCheck(checks, "slo.reconnect-frequency", "Reconnect frequency", options,
            ReadBoolean(sloRoot, "breaches", "reconnectFrequencyCriticalPerHour"),
            ReadBoolean(sloRoot, "breaches", "reconnectFrequencyWarnPerHour"),
            ReadScalar(sloRoot, "reconnectFrequencyPerHour"),
            ReadScalar(sloRoot, "thresholds", "reconnectFrequencyCriticalPerHour"),
            ReadScalar(sloRoot, "thresholds", "reconnectFrequencyWarnPerHour"));
    }

    private static void EvaluateThresholdCheck(
        List<OpsCheck> checks,
        string checkName,
        string metricTitle,
        OpsVerifyOptions options,
        bool isCritical,
        bool isWarning,
        object? observedValue,
        object? criticalThreshold,
        object? warningThreshold)
    {
        if (isCritical)
        {
            AddCheck(checks, checkName, "FAIL", $"{metricTitle} crossed critical threshold.", observedValue, criticalThreshold is null ? "< critical" : $"< {criticalThreshold}");
            return;
        }

        if (isWarning)
        {
            AddCheck(checks, checkName, options.FailOnSloWarning ? "FAIL" : "WARN", $"{metricTitle} crossed warning threshold.", observedValue, warningThreshold is null ? "< warning" : $"< {warningThreshold}");
            return;
        }

        AddCheck(checks, checkName, "PASS", $"{metricTitle} is within threshold.", observedValue, warningThreshold is null ? "< warning" : $"< {warningThreshold}");
    }

    private static void EvaluateAuditChecks(List<OpsCheck> checks, JsonElement auditRoot, OpsVerifyOptions options)
    {
        if (auditRoot.ValueKind != JsonValueKind.Array || auditRoot.GetArrayLength() == 0)
        {
            var status = options.RequireAuditTrailCategories || options.FailOnSecurityWarning ? "FAIL" : "WARN";
            AddCheck(checks, "security.audit-trail-presence", status, "Audit event stream is empty.", 0, "> 0");
            return;
        }

        var sampleTake = Clamp(options.AuditSampleTake, 1, 2000);
        var events = auditRoot.EnumerateArray().Select(static e => e.Clone()).ToList();
        var sampled = events
            .OrderByDescending(e => ParseCreatedAtUtc(e))
            .Take(sampleTake)
            .ToArray();

        var categories = sampled
            .Select(static e => ReadString(e, "category"))
            .Where(static c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.Ordinal)
            .Select(static c => c!)
            .ToArray()!;

        var hasCommand = categories.Contains("session.command", StringComparer.Ordinal);
        var hasAck = categories.Contains("transport.ack", StringComparer.Ordinal);
        var hasLease = categories.Any(static c => c.StartsWith("session.control", StringComparison.Ordinal));

        if (hasCommand && hasAck && hasLease)
        {
            AddCheck(checks, "security.audit-trail-categories", "PASS", "Audit stream contains command/lease/ack categories.", string.Join(", ", categories), "session.command + session.control* + transport.ack");
            return;
        }

        var missing = new List<string>();
        if (!hasCommand) missing.Add("session.command");
        if (!hasLease) missing.Add("session.control*");
        if (!hasAck) missing.Add("transport.ack");
        var statusFail = options.RequireAuditTrailCategories || options.FailOnSecurityWarning ? "FAIL" : "WARN";
        AddCheck(checks, "security.audit-trail-categories", statusFail, $"Audit stream is missing categories: {string.Join(", ", missing)}.", string.Join(", ", categories), "session.command + session.control* + transport.ack");
    }

    private static DateTimeOffset ParseCreatedAtUtc(JsonElement element)
    {
        var raw = ReadString(element, "createdAtUtc");
        return DateTimeOffset.TryParse(raw, out var parsed) ? parsed : DateTimeOffset.MinValue;
    }

    private static async Task<JsonDocument> InvokeApiJsonAsync(HttpClient client, OpsVerifyOptions options, TokenState tokenState, string relativePath)
    {
        var uri = $"{options.BaseUrl.TrimEnd('/')}{relativePath}";
        var tokenAttempted = false;

        while (true)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (!string.IsNullOrWhiteSpace(tokenState.ResolvedAccessToken))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenState.ResolvedAccessToken);
            }

            using var response = await client.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                return await ReadJsonOrThrowAsync(response);
            }

            var body = await SafeReadBodyAsync(response);
            var isUnauthorized = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
            if (!isUnauthorized || tokenAttempted)
            {
                if (tokenAttempted && tokenState.MoveNextAuthority())
                {
                    tokenState.ResolvedAccessToken = null;
                    tokenAttempted = false;
                    continue;
                }

                throw new HttpRequestException(
                    $"Request to {uri} failed with status {(int)response.StatusCode} ({response.StatusCode}).{(string.IsNullOrWhiteSpace(body) ? string.Empty : $" {body}")}");
            }

            tokenState.ResolvedAccessToken = await GetResolvedAccessTokenAsync(client, options, tokenState);
            tokenAttempted = true;
        }
    }

    private static async Task<string> GetResolvedAccessTokenAsync(HttpClient client, OpsVerifyOptions options, TokenState tokenState)
    {
        if (!string.IsNullOrWhiteSpace(tokenState.ResolvedAccessToken))
        {
            return tokenState.ResolvedAccessToken;
        }

        if (string.Equals(options.AccessToken, "none", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Access token is required for this endpoint, but AccessToken=none disables automatic token acquisition.");
        }

        if (!string.IsNullOrWhiteSpace(options.AccessToken)
            && !string.Equals(options.AccessToken, "auto", StringComparison.OrdinalIgnoreCase))
        {
            tokenState.ResolvedAccessToken = options.AccessToken;
            return tokenState.ResolvedAccessToken;
        }

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

        string lastError = "Keycloak token request failed.";
        do
        {
            var authority = tokenState.GetCurrentAuthority();
            var tokenEndpoint = $"{authority}/protocol/openid-connect/token";

            for (var i = 0; i < scopeCandidates.Count; i++)
            {
                var scope = scopeCandidates[i];
                var formPairs = new List<KeyValuePair<string, string>>
                {
                    new("grant_type", "password"),
                    new("client_id", options.TokenClientId),
                    new("username", options.TokenUsername),
                    new("password", options.TokenPassword),
                };
                if (!string.IsNullOrWhiteSpace(options.TokenClientSecret))
                {
                    formPairs.Add(new KeyValuePair<string, string>("client_secret", options.TokenClientSecret));
                }
                if (!string.IsNullOrWhiteSpace(scope))
                {
                    formPairs.Add(new KeyValuePair<string, string>("scope", scope));
                }

                using var request = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint)
                {
                    Content = new FormUrlEncodedContent(formPairs),
                };
                using var response = await client.SendAsync(request);
                var payload = await SafeReadBodyAsync(response);
                if (response.IsSuccessStatusCode)
                {
                    using var tokenJson = JsonDocument.Parse(payload);
                    var accessToken = ReadString(tokenJson.RootElement, "access_token");
                    if (string.IsNullOrWhiteSpace(accessToken))
                    {
                        throw new InvalidOperationException("Token response did not contain access_token.");
                    }

                    tokenState.ResolvedAccessToken = accessToken;
                    return accessToken;
                }

                lastError = $"Keycloak token request failed via {tokenEndpoint}: {(int)response.StatusCode} {response.StatusCode}";
                if (!string.IsNullOrWhiteSpace(payload))
                {
                    lastError += Environment.NewLine + payload;
                }

                var hasNextScope = i < scopeCandidates.Count - 1;
                var invalidScope = payload.Contains("invalid_scope", StringComparison.OrdinalIgnoreCase);
                if (hasNextScope && (invalidScope || response.StatusCode == HttpStatusCode.BadRequest))
                {
                    continue;
                }

                break;
            }
        }
        while (tokenState.MoveNextAuthority());

        throw new InvalidOperationException(lastError);
    }

    private static async Task<JsonDocument> ReadJsonOrThrowAsync(HttpResponseMessage response)
    {
        var body = await SafeReadBodyAsync(response);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Request failed with status {(int)response.StatusCode} ({response.StatusCode}).{(string.IsNullOrWhiteSpace(body) ? string.Empty : $" {body}")}");
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            throw new InvalidOperationException("Response body is empty.");
        }

        return JsonDocument.Parse(body);
    }

    private static async Task<string> SafeReadBodyAsync(HttpResponseMessage response)
        => response.Content is null ? string.Empty : await response.Content.ReadAsStringAsync();

    private static int Clamp(int value, int min, int max) => Math.Max(min, Math.Min(max, value));

    private static void AddCheck(List<OpsCheck> checks, string name, string status, string message, object? observed, object? expected)
        => checks.Add(new OpsCheck(name, status, message, observed, expected));

    private static bool ReadBoolean(JsonElement element, params string[] path)
    {
        if (!TryGetPath(element, out var value, path))
        {
            return false;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => value.TryGetInt64(out var i) && i != 0,
            JsonValueKind.String => bool.TryParse(value.GetString(), out var parsed) && parsed,
            _ => false,
        };
    }

    private static string? ReadString(JsonElement element, params string[] path)
    {
        if (!TryGetPath(element, out var value, path))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    private static object? ReadScalar(JsonElement element, params string[] path)
    {
        if (!TryGetPath(element, out var value, path))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.TryGetInt64(out var i) ? i : value.TryGetDouble(out var d) ? d : value.GetRawText(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => value.GetRawText(),
        };
    }

    private static string[] ReadStringArray(JsonElement element, params string[] path)
    {
        if (!TryGetPath(element, out var value, path) || value.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        return value.EnumerateArray()
            .Where(static e => e.ValueKind == JsonValueKind.String)
            .Select(static e => e.GetString())
            .Where(static s => !string.IsNullOrWhiteSpace(s))
            .Select(static s => s!)
            .ToArray();
    }

    private static bool TryGetPath(JsonElement element, out JsonElement result, params string[] path)
    {
        result = element;
        foreach (var segment in path)
        {
            if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty(segment, out result))
            {
                return false;
            }
        }

        return true;
    }

    private static void PrintChecksTable(string baseUrl, string resultStatus, string outputJsonPath, IReadOnlyList<OpsCheck> checks)
    {
        Console.WriteLine();
        Console.WriteLine("=== Ops SLO + Security Verify ===");
        Console.WriteLine($"Base URL: {baseUrl}");
        Console.WriteLine($"Status:   {resultStatus}");
        if (!string.IsNullOrWhiteSpace(outputJsonPath))
        {
            Console.WriteLine($"Summary:  {outputJsonPath}");
        }
        Console.WriteLine();

        const int nameWidth = 34;
        const int statusWidth = 6;
        Console.WriteLine($"{Pad("Name", nameWidth)} {Pad("Status", statusWidth)} Observed");
        Console.WriteLine($"{new string('-', nameWidth)} {new string('-', statusWidth)} --------");
        foreach (var check in checks)
        {
            var observed = check.Observed?.ToString() ?? string.Empty;
            if (observed.Length > 90)
            {
                observed = observed[..90] + "...";
            }

            Console.WriteLine($"{Pad(check.Name, nameWidth)} {Pad(check.Status, statusWidth)} {observed}");
        }
    }

    private static string Pad(string value, int width)
        => value.Length >= width ? value[..width] : value.PadRight(width, ' ');

    private sealed class OpsVerifyOptions
    {
        public string BaseUrl { get; set; } = "http://127.0.0.1:18093";
        public int SloWindowMinutes { get; set; } = 60;
        public string AccessToken { get; set; } = "auto";
        public string AuthAuthority { get; set; } = "http://host.docker.internal:18096/realms/hidbridge-dev";
        public string TokenClientId { get; set; } = "controlplane-smoke";
        public string TokenClientSecret { get; set; } = string.Empty;
        public string TokenScope { get; set; } = "openid profile email";
        public string TokenUsername { get; set; } = "operator.smoke.admin";
        public string TokenPassword { get; set; } = "ChangeMe123!";
        public bool AllowAuthDisabled { get; set; }
        public bool FailOnSloWarning { get; set; }
        public bool FailOnSecurityWarning { get; set; }
        public bool RequireAuditTrailCategories { get; set; }
        public int AuditSampleTake { get; set; } = 250;
        public string OutputJsonPath { get; set; } = string.Empty;

        public static bool TryParse(IReadOnlyList<string> args, out OpsVerifyOptions options, out string? error)
        {
            options = new OpsVerifyOptions();
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
                    error = $"Unexpected token '{token}'. Expected PowerShell-style option (e.g. -BaseUrl).";
                    return false;
                }

                var name = token.TrimStart('-');
                var hasValue = i + 1 < args.Count && !args[i + 1].StartsWith("-", StringComparison.Ordinal);
                string? value = hasValue ? args[i + 1] : null;

                switch (name.ToLowerInvariant())
                {
                    case "baseurl": options.BaseUrl = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "slowindowminutes": options.SloWindowMinutes = ParseInt(name, value, hasValue, ref i, ref error); break;
                    case "accesstoken": options.AccessToken = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "authauthority": options.AuthAuthority = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "tokenclientid": options.TokenClientId = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "tokenclientsecret": options.TokenClientSecret = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "tokenscope": options.TokenScope = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "tokenusername": options.TokenUsername = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "tokenpassword": options.TokenPassword = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "allowauthdisabled": options.AllowAuthDisabled = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "failonslowarning": options.FailOnSloWarning = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "failonsecuritywarning": options.FailOnSecurityWarning = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "requireaudittrailcategories": options.RequireAuditTrailCategories = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "auditsampletake": options.AuditSampleTake = ParseInt(name, value, hasValue, ref i, ref error); break;
                    case "outputjsonpath": options.OutputJsonPath = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    default:
                        error = $"Unsupported ops-verify option '{token}'.";
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

    private sealed class TokenState
    {
        private readonly List<string> _authorities;
        private int _cursor;

        private TokenState(List<string> authorities, string? resolvedAccessToken)
        {
            _authorities = authorities;
            ResolvedAccessToken = resolvedAccessToken;
            _cursor = 0;
        }

        public string? ResolvedAccessToken { get; set; }

        public string GetCurrentAuthority()
            => _authorities[Math.Clamp(_cursor, 0, _authorities.Count - 1)];

        public bool MoveNextAuthority()
        {
            if (_cursor < _authorities.Count - 1)
            {
                _cursor++;
                return true;
            }

            return false;
        }

        public static TokenState Initialize(string authAuthority, string accessToken)
        {
            var authorities = new List<string>();
            if (!string.IsNullOrWhiteSpace(authAuthority))
            {
                authorities.Add(authAuthority.TrimEnd('/'));
            }

            if (Uri.TryCreate(authAuthority, UriKind.Absolute, out var parsed))
            {
                var pathAndQuery = string.IsNullOrWhiteSpace(parsed.PathAndQuery) ? "/realms/hidbridge-dev" : parsed.PathAndQuery;
                var fallbackHosts = new List<string>();
                if (string.Equals(parsed.Host, "host.docker.internal", StringComparison.OrdinalIgnoreCase))
                {
                    fallbackHosts.Add("127.0.0.1");
                    fallbackHosts.Add("localhost");
                }
                else if (string.Equals(parsed.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
                      || string.Equals(parsed.Host, "localhost", StringComparison.OrdinalIgnoreCase))
                {
                    fallbackHosts.Add("host.docker.internal");
                }

                foreach (var fallbackHost in fallbackHosts)
                {
                    var builder = new UriBuilder(parsed)
                    {
                        Host = fallbackHost,
                        Path = pathAndQuery.TrimStart('/'),
                    };
                    var candidate = builder.Uri.AbsoluteUri.TrimEnd('/');
                    if (!authorities.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                    {
                        authorities.Add(candidate);
                    }
                }
            }

            if (authorities.Count == 0)
            {
                authorities.Add("http://127.0.0.1:18096/realms/hidbridge-dev");
            }

            string? resolved = null;
            if (!string.IsNullOrWhiteSpace(accessToken)
                && !string.Equals(accessToken, "auto", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(accessToken, "none", StringComparison.OrdinalIgnoreCase))
            {
                resolved = accessToken;
            }

            return new TokenState(authorities, resolved);
        }
    }

    private sealed record OpsCheck(string Name, string Status, string Message, object? Observed, object? Expected);
}

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

        var runnerArgs = new List<string>
        {
            "--platform-root", platformRoot,
            "--api-base-url", options.ApiBaseUrl,
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
            "--uart-port", options.UartPort,
            "--uart-baud", options.UartBaud.ToString(),
            "--uart-hmac-key", options.UartHmacKey,
            "--principal-id", options.PrincipalId,
            "--peer-ready-timeout-sec", options.PeerReadyTimeoutSec.ToString(),
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

        await process.WaitForExitAsync();
        return process.ExitCode;
    }

    private sealed class WebRtcAcceptanceOptions
    {
        public string ApiBaseUrl { get; set; } = "http://127.0.0.1:18093";
        public string CommandExecutor { get; set; } = "uart";
        public bool AllowLegacyControlWs { get; set; }
        public string ControlHealthUrl { get; set; } = "http://127.0.0.1:28092/health";
        public string ControlWsUrl { get; set; } = string.Empty;
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
        public string UartPort { get; set; } = "COM6";
        public int UartBaud { get; set; } = 3000000;
        public string UartHmacKey { get; set; } = "your-master-secret";
        public string PrincipalId { get; set; } = "smoke-runner";
        public int PeerReadyTimeoutSec { get; set; } = 45;
        public bool SkipRuntimeBootstrap { get; set; }
        public bool StopExisting { get; set; }
        public bool StopStackAfter { get; set; }
        public string OutputJsonPath { get; set; } = "Platform/.logs/webrtc-edge-agent-acceptance.result.json";

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
                    case "commandexecutor": options.CommandExecutor = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "allowlegacycontrolws": options.AllowLegacyControlWs = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "controlhealthurl": options.ControlHealthUrl = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "controlwsurl": options.ControlWsUrl = RequireValue(name, value, ref i, ref hasValue, ref error); break;
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
                    case "uartport": options.UartPort = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "uartbaud": options.UartBaud = ParseInt(name, value, hasValue, ref i, ref error); break;
                    case "uarthmackey": options.UartHmacKey = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "principalid": options.PrincipalId = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "peerreadytimeoutsec": options.PeerReadyTimeoutSec = ParseInt(name, value, hasValue, ref i, ref error); break;
                    case "skipruntimebootstrap": options.SkipRuntimeBootstrap = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "stopexisting": options.StopExisting = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "stopstackafter": options.StopStackAfter = ParseSwitch(name, value, hasValue, ref i, ref error); break;
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
    }
}

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
                await RunStepAsync(
                    platformRoot,
                    logRoot,
                    results,
                    options,
                    "Realm Sync",
                    Path.Combine("Identity", "Keycloak", "Sync-HidBridgeDevRealm.ps1"),
                    Array.Empty<string>());
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

                await RunStepAsync(
                    platformRoot,
                    logRoot,
                    results,
                    options,
                    "CI Local",
                    Path.Combine("Scripts", "run_ci_local.ps1"),
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
            var exportArgs = new List<string>
            {
                "-OutputRoot", artifactExportPath,
            };

            if (options.IncludeSmokeDataOnFailure)
            {
                exportArgs.Add("-IncludeSmokeData");
            }

            if (options.IncludeBackupsOnFailure)
            {
                exportArgs.Add("-IncludeBackups");
            }

            try
            {
                await RuntimeCtlApp.RunScriptToLogAsync(platformRoot, Path.Combine("Scripts", "run_export_artifacts.ps1"), exportArgs, Path.Combine(logRoot, "export-artifacts.log"));
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

    private static async Task RunStepAsync(
        string platformRoot,
        string logRoot,
        List<FullStepResult> results,
        FullOptions options,
        string stepName,
        string scriptRelativePath,
        IReadOnlyList<string> scriptArgs)
    {
        var logPath = Path.Combine(logRoot, stepName.Replace(' ', '-').Replace("(", string.Empty).Replace(")", string.Empty) + ".log");

        var stopwatch = Stopwatch.StartNew();
        var invocation = await RuntimeCtlApp.RunScriptToLogAsync(platformRoot, scriptRelativePath, scriptArgs, logPath);
        stopwatch.Stop();

        var status = invocation.ExitCode == 0 ? "PASS" : "FAIL";
        results.Add(new FullStepResult(stepName, status, stopwatch.Elapsed.TotalSeconds, invocation.ExitCode, logPath));

        Console.WriteLine();
        Console.WriteLine($"=== {stepName} ===");
        Console.WriteLine($"{status}  {stepName}");
        Console.WriteLine($"Log:   {logPath}");

        if (invocation.ExitCode != 0 && options.StopOnFailure)
        {
            throw new InvalidOperationException($"{stepName} failed with exit code {invocation.ExitCode}");
        }
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
