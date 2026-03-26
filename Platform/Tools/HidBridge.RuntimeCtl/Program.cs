using System.Diagnostics;
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
            ["full"] = new("full", "Runs full local gate lane.", CommandKind.ScriptBridge, "run_full.ps1"),
            ["webrtc-stack"] = new("webrtc-stack", "Bootstraps WebRTC stack and edge adapter.", CommandKind.ScriptBridge, "run_webrtc_stack.ps1"),
            ["webrtc-acceptance"] = new("webrtc-acceptance", "Runs WebRTC edge-agent acceptance.", CommandKind.ScriptBridge, "run_webrtc_edge_agent_acceptance.ps1"),
            ["ops-verify"] = new("ops-verify", "Runs ops SLO/security verification.", CommandKind.ScriptBridge, "run_ops_slo_security_verify.ps1"),
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
