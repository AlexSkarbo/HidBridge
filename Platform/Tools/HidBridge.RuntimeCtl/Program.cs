using System.Diagnostics;
using System.ComponentModel;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using System.Text;

var app = RuntimeCtlApp.Parse(args);
return await app.RunAsync();

// NOTE: RuntimeCtl currently hosts multiple command lanes in one file for migration speed.
// Next refactor step: split commands into dedicated files (runtime, smoke, webrtc, ci, identity).
internal sealed class RuntimeCtlApp
{
    private static readonly IReadOnlyDictionary<string, CommandSpec> CommandAliases =
        new Dictionary<string, CommandSpec>(StringComparer.OrdinalIgnoreCase)
        {
            ["doctor"] = new("doctor", "Runs environment and API health checks (native C#).", CommandKind.Doctor, null),
            ["checks"] = new("checks", "Runs platform checks lane (native C#).", CommandKind.Checks, null),
            ["tests"] = new("tests", "Runs restore/build/test lane (native C#).", CommandKind.Tests, null),
            ["smoke"] = new("smoke", "Runs backend smoke lane.", CommandKind.Smoke, null),
            ["smoke-file"] = new("smoke-file", "Runs file-provider backend smoke lane.", CommandKind.SmokeFile, null),
            ["smoke-sql"] = new("smoke-sql", "Runs sql-provider backend smoke lane.", CommandKind.SmokeSql, null),
            ["bearer-smoke"] = new("bearer-smoke", "Runs API bearer smoke lane (native C#).", CommandKind.BearerSmoke, null),
            ["smoke-bearer"] = new("smoke-bearer", "Runs API bearer smoke lane (native C#).", CommandKind.BearerSmoke, null),
            ["export-artifacts"] = new("export-artifacts", "Exports runtime artifacts (native C#).", CommandKind.ExportArtifacts, null),
            ["clean-logs"] = new("clean-logs", "Prunes old operational logs/smoke-data (native C#).", CommandKind.CleanLogs, null),
            ["token-debug"] = new("token-debug", "Acquires/inspects OIDC token and dumps claims (native C#).", CommandKind.TokenDebug, null),
            ["close-failed-rooms"] = new("close-failed-rooms", "Closes failed sessions via API action endpoint (native C#).", CommandKind.CloseFailedRooms, null),
            ["close-stale-rooms"] = new("close-stale-rooms", "Closes stale sessions via API action endpoint (native C#).", CommandKind.CloseStaleRooms, null),
            ["demo-seed"] = new("demo-seed", "Seeds demo runtime state (native C#).", CommandKind.DemoSeed, null),
            ["ci-local"] = new("ci-local", "Runs local CI gate lane (native C# orchestration).", CommandKind.CiLocal, null),
            ["full"] = new("full", "Runs full local gate lane (native C# orchestration).", CommandKind.Full, null),
            ["demo-flow"] = new("demo-flow", "Runs demo bootstrap flow (native C# orchestration).", CommandKind.DemoFlow, null),
            ["demo-gate"] = new("demo-gate", "Runs demo runtime gate (native C# orchestration for WebRTC mode).", CommandKind.DemoGate, null),
            ["webrtc-stack"] = new("webrtc-stack", "Bootstraps WebRTC stack and edge adapter (native C# orchestration).", CommandKind.WebRtcStack, null),
            ["webrtc-edge-agent-smoke"] = new("webrtc-edge-agent-smoke", "Runs WebRTC edge-agent smoke (native C# orchestration).", CommandKind.WebRtcEdgeAgentSmoke, null),
            ["webrtc-stack-terminal-b"] = new("webrtc-stack-terminal-b", "Compatibility alias for WebRTC edge-agent smoke (native C# orchestration).", CommandKind.WebRtcEdgeAgentSmoke, null),
            ["webrtc-acceptance"] = new("webrtc-acceptance", "Runs WebRTC edge-agent acceptance (native C# orchestration).", CommandKind.WebRtcAcceptance, null),
            ["webrtc-edge-agent-acceptance"] = new("webrtc-edge-agent-acceptance", "Compatibility alias for WebRTC edge-agent acceptance (native C# orchestration).", CommandKind.WebRtcAcceptance, null),
            ["ops-verify"] = new("ops-verify", "Runs ops SLO/security verification (native C# orchestration).", CommandKind.OpsVerify, null),
            ["ops-slo-security-verify"] = new("ops-slo-security-verify", "Compatibility alias for ops SLO/security verification (native C# orchestration).", CommandKind.OpsVerify, null),
            ["identity-reset"] = new("identity-reset", "Resets Keycloak realm and smoke principals (native C#).", CommandKind.IdentityReset, null),
            ["platform-runtime"] = new("platform-runtime", "Controls docker runtime profile (native C# orchestration).", CommandKind.PlatformRuntime, null),
        };

    private static readonly IReadOnlyDictionary<string, string> TaskMap =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["bearer-rollout"] = "Scripts/run_bearer_rollout_phase.ps1",
            ["identity-onboard"] = "Identity/Keycloak/Onboard-HidBridgeOperator.ps1",
            ["uart-diagnostics"] = "Scripts/run_uart_diagnostics.ps1",
            ["webrtc-relay-smoke"] = "Scripts/run_webrtc_relay_smoke.ps1",
            ["webrtc-peer-adapter"] = "Scripts/run_webrtc_peer_adapter.ps1",
        };

    private static readonly IReadOnlyDictionary<string, string> NativeTaskRedirects =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["webrtc-edge-agent-acceptance"] = "webrtc-acceptance",
            ["ops-slo-security-verify"] = "ops-verify",
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
            if (NativeTaskRedirects.TryGetValue(taskName, out var redirectedTask))
            {
                taskName = redirectedTask;
            }
            if (CommandAliases.TryGetValue(taskName, out var nativeTask))
            {
                return new RuntimeCtlApp(platformRoot, nativeTask, NormalizeForwardArgs(remaining), showHelp: false, error: null);
            }

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
            CommandKind.Doctor => await DoctorCommand.RunAsync(_platformRoot, _forwardArgs),
            CommandKind.Checks => await ChecksCommand.RunAsync(_platformRoot, _forwardArgs),
            CommandKind.Tests => await TestsCommand.RunAsync(_platformRoot, _forwardArgs),
            CommandKind.Smoke => await SmokeCommand.RunAsync(_platformRoot, _forwardArgs, SmokeCommand.SmokeMode.ProviderAuto),
            CommandKind.SmokeFile => await SmokeCommand.RunAsync(_platformRoot, _forwardArgs, SmokeCommand.SmokeMode.ForceFile),
            CommandKind.SmokeSql => await SmokeCommand.RunAsync(_platformRoot, _forwardArgs, SmokeCommand.SmokeMode.ForceSql),
            CommandKind.BearerSmoke => await BearerSmokeCommand.RunAsync(_platformRoot, _forwardArgs),
            CommandKind.ExportArtifacts => await ArtifactExportCommand.RunAsync(_platformRoot, _forwardArgs),
            CommandKind.CleanLogs => await CleanLogsCommand.RunAsync(_platformRoot, _forwardArgs),
            CommandKind.TokenDebug => await TokenDebugCommand.RunAsync(_platformRoot, _forwardArgs),
            CommandKind.CloseFailedRooms => await CloseFailedRoomsCommand.RunAsync(_platformRoot, _forwardArgs),
            CommandKind.CloseStaleRooms => await CloseStaleRoomsCommand.RunAsync(_platformRoot, _forwardArgs),
            CommandKind.DemoSeed => await DemoSeedCommand.RunAsync(_platformRoot, _forwardArgs),
            CommandKind.CiLocal => await CiLocalCommand.RunAsync(_platformRoot, _forwardArgs),
            CommandKind.Full => await FullCommand.RunAsync(_platformRoot, _forwardArgs),
            CommandKind.WebRtcAcceptance => await WebRtcAcceptanceCommand.RunAsync(_platformRoot, _forwardArgs),
            CommandKind.OpsVerify => await OpsVerifyCommand.RunAsync(_platformRoot, _forwardArgs),
            CommandKind.DemoFlow => await DemoFlowCommand.RunAsync(_platformRoot, _forwardArgs),
            CommandKind.DemoGate => await DemoGateCommand.RunAsync(_platformRoot, _forwardArgs),
            CommandKind.WebRtcStack => await WebRtcStackCommand.RunAsync(_platformRoot, _forwardArgs),
            CommandKind.WebRtcEdgeAgentSmoke => await WebRtcEdgeAgentSmokeCommand.RunAsync(_platformRoot, _forwardArgs),
            CommandKind.IdentityReset => await IdentityResetCommand.RunAsync(_platformRoot, _forwardArgs),
            CommandKind.PlatformRuntime => await PlatformRuntimeCommand.RunAsync(_platformRoot, _forwardArgs),
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
        Doctor,
        IdentityReset,
        Checks,
        Tests,
        Smoke,
        SmokeFile,
        SmokeSql,
        BearerSmoke,
        ExportArtifacts,
        CleanLogs,
        TokenDebug,
        CloseFailedRooms,
        CloseStaleRooms,
        DemoSeed,
        CiLocal,
        Full,
        WebRtcAcceptance,
        OpsVerify,
        DemoFlow,
        DemoGate,
        WebRtcStack,
        WebRtcEdgeAgentSmoke,
        PlatformRuntime,
    }

    internal sealed record ScriptInvocationResult(int ExitCode, string StdOut, string StdErr);
}

internal static class PlatformRuntimeCommand
{
    public static async Task<int> RunAsync(string platformRoot, IReadOnlyList<string> args)
    {
        if (!PlatformRuntimeOptions.TryParse(args, out var options, out var parseError))
        {
            Console.Error.WriteLine($"platform-runtime options error: {parseError}");
            return 1;
        }

        try
        {
            var composePath = ResolveComposeFilePath(platformRoot, options.ComposeFile);
            switch (options.Action)
            {
                case PlatformRuntimeAction.Up:
                {
                    if (options.Pull)
                    {
                        await RunComposeAsync(composePath, ["pull"]);
                    }

                    var upArgs = new List<string> { "up", "-d" };
                    if (options.Build)
                    {
                        upArgs.Add("--build");
                    }
                    if (options.RemoveOrphans)
                    {
                        upArgs.Add("--remove-orphans");
                    }

                    await RunComposeAsync(composePath, upArgs);

                    if (!options.SkipReadyWait)
                    {
                        await WaitHttpReadyAsync("API health", "http://127.0.0.1:18093/health", options.ReadyTimeoutSec);
                        await WaitHttpReadyAsync("Web shell", "http://127.0.0.1:18110/", options.ReadyTimeoutSec);
                        await WaitHttpReadyAsync("Keycloak realm", "http://127.0.0.1:18096/realms/hidbridge-dev", options.ReadyTimeoutSec);
                    }

                    PrintRuntimeSummary(composePath);
                    await RunComposeAsync(composePath, ["ps"]);
                    break;
                }
                case PlatformRuntimeAction.Down:
                {
                    var downArgs = new List<string> { "down" };
                    if (options.RemoveVolumes)
                    {
                        downArgs.Add("--volumes");
                    }
                    if (options.RemoveOrphans)
                    {
                        downArgs.Add("--remove-orphans");
                    }

                    await RunComposeAsync(composePath, downArgs);
                    break;
                }
                case PlatformRuntimeAction.Restart:
                {
                    var restartDownArgs = new List<string> { "down" };
                    if (options.RemoveVolumes)
                    {
                        restartDownArgs.Add("--volumes");
                    }
                    if (options.RemoveOrphans)
                    {
                        restartDownArgs.Add("--remove-orphans");
                    }

                    await RunComposeAsync(composePath, restartDownArgs);

                    var restartUpArgs = new List<string> { "up", "-d" };
                    if (options.Build)
                    {
                        restartUpArgs.Add("--build");
                    }
                    if (options.RemoveOrphans)
                    {
                        restartUpArgs.Add("--remove-orphans");
                    }

                    await RunComposeAsync(composePath, restartUpArgs);

                    if (!options.SkipReadyWait)
                    {
                        await WaitHttpReadyAsync("API health", "http://127.0.0.1:18093/health", options.ReadyTimeoutSec);
                        await WaitHttpReadyAsync("Web shell", "http://127.0.0.1:18110/", options.ReadyTimeoutSec);
                        await WaitHttpReadyAsync("Keycloak realm", "http://127.0.0.1:18096/realms/hidbridge-dev", options.ReadyTimeoutSec);
                    }

                    PrintRuntimeSummary(composePath);
                    await RunComposeAsync(composePath, ["ps"]);
                    break;
                }
                case PlatformRuntimeAction.Status:
                    await RunComposeAsync(composePath, ["ps"]);
                    break;
                case PlatformRuntimeAction.Logs:
                {
                    var logArgs = new List<string> { "logs" };
                    if (options.Follow)
                    {
                        logArgs.Add("--follow");
                    }

                    await RunComposeAsync(composePath, logArgs);
                    break;
                }
                default:
                    throw new InvalidOperationException($"Unsupported action: {options.Action}");
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static async Task RunComposeAsync(string composePath, IReadOnlyList<string> composeArgs)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "docker",
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("compose");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add(composePath);
        foreach (var arg in composeArgs)
        {
            startInfo.ArgumentList.Add(arg);
        }

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            throw new InvalidOperationException(
                "docker was not found in PATH. Install Docker Desktop/Engine before running platform-runtime.",
                ex);
        }

        if (process is null)
        {
            throw new InvalidOperationException("Failed to start docker compose process.");
        }

        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"docker compose failed with exit code {process.ExitCode}.");
        }
    }

    private static async Task WaitHttpReadyAsync(string name, string url, int timeoutSec)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(Math.Max(5, timeoutSec));
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await TestHttpReadyAsync(url))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        throw new InvalidOperationException($"{name} did not become ready within {timeoutSec} sec: {url}");
    }

    private static async Task<bool> TestHttpReadyAsync(string url)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var response = await http.GetAsync(url);
            return (int)response.StatusCode >= 200 && (int)response.StatusCode < 400;
        }
        catch
        {
            return false;
        }
    }

    private static string ResolveComposeFilePath(string platformRoot, string composeFilePath)
    {
        if (string.IsNullOrWhiteSpace(composeFilePath))
        {
            throw new InvalidOperationException("Compose file path cannot be empty.");
        }

        if (Path.IsPathRooted(composeFilePath))
        {
            var rootedPath = Path.GetFullPath(composeFilePath);
            if (!File.Exists(rootedPath))
            {
                throw new InvalidOperationException($"Compose file was not found: {rootedPath}");
            }

            return rootedPath;
        }

        var repoRoot = Directory.GetParent(platformRoot)?.FullName ?? platformRoot;
        var candidate = Path.Combine(repoRoot, composeFilePath);
        if (!File.Exists(candidate))
        {
            throw new InvalidOperationException($"Compose file was not found: {candidate}");
        }

        return Path.GetFullPath(candidate);
    }

    private static void PrintRuntimeSummary(string composePath)
    {
        Console.WriteLine();
        Console.WriteLine("=== Platform Runtime Summary ===");
        Console.WriteLine($"Compose file: {composePath}");
        Console.WriteLine("API:          http://127.0.0.1:18093");
        Console.WriteLine("Web:          http://127.0.0.1:18110");
        Console.WriteLine("Keycloak:     http://127.0.0.1:18096");
        Console.WriteLine("Postgres:     127.0.0.1:5434");
        Console.WriteLine("Identity DB:  127.0.0.1:5435");
        Console.WriteLine();
        Console.WriteLine("Edge agent remains external to this compose profile.");
    }

    private enum PlatformRuntimeAction
    {
        Up,
        Down,
        Restart,
        Status,
        Logs,
    }

    private sealed class PlatformRuntimeOptions
    {
        public PlatformRuntimeAction Action { get; set; } = PlatformRuntimeAction.Up;
        public string ComposeFile { get; set; } = "docker-compose.platform-runtime.yml";
        public bool Build { get; set; }
        public bool Pull { get; set; }
        public bool RemoveVolumes { get; set; }
        public bool RemoveOrphans { get; set; }
        public bool Follow { get; set; }
        public bool SkipReadyWait { get; set; }
        public int ReadyTimeoutSec { get; set; } = 180;

        public static bool TryParse(IReadOnlyList<string> args, out PlatformRuntimeOptions options, out string? error)
        {
            options = new PlatformRuntimeOptions();
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
                    error = $"Unexpected token '{token}'. Expected PowerShell-style option (e.g. -Action up).";
                    return false;
                }

                var name = token.TrimStart('-');
                var hasValue = i + 1 < args.Count && !args[i + 1].StartsWith("-", StringComparison.Ordinal);
                var value = hasValue ? args[i + 1] : null;

                switch (name.ToLowerInvariant())
                {
                    case "action":
                    {
                        var actionToken = RequireValue(name, value, ref i, ref hasValue, ref error);
                        if (error is not null)
                        {
                            return false;
                        }

                        if (!TryParseAction(actionToken, out var action))
                        {
                            error = "Action must be one of: up, down, restart, status, logs.";
                            return false;
                        }

                        options.Action = action;
                        break;
                    }
                    case "composefile":
                        options.ComposeFile = RequireValue(name, value, ref i, ref hasValue, ref error);
                        break;
                    case "build":
                        options.Build = ParseSwitch(name, value, hasValue, ref i, ref error);
                        break;
                    case "pull":
                        options.Pull = ParseSwitch(name, value, hasValue, ref i, ref error);
                        break;
                    case "removevolumes":
                        options.RemoveVolumes = ParseSwitch(name, value, hasValue, ref i, ref error);
                        break;
                    case "removeorphans":
                        options.RemoveOrphans = ParseSwitch(name, value, hasValue, ref i, ref error);
                        break;
                    case "follow":
                        options.Follow = ParseSwitch(name, value, hasValue, ref i, ref error);
                        break;
                    case "skipreadywait":
                        options.SkipReadyWait = ParseSwitch(name, value, hasValue, ref i, ref error);
                        break;
                    case "readytimeoutsec":
                    {
                        var timeoutToken = RequireValue(name, value, ref i, ref hasValue, ref error);
                        if (error is not null)
                        {
                            return false;
                        }

                        if (!int.TryParse(timeoutToken, out var timeout) || timeout <= 0)
                        {
                            error = "ReadyTimeoutSec must be a positive integer.";
                            return false;
                        }

                        options.ReadyTimeoutSec = timeout;
                        break;
                    }
                    default:
                        error = $"Unsupported platform-runtime option '{token}'.";
                        return false;
                }

                if (error is not null)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool TryParseAction(string token, out PlatformRuntimeAction action)
        {
            switch (token.Trim().ToLowerInvariant())
            {
                case "up":
                    action = PlatformRuntimeAction.Up;
                    return true;
                case "down":
                    action = PlatformRuntimeAction.Down;
                    return true;
                case "restart":
                    action = PlatformRuntimeAction.Restart;
                    return true;
                case "status":
                    action = PlatformRuntimeAction.Status;
                    return true;
                case "logs":
                    action = PlatformRuntimeAction.Logs;
                    return true;
                default:
                    action = PlatformRuntimeAction.Up;
                    return false;
            }
        }

        private static string RequireValue(string name, string? value, ref int index, ref bool hasValue, ref string? error)
        {
            if (!hasValue || string.IsNullOrWhiteSpace(value))
            {
                error = $"Option -{name} requires a value.";
                return string.Empty;
            }

            index++;
            hasValue = false;
            return value;
        }

        private static bool ParseSwitch(string name, string? value, bool hasValue, ref int index, ref string? error)
        {
            if (!hasValue)
            {
                return true;
            }

            if (!bool.TryParse(value, out var parsed))
            {
                error = $"Option -{name} expects a boolean value when specified with an argument.";
                return false;
            }

            index++;
            return parsed;
        }
    }
}

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

internal static class ChecksCommand
{
    public static async Task<int> RunAsync(string platformRoot, IReadOnlyList<string> args)
    {
        if (!ChecksOptions.TryParse(args, out var options, out var parseError))
        {
            Console.Error.WriteLine($"checks options error: {parseError}");
            return 1;
        }

        var repoRoot = Directory.GetParent(platformRoot)?.FullName ?? platformRoot;
        var logRoot = Path.Combine(platformRoot, ".logs", "checks", DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(logRoot);
        var results = new List<(string Name, string Status, double Seconds, int ExitCode, string LogPath)>();

        async Task RunDotnetStep(string name, IReadOnlyList<string> dotnetArgs)
        {
            var logPath = Path.Combine(logRoot, $"{name.Replace(' ', '-').Replace("(", string.Empty).Replace(")", string.Empty)}.log");
            var sw = Stopwatch.StartNew();
            var startInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                WorkingDirectory = repoRoot,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var arg in dotnetArgs)
            {
                startInfo.ArgumentList.Add(arg);
            }

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                throw new InvalidOperationException($"Failed to start dotnet step '{name}'.");
            }

            var stdOut = await process.StandardOutput.ReadToEndAsync();
            var stdErr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            sw.Stop();
            await File.WriteAllTextAsync(logPath, $"{stdOut}{Environment.NewLine}{stdErr}".Trim());

            var status = process.ExitCode == 0 ? "PASS" : "FAIL";
            results.Add((name, status, sw.Elapsed.TotalSeconds, process.ExitCode, logPath));
            Console.WriteLine();
            Console.WriteLine($"=== {name} ===");
            Console.WriteLine($"{status}  {name}");
            Console.WriteLine($"Log:   {logPath}");
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"{name} failed with exit code {process.ExitCode}");
            }
        }

        try
        {
            if (!options.SkipUnitTests)
            {
                await RunDotnetStep("Platform restore", ["restore", "Platform/HidBridge.Platform.sln"]);
                await RunDotnetStep("Platform build", ["build", "Platform/HidBridge.Platform.sln", "-c", options.Configuration, "-v", options.DotnetVerbosity, "-m:1", "-nodeReuse:false"]);
                await RunDotnetStep("HidBridge.Platform.Tests", ["test", "Platform/Tests/HidBridge.Platform.Tests/HidBridge.Platform.Tests.csproj", "-c", options.Configuration, "-v", options.DotnetVerbosity, "-m:1", "-nodeReuse:false"]);
            }

            if (!options.SkipSmoke)
            {
                var smokeArgs = new List<string>
                {
                    "-Configuration", options.Configuration,
                    "-ConnectionString", options.ConnectionString,
                    "-Schema", options.Schema,
                    "-BaseUrl", options.BaseUrl,
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
                if (!string.IsNullOrWhiteSpace(options.AuthAudience))
                {
                    smokeArgs.Add("-AuthAudience");
                    smokeArgs.Add(options.AuthAudience);
                }
                if (!string.IsNullOrWhiteSpace(options.TokenClientSecret))
                {
                    smokeArgs.Add("-TokenClientSecret");
                    smokeArgs.Add(options.TokenClientSecret);
                }
                if (options.NoBuild || !options.SkipUnitTests)
                {
                    smokeArgs.Add("-NoBuild");
                }

                var smokeExitCode = await BearerSmokeCommand.RunAsync(platformRoot, smokeArgs);
                var smokeLog = Path.Combine(platformRoot, ".logs", "bearer-smoke", "latest.log");
                results.Add(("Platform smoke (Sql)", smokeExitCode == 0 ? "PASS" : "FAIL", 0, smokeExitCode, smokeLog));
                if (smokeExitCode != 0)
                {
                    throw new InvalidOperationException("Platform smoke (Sql) failed.");
                }
            }
        }
        catch (Exception ex)
        {
            var abortPath = Path.Combine(logRoot, "checks.abort.log");
            await File.WriteAllTextAsync(abortPath, ex.ToString());
            results.Add(("Checks orchestration", "FAIL", 0, 1, abortPath));
            Console.WriteLine();
            Console.WriteLine($"Platform checks aborted: {ex.Message}");
        }

        Console.WriteLine();
        Console.WriteLine("=== Checks Summary ===");
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

        return results.Any(static x => string.Equals(x.Status, "FAIL", StringComparison.OrdinalIgnoreCase)) ? 1 : 0;
    }

    private sealed class ChecksOptions
    {
        public string Configuration { get; set; } = "Debug";
        public string DotnetVerbosity { get; set; } = "minimal";
        public string ConnectionString { get; set; } = "Host=127.0.0.1;Port=5434;Database=hidbridge;Username=hidbridge;Password=hidbridge";
        public string Schema { get; set; } = "hidbridge";
        public string BaseUrl { get; set; } = "http://127.0.0.1:18093";
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
        public bool NoBuild { get; set; }
        public bool SkipUnitTests { get; set; }
        public bool SkipSmoke { get; set; }

        public static bool TryParse(IReadOnlyList<string> args, out ChecksOptions options, out string? error)
        {
            options = new ChecksOptions();
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
                    case "configuration": options.Configuration = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "dotnetverbosity": options.DotnetVerbosity = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "connectionstring": options.ConnectionString = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "schema": options.Schema = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "baseurl": options.BaseUrl = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "authauthority": options.AuthAuthority = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "authaudience": options.AuthAudience = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "tokenclientid": options.TokenClientId = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "tokenclientsecret": options.TokenClientSecret = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "tokenscope": options.TokenScope = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "tokenusername": options.TokenUsername = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "tokenpassword": options.TokenPassword = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "viewertokenusername": options.ViewerTokenUsername = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "viewertokenpassword": options.ViewerTokenPassword = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "foreigntokenusername": options.ForeignTokenUsername = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "foreigntokenpassword": options.ForeignTokenPassword = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "nobuild": options.NoBuild = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "skipunittests": options.SkipUnitTests = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "skipsmoke": options.SkipSmoke = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    // accepted for compatibility, handled by bearer-smoke defaults
                    case "provider":
                    case "enableapiauth":
                    case "disableheaderfallback":
                    case "beareronly":
                        if (hasValue) { i++; }
                        break;
                    default:
                        error = $"Unsupported checks option '{token}'.";
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

internal static class TestsCommand
{
    public static async Task<int> RunAsync(string platformRoot, IReadOnlyList<string> args)
    {
        if (!TestsOptions.TryParse(args, out var options, out var parseError))
        {
            Console.Error.WriteLine($"tests options error: {parseError}");
            return 1;
        }

        var repoRoot = Directory.GetParent(platformRoot)?.FullName ?? platformRoot;
        var logRoot = Path.Combine(platformRoot, ".logs", "tests", DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(logRoot);
        var results = new List<(string Name, string Status, double Seconds, int ExitCode, string LogPath)>();

        async Task RunDotnetStep(string name, IReadOnlyList<string> dotnetArgs)
        {
            var logPath = Path.Combine(logRoot, $"{name.Replace(' ', '-').Replace("(", string.Empty).Replace(")", string.Empty)}.log");
            var sw = Stopwatch.StartNew();
            var startInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                WorkingDirectory = repoRoot,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var arg in dotnetArgs)
            {
                startInfo.ArgumentList.Add(arg);
            }

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                throw new InvalidOperationException($"Failed to start dotnet step '{name}'.");
            }

            var stdOut = await process.StandardOutput.ReadToEndAsync();
            var stdErr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            sw.Stop();
            await File.WriteAllTextAsync(logPath, $"{stdOut}{Environment.NewLine}{stdErr}".Trim());

            var status = process.ExitCode == 0 ? "PASS" : "FAIL";
            results.Add((name, status, sw.Elapsed.TotalSeconds, process.ExitCode, logPath));
            Console.WriteLine();
            Console.WriteLine($"=== {name} ===");
            Console.WriteLine($"{status}  {name}");
            Console.WriteLine($"Log:   {logPath}");
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"{name} failed with exit code {process.ExitCode}");
            }
        }

        try
        {
            if (ResolveDotnetPath() is null)
            {
                var logPath = Path.Combine(logRoot, "dotnet-availability.log");
                await File.WriteAllTextAsync(logPath, "dotnet not found in PATH");
                results.Add(("dotnet availability", "FAIL", 0, 127, logPath));
                if (options.StopOnFailure)
                {
                    throw new InvalidOperationException("dotnet not found in PATH");
                }
            }
            else
            {
                await RunDotnetStep("Platform restore", ["restore", "Platform/HidBridge.Platform.sln"]);
                await RunDotnetStep("Platform build", ["build", "Platform/HidBridge.Platform.sln", "-c", options.Configuration, "-v", options.DotnetVerbosity, "-m:1", "-nodeReuse:false"]);
                await RunDotnetStep("HidBridge.Platform.Tests", ["test", "Platform/Tests/HidBridge.Platform.Tests/HidBridge.Platform.Tests.csproj", "-c", options.Configuration, "-v", options.DotnetVerbosity, "-m:1", "-nodeReuse:false"]);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.WriteLine($"Platform test runner aborted: {ex.Message}");
        }

        Console.WriteLine();
        Console.WriteLine("=== Tests Summary ===");
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

        return results.Any(static x => string.Equals(x.Status, "FAIL", StringComparison.OrdinalIgnoreCase)) ? 1 : 0;
    }

    private static string? ResolveDotnetPath()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var entries = path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var entry in entries)
        {
            if (OperatingSystem.IsWindows())
            {
                var candidateExe = Path.Combine(entry, "dotnet.exe");
                if (File.Exists(candidateExe))
                {
                    return candidateExe;
                }
            }

            var candidate = Path.Combine(entry, "dotnet");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private sealed class TestsOptions
    {
        public string Configuration { get; set; } = "Debug";
        public string DotnetVerbosity { get; set; } = "minimal";
        public bool StopOnFailure { get; set; }

        public static bool TryParse(IReadOnlyList<string> args, out TestsOptions options, out string? error)
        {
            options = new TestsOptions();
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
                    case "configuration": options.Configuration = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "dotnetverbosity": options.DotnetVerbosity = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "stoponfailure": options.StopOnFailure = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    default:
                        error = $"Unsupported tests option '{token}'.";
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

internal static class DemoSeedCommand
{
    public static async Task<int> RunAsync(string platformRoot, IReadOnlyList<string> args)
    {
        if (!DemoSeedOptions.TryParse(args, out var options, out var parseError))
        {
            Console.Error.WriteLine($"demo-seed options error: {parseError}");
            return 1;
        }

        try
        {
            var accessToken = options.AccessToken;
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                accessToken = await RequestTokenAsync(options);
            }

            var callerHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Authorization"] = $"Bearer {accessToken}",
                ["X-HidBridge-UserId"] = options.PrincipalId,
                ["X-HidBridge-PrincipalId"] = options.PrincipalId,
                ["X-HidBridge-TenantId"] = options.TenantId,
                ["X-HidBridge-OrganizationId"] = options.OrganizationId,
                ["X-HidBridge-Role"] = "operator.admin,operator.moderator,operator.viewer",
            };

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            var sessions = await InvokeJsonArrayAsync(http, HttpMethod.Get, $"{options.BaseUrl.TrimEnd('/')}/api/v1/sessions", callerHeaders, null);
            var activeSessions = sessions.Where(IsActiveSession).ToArray();
            var sessionsToClose = sessions.Where(IsClosableSession).ToArray();

            var closedSessionIds = new List<string>();
            if (!options.SkipCloseActiveSessions)
            {
                foreach (var session in sessionsToClose)
                {
                    var sessionId = GetSessionId(session);
                    if (string.IsNullOrWhiteSpace(sessionId))
                    {
                        continue;
                    }

                    var closeBody = JsonSerializer.SerializeToElement(new
                    {
                        sessionId,
                        reason = "demo-seed cleanup",
                    });
                    _ = await InvokeJsonElementAsync(
                        http,
                        HttpMethod.Post,
                        $"{options.BaseUrl.TrimEnd('/')}/api/v1/sessions/{Uri.EscapeDataString(sessionId)}/close",
                        callerHeaders,
                        closeBody);
                    closedSessionIds.Add(sessionId);
                }
            }

            JsonElement inventory = default;
            JsonElement[] endpoints = Array.Empty<JsonElement>();
            JsonElement? readyEndpoint = null;
            var timeoutSeconds = Math.Max(1, options.ReadyEndpointTimeoutSec);
            var pollIntervalMs = Math.Max(100, options.ReadyEndpointPollIntervalMs);
            var deadline = DateTimeOffset.UtcNow.AddSeconds(timeoutSeconds);

            while (true)
            {
                inventory = await InvokeJsonElementAsync(http, HttpMethod.Get, $"{options.BaseUrl.TrimEnd('/')}/api/v1/dashboards/inventory", callerHeaders, null);
                endpoints = GetArrayProperty(inventory, "endpoints");
                readyEndpoint = endpoints.FirstOrDefault(endpoint => IsEndpointReady(endpoint, sessions));

                if (readyEndpoint.HasValue)
                {
                    break;
                }

                if (DateTimeOffset.UtcNow >= deadline)
                {
                    break;
                }

                await Task.Delay(pollIntervalMs);
            }

            if (!readyEndpoint.HasValue)
            {
                var endpointSample = string.Join(", ", endpoints.Take(5).Select(endpoint =>
                {
                    var endpointId = GetStringProperty(endpoint, "endpointId");
                    var activeSessionId = GetStringProperty(endpoint, "activeSessionId");
                    if (string.IsNullOrWhiteSpace(activeSessionId))
                    {
                        return $"{endpointId}:idle";
                    }
                    var linkedSession = sessions.FirstOrDefault(x => string.Equals(GetSessionId(x), activeSessionId, StringComparison.Ordinal));
                    var linkedState = linkedSession.ValueKind == JsonValueKind.Undefined ? "stale" : GetSessionState(linkedSession);
                    return $"{endpointId}:busy({activeSessionId},state={linkedState})";
                }));

                throw new InvalidOperationException(
                    $"Demo seed did not find any idle endpoint within {timeoutSeconds} second(s). " +
                    $"Active sessions in scope: {activeSessions.Length}; non-terminal sessions in scope: {sessionsToClose.Length}; " +
                    $"endpoints visible: {endpoints.Length}; endpoint sample: {endpointSample}.");
            }

            Console.WriteLine();
            Console.WriteLine("=== Demo Seed Summary ===");
            Console.WriteLine($"BaseUrl: {options.BaseUrl}");
            Console.WriteLine($"Active sessions found: {activeSessions.Length}");
            Console.WriteLine($"Non-terminal sessions found: {sessionsToClose.Length}");
            Console.WriteLine($"Closed sessions: {closedSessionIds.Count}");
            Console.WriteLine($"Endpoints visible: {endpoints.Length}");
            if (closedSessionIds.Count > 0)
            {
                Console.WriteLine($"Closed session IDs: {string.Join(", ", closedSessionIds)}");
            }
            Console.WriteLine($"Ready endpoint: {GetStringProperty(readyEndpoint.Value, "endpointId")}");
            Console.WriteLine($"Ready endpoint agent: {GetStringProperty(readyEndpoint.Value, "agentId")}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static bool IsActiveSession(JsonElement session) =>
        string.Equals(GetSessionState(session), "Active", StringComparison.OrdinalIgnoreCase);

    private static bool IsClosableSession(JsonElement session)
    {
        var sessionId = GetSessionId(session);
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        var state = GetSessionState(session);
        if (string.IsNullOrWhiteSpace(state))
        {
            return true;
        }

        return !string.Equals(state, "Ended", StringComparison.OrdinalIgnoreCase)
               && !string.Equals(state, "Failed", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsEndpointReady(JsonElement endpoint, IReadOnlyList<JsonElement> sessions)
    {
        var activeSessionId = GetStringProperty(endpoint, "activeSessionId");
        if (string.IsNullOrWhiteSpace(activeSessionId))
        {
            return true;
        }

        var linkedSession = sessions.FirstOrDefault(x => string.Equals(GetSessionId(x), activeSessionId, StringComparison.Ordinal));
        if (linkedSession.ValueKind == JsonValueKind.Undefined)
        {
            return true;
        }

        var state = GetSessionState(linkedSession);
        return string.Equals(state, "Ended", StringComparison.OrdinalIgnoreCase)
               || string.Equals(state, "Failed", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetSessionId(JsonElement session)
    {
        var sessionId = GetStringProperty(session, "sessionId");
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            return sessionId;
        }
        return GetStringProperty(session, "SessionId");
    }

    private static string GetSessionState(JsonElement session)
    {
        if (session.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }
        if (!session.TryGetProperty("state", out var state) || state.ValueKind == JsonValueKind.Null)
        {
            return string.Empty;
        }
        return state.ValueKind == JsonValueKind.String ? (state.GetString() ?? string.Empty) : state.GetRawText();
    }

    private static string GetStringProperty(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return string.Empty;
        }
        return property.ValueKind == JsonValueKind.String ? (property.GetString() ?? string.Empty) : property.GetRawText();
    }

    private static JsonElement[] GetArrayProperty(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<JsonElement>();
        }
        return property.EnumerateArray().ToArray();
    }

    private static async Task<JsonElement[]> InvokeJsonArrayAsync(
        HttpClient http,
        HttpMethod method,
        string uri,
        IReadOnlyDictionary<string, string> headers,
        JsonElement? body)
    {
        var element = await InvokeJsonElementAsync(http, method, uri, headers, body);
        if (element.ValueKind == JsonValueKind.Array)
        {
            return element.EnumerateArray().ToArray();
        }
        return Array.Empty<JsonElement>();
    }

    private static async Task<JsonElement> InvokeJsonElementAsync(
        HttpClient http,
        HttpMethod method,
        string uri,
        IReadOnlyDictionary<string, string> headers,
        JsonElement? body)
    {
        using var request = new HttpRequestMessage(method, uri);
        foreach (var header in headers)
        {
            if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value) && request.Content is not null)
            {
                request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        if (body.HasValue)
        {
            request.Content = new StringContent(body.Value.GetRawText(), Encoding.UTF8, "application/json");
        }

        using var response = await http.SendAsync(request);
        var payload = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"HTTP {method.Method} {uri} failed ({(int)response.StatusCode}): {payload}");
        }
        using var doc = JsonDocument.Parse(payload);
        return doc.RootElement.Clone();
    }

    private static async Task<string> RequestTokenAsync(DemoSeedOptions options)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
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

        string? lastError = null;
        foreach (var scope in scopeCandidates)
        {
            var form = new Dictionary<string, string>
            {
                ["grant_type"] = "password",
                ["client_id"] = options.TokenClientId,
                ["username"] = options.TokenUsername,
                ["password"] = options.TokenPassword,
            };
            if (!string.IsNullOrWhiteSpace(options.TokenClientSecret))
            {
                form["client_secret"] = options.TokenClientSecret;
            }
            if (!string.IsNullOrWhiteSpace(scope))
            {
                form["scope"] = scope;
            }

            using var response = await client.PostAsync(
                $"{options.KeycloakBaseUrl.TrimEnd('/')}/realms/{Uri.EscapeDataString(options.RealmName)}/protocol/openid-connect/token",
                new FormUrlEncodedContent(form));
            var payload = await response.Content.ReadAsStringAsync();
            if (response.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(payload);
                if (!doc.RootElement.TryGetProperty("access_token", out var token))
                {
                    throw new InvalidOperationException("Demo seed token response did not contain access_token.");
                }
                return token.GetString() ?? string.Empty;
            }

            lastError = $"Demo seed could not acquire bearer token for '{options.TokenUsername}'. Token request failed ({(int)response.StatusCode}): {payload}";
            var invalidScope = response.StatusCode == HttpStatusCode.BadRequest
                               && payload.Contains("invalid_scope", StringComparison.OrdinalIgnoreCase);
            var hasMore = !string.Equals(scope, scopeCandidates[^1], StringComparison.Ordinal);
            if (invalidScope && hasMore)
            {
                continue;
            }

            throw new InvalidOperationException(lastError);
        }

        throw new InvalidOperationException(lastError ?? "Demo seed token request failed.");
    }

    private sealed class DemoSeedOptions
    {
        public string BaseUrl { get; set; } = "http://127.0.0.1:18093";
        public string KeycloakBaseUrl { get; set; } = "http://127.0.0.1:18096";
        public string RealmName { get; set; } = "hidbridge-dev";
        public string TokenClientId { get; set; } = "controlplane-smoke";
        public string TokenClientSecret { get; set; } = string.Empty;
        public string TokenScope { get; set; } = "openid profile email";
        public string TokenUsername { get; set; } = "operator.smoke.admin";
        public string TokenPassword { get; set; } = "ChangeMe123!";
        public string AccessToken { get; set; } = string.Empty;
        public string PrincipalId { get; set; } = "demo-seed-admin";
        public string TenantId { get; set; } = "local-tenant";
        public string OrganizationId { get; set; } = "local-org";
        public bool SkipCloseActiveSessions { get; set; }
        public int ReadyEndpointTimeoutSec { get; set; } = 60;
        public int ReadyEndpointPollIntervalMs { get; set; } = 500;

        public static bool TryParse(IReadOnlyList<string> args, out DemoSeedOptions options, out string? error)
        {
            options = new DemoSeedOptions();
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
                    case "baseurl": options.BaseUrl = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "keycloakbaseurl": options.KeycloakBaseUrl = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "realmname": options.RealmName = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "tokenclientid": options.TokenClientId = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "tokenclientsecret": options.TokenClientSecret = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "tokenscope": options.TokenScope = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "tokenusername": options.TokenUsername = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "tokenpassword": options.TokenPassword = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "accesstoken": options.AccessToken = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "principalid": options.PrincipalId = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "tenantid": options.TenantId = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "organizationid": options.OrganizationId = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "skipcloseactivesessions": options.SkipCloseActiveSessions = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "readyendpointtimeoutsec": options.ReadyEndpointTimeoutSec = ParseInt(name, value, hasValue, ref i, ref error); break;
                    case "readyendpointpollintervalms": options.ReadyEndpointPollIntervalMs = ParseInt(name, value, hasValue, ref i, ref error); break;
                    default:
                        error = $"Unsupported demo-seed option '{token}'.";
                        return false;
                }

                if (error is not null)
                {
                    return false;
                }
            }

            return true;
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

internal static class BearerSmokeCommand
{
    public static async Task<int> RunAsync(string platformRoot, IReadOnlyList<string> args)
    {
        if (!BearerSmokeOptions.TryParse(args, out var options, out var parseError))
        {
            Console.Error.WriteLine($"bearer-smoke options error: {parseError}");
            return 1;
        }

        var repoRoot = Directory.GetParent(platformRoot)?.FullName ?? platformRoot;
        var logRoot = Path.Combine(platformRoot, ".logs", "bearer-smoke", DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(logRoot);
        var latest = Path.Combine(platformRoot, ".logs", "bearer-smoke", "latest.log");
        Directory.CreateDirectory(Path.GetDirectoryName(latest)!);
        var logPath = Path.Combine(logRoot, "bearer-smoke.log");
        var summaryPath = Path.Combine(logRoot, "smoke.result.json");
        var stdoutLog = Path.Combine(logRoot, "controlplane.stdout.log");
        var stderrLog = Path.Combine(logRoot, "controlplane.stderr.log");
        var projectPath = Path.Combine(repoRoot, "Platform", "Platform", "HidBridge.ControlPlane.Api", "HidBridge.ControlPlane.Api.csproj");

        var lines = new List<string>();
        Process? apiProcess = null;
        var startedApi = false;

        try
        {
            if (!options.NoBuild)
            {
                var buildLogPath = Path.Combine(logRoot, "build.log");
                var buildExitCode = await RunDotnetStepAsync(
                    repoRoot,
                    ["build", "Platform/HidBridge.Platform.sln", "-c", options.Configuration, "-v", "minimal", "-m:1", "-nodeReuse:false"],
                    buildLogPath);
                if (buildExitCode != 0)
                {
                    throw new InvalidOperationException($"Build failed with exit code {buildExitCode}. See {buildLogPath}.");
                }
                lines.Add("PASS build");
            }

            if (string.IsNullOrWhiteSpace(options.DataRoot))
            {
                options.DataRoot = Path.Combine(platformRoot, ".smoke-data", options.Provider, DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss"));
            }
            Directory.CreateDirectory(options.DataRoot);

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            var apiHealthyAtRequestedUrl = await TryHealthAsync(http, options.BaseUrl);
            var effectiveBaseUrl = options.BaseUrl.TrimEnd('/');
            var shouldLaunchDedicatedApi = !apiHealthyAtRequestedUrl || options.ProviderExplicit;
            if (shouldLaunchDedicatedApi)
            {
                var launchBaseUrl = options.BaseUrl.TrimEnd('/');
                if (apiHealthyAtRequestedUrl)
                {
                    var requestedUri = new Uri(options.BaseUrl);
                    var requestedPort = requestedUri.IsDefaultPort ? 80 : requestedUri.Port;
                    var freePort = FindAvailablePort(requestedPort + 1);
                    launchBaseUrl = $"{requestedUri.Scheme}://127.0.0.1:{freePort}";
                    lines.Add($"INFO api.dedicated.url={launchBaseUrl}");
                }

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
                startInfo.ArgumentList.Add(projectPath);
                startInfo.ArgumentList.Add("-c");
                startInfo.ArgumentList.Add(options.Configuration);
                startInfo.ArgumentList.Add("--no-build");
                startInfo.ArgumentList.Add("--no-launch-profile");
                startInfo.Environment["ASPNETCORE_URLS"] = launchBaseUrl;
                // API appsettings pins Kestrel endpoint to :18093; force dedicated smoke URL explicitly.
                startInfo.Environment["Kestrel__Endpoints__Http__Url"] = launchBaseUrl;
                startInfo.Environment["HIDBRIDGE_PERSISTENCE_PROVIDER"] = options.Provider;
                startInfo.Environment["HIDBRIDGE_DATA_ROOT"] = options.DataRoot;
                startInfo.Environment["HIDBRIDGE_SQL_CONNECTION"] = options.ConnectionString;
                startInfo.Environment["HIDBRIDGE_SQL_SCHEMA"] = options.Schema;
                startInfo.Environment["HIDBRIDGE_SQL_APPLY_MIGRATIONS"] = "true";
                startInfo.Environment["HIDBRIDGE_TRANSPORT_PROVIDER"] = "webrtc-datachannel";
                startInfo.Environment["HIDBRIDGE_AUTH_ENABLED"] = options.EnableApiAuth ? "true" : "false";
                startInfo.Environment["HIDBRIDGE_AUTH_AUTHORITY"] = options.AuthAuthority;
                startInfo.Environment["HIDBRIDGE_AUTH_AUDIENCE"] = options.AuthAudience;
                startInfo.Environment["HIDBRIDGE_AUTH_REQUIRE_HTTPS_METADATA"] = options.EnableApiAuth && options.AuthAuthority.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? "false" : "true";
                startInfo.Environment["HIDBRIDGE_AUTH_ALLOW_HEADER_FALLBACK"] = options.DisableHeaderFallback ? "false" : "true";
                startInfo.Environment["HIDBRIDGE_UART_PASSIVE_HEALTH_MODE"] = "true";
                startInfo.Environment["HIDBRIDGE_UART_RELEASE_PORT_AFTER_EXECUTE"] = "true";

                apiProcess = WebRtcStackCommand.StartRedirectedProcess(startInfo, stdoutLog, stderrLog);
                startedApi = true;

                var healthReady = false;
                for (var i = 0; i < 40; i++)
                {
                    await Task.Delay(500);
                    if (await TryHealthAsync(http, launchBaseUrl))
                    {
                        healthReady = true;
                        break;
                    }
                    if (apiProcess.HasExited)
                    {
                        break;
                    }
                }

                if (!healthReady)
                {
                    throw new InvalidOperationException($"API did not become healthy at {launchBaseUrl}.");
                }

                effectiveBaseUrl = launchBaseUrl;
                options.BaseUrl = launchBaseUrl;
            }

            lines.Add("PASS api.health");

            var inventoryUri = $"{options.BaseUrl.TrimEnd('/')}/api/v1/dashboards/inventory";
            var ownerToken = string.Empty;
            var viewerToken = string.Empty;
            var foreignToken = string.Empty;
            var bearerChecksRequested = options.EnableApiAuth || options.BearerOnly;
            var effectiveBearerChecks = bearerChecksRequested;
            var providedAccessToken = !string.IsNullOrWhiteSpace(options.AccessToken) && IsJwtTokenLike(options.AccessToken);
            if (!effectiveBearerChecks)
            {
                var anonymousStatus = await GetStatusAsync(http, inventoryUri, null);
                if (anonymousStatus is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    effectiveBearerChecks = true;
                    lines.Add("WARN inventory.anonymous.denied.auto-switch-bearer");
                }
                else if ((int)anonymousStatus >= 200 && (int)anonymousStatus < 300)
                {
                    lines.Add("PASS inventory.anonymous.access");
                }
                else
                {
                    throw new InvalidOperationException($"Unexpected anonymous status for inventory endpoint: {(int)anonymousStatus}.");
                }
            }

            if (effectiveBearerChecks)
            {
                if (providedAccessToken)
                {
                    ownerToken = options.AccessToken;
                    viewerToken = options.AccessToken;
                    foreignToken = options.AccessToken;
                    lines.Add("PASS oidc.tokens.provided");
                }
                else
                {
                    ownerToken = await RequestOidcTokenAsync(options.AuthAuthority, options.TokenClientId, options.TokenClientSecret, options.TokenScope, options.TokenUsername, options.TokenPassword);
                    viewerToken = await RequestOidcTokenAsync(options.AuthAuthority, options.TokenClientId, options.TokenClientSecret, options.TokenScope, options.ViewerTokenUsername, options.ViewerTokenPassword);
                    foreignToken = await RequestOidcTokenAsync(options.AuthAuthority, options.TokenClientId, options.TokenClientSecret, options.TokenScope, options.ForeignTokenUsername, options.ForeignTokenPassword);
                    lines.Add("PASS oidc.tokens");
                }

                var unauthorizedStatus = await GetStatusAsync(http, inventoryUri, null);
                if (unauthorizedStatus is not HttpStatusCode.Unauthorized and not HttpStatusCode.Forbidden)
                {
                    throw new InvalidOperationException($"Expected 401/403 without bearer token for inventory endpoint, got {(int)unauthorizedStatus}.");
                }
                lines.Add("PASS inventory.anonymous.denied");

                await EnsureSuccessAsync(http, inventoryUri, ownerToken, "owner");
                await EnsureSuccessAsync(http, inventoryUri, viewerToken, "viewer");
                await EnsureSuccessAsync(http, inventoryUri, foreignToken, "foreign");
                lines.Add("PASS inventory.bearer.access");

                var runtimeUri = $"{options.BaseUrl.TrimEnd('/')}/api/v1/runtime/uart";
                using var runtimeRequest = new HttpRequestMessage(HttpMethod.Get, runtimeUri);
                runtimeRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ownerToken);
                using var runtimeResponse = await http.SendAsync(runtimeRequest);
                var runtimePayload = await runtimeResponse.Content.ReadAsStringAsync();
                if (!runtimeResponse.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException($"Runtime probe failed ({(int)runtimeResponse.StatusCode}): {runtimePayload}");
                }

                using var runtimeDoc = JsonDocument.Parse(runtimePayload);
                var provider = runtimeDoc.RootElement.TryGetProperty("persistenceProvider", out var providerProperty)
                    ? (providerProperty.GetString() ?? string.Empty)
                    : string.Empty;
                if (!string.IsNullOrWhiteSpace(provider)
                    && !string.Equals(provider, options.Provider, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Runtime provider mismatch. Expected '{options.Provider}', got '{provider}'.");
                }
                lines.Add("PASS runtime.provider");
            }

            var multiActorBearerMode = effectiveBearerChecks && !providedAccessToken;
            JsonElement? scenario = null;
            var shouldRunScenario = effectiveBearerChecks || !options.DisableHeaderFallback;
            if (shouldRunScenario)
            {
                scenario = await RunSessionScenarioAsync(
                    http,
                    options,
                    effectiveBearerChecks,
                    ownerToken,
                    viewerToken,
                    foreignToken,
                    multiActorBearerMode);
                lines.Add("PASS session.flow");
            }
            else
            {
                lines.Add("WARN session.flow.skipped");
            }

            var summary = new
            {
                generatedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
                status = "PASS",
                baseUrl = options.BaseUrl,
                provider = options.Provider,
                dataRoot = options.DataRoot,
                authAuthority = options.AuthAuthority,
                multiActorBearerMode,
                checks = lines,
                scenario,
            };
            await File.WriteAllTextAsync(summaryPath, JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
            await File.WriteAllLinesAsync(logPath, lines);
            await File.WriteAllTextAsync(latest, logPath);

            Console.WriteLine();
            Console.WriteLine("=== Bearer Smoke Summary ===");
            Console.WriteLine("PASS  Bearer Smoke");
            Console.WriteLine($"Log:   {logPath}");
            return 0;
        }
        catch (Exception ex)
        {
            lines.Add($"FAIL {ex.Message}");
            await File.WriteAllLinesAsync(logPath, lines);
            await File.WriteAllTextAsync(latest, logPath);
            Console.WriteLine();
            Console.WriteLine("=== Bearer Smoke Summary ===");
            Console.WriteLine("FAIL  Bearer Smoke");
            Console.WriteLine($"Log:   {logPath}");
            return 1;
        }
        finally
        {
            if (startedApi && apiProcess is not null && !apiProcess.HasExited)
            {
                try
                {
                    apiProcess.Kill(entireProcessTree: true);
                }
                catch
                {
                    // best effort
                }
            }
        }
    }

    private static async Task EnsureSuccessAsync(HttpClient http, string uri, string accessToken, string actor)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, uri);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var resp = await http.SendAsync(req);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"Expected success for {actor} bearer on {uri}, got {(int)resp.StatusCode} {resp.StatusCode}. {body}");
        }
    }

    private static async Task<int> RunDotnetStepAsync(string workingDirectory, IReadOnlyList<string> args, string logPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            await File.WriteAllTextAsync(logPath, "Failed to start dotnet process.");
            return 1;
        }

        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Directory.CreateDirectory(Path.GetDirectoryName(logPath) ?? workingDirectory);
        await File.WriteAllTextAsync(logPath, $"{stdout}{Environment.NewLine}{stderr}".Trim());
        return process.ExitCode;
    }

    private static bool IsJwtTokenLike(string value) =>
        value.Count(static c => c == '.') >= 2;

    private static int FindAvailablePort(int startingPort)
    {
        var port = Math.Max(1025, startingPort);
        for (var attempt = 0; attempt < 200; attempt++, port++)
        {
            try
            {
                using var listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                return port;
            }
            catch (SocketException)
            {
                // try next port
            }
        }

        throw new InvalidOperationException($"Unable to find free TCP port starting from {startingPort}.");
    }

    private static async Task<HttpStatusCode> GetStatusAsync(HttpClient http, string uri, string? accessToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, uri);
        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }
        using var resp = await http.SendAsync(req);
        return resp.StatusCode;
    }

    private static async Task<bool> TryHealthAsync(HttpClient http, string baseUrl)
    {
        try
        {
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

    private static async Task<string> RequestOidcTokenAsync(
        string authAuthority,
        string clientId,
        string clientSecret,
        string scope,
        string username,
        string password)
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
        foreach (var candidateScope in scopeCandidates)
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
            if (!string.IsNullOrWhiteSpace(candidateScope))
            {
                form["scope"] = candidateScope;
            }

            using var response = await client.PostAsync(
                $"{authAuthority.TrimEnd('/')}/protocol/openid-connect/token",
                new FormUrlEncodedContent(form));
            var payload = await response.Content.ReadAsStringAsync();
            if (response.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(payload);
                if (!doc.RootElement.TryGetProperty("access_token", out var accessToken))
                {
                    throw new InvalidOperationException("OIDC token response did not contain access_token.");
                }

                return accessToken.GetString() ?? string.Empty;
            }

            lastError = $"Token request failed ({(int)response.StatusCode}): {payload}";
            var invalidScope = response.StatusCode == HttpStatusCode.BadRequest
                               && payload.Contains("invalid_scope", StringComparison.OrdinalIgnoreCase);
            var hasMore = !string.Equals(candidateScope, scopeCandidates[^1], StringComparison.Ordinal);
            if (invalidScope && hasMore)
            {
                continue;
            }

            throw new InvalidOperationException(lastError);
        }

        throw new InvalidOperationException(lastError ?? "Token request failed.");
    }

    private static async Task<JsonElement> RunSessionScenarioAsync(
        HttpClient http,
        BearerSmokeOptions options,
        bool requireBearer,
        string ownerToken,
        string viewerToken,
        string foreignToken,
        bool multiActorBearerMode)
    {
        static int Count(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Array)
            {
                return value.GetArrayLength();
            }
            if (value.ValueKind == JsonValueKind.Null || value.ValueKind == JsonValueKind.Undefined)
            {
                return 0;
            }
            return 1;
        }

        static void Ensure(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }

        var runId = Guid.NewGuid().ToString("N")[..8];
        var sessionId = $"smoke-{options.Provider.ToLowerInvariant()}-{runId}";
        var participantId = $"participant-observer-{runId}";
        var shareId = $"share-observer-{runId}";
        var invitationShareId = $"invite-controller-{runId}";
        var commandId = $"cmd-noop-{runId}";
        var baseUrl = options.BaseUrl.TrimEnd('/');
        var includeFallbackHeaders = !options.DisableHeaderFallback;

        var ownerHeaders = BuildCallerHeaders(
            subjectId: "user-smoke-owner",
            principalId: "smoke-runner",
            roles: "operator.admin",
            tenantId: "local-tenant",
            organizationId: "local-org",
            includeFallbackHeaders: includeFallbackHeaders,
            bearerToken: requireBearer ? ownerToken : null);
        var controllerHeaders = BuildCallerHeaders(
            subjectId: "user-smoke-controller",
            principalId: "controller-user",
            roles: "operator.viewer",
            tenantId: "local-tenant",
            organizationId: "local-org",
            includeFallbackHeaders: includeFallbackHeaders,
            bearerToken: requireBearer ? (multiActorBearerMode ? viewerToken : ownerToken) : null);
        var foreignHeaders = BuildCallerHeaders(
            subjectId: "user-foreign-viewer",
            principalId: "foreign-user",
            roles: "operator.viewer",
            tenantId: "foreign-tenant",
            organizationId: "foreign-org",
            includeFallbackHeaders: includeFallbackHeaders,
            bearerToken: requireBearer ? (multiActorBearerMode ? foreignToken : ownerToken) : null);

        var agents = await InvokeJsonAsync(
            http,
            HttpMethod.Get,
            $"{baseUrl}/api/v1/agents",
            ownerHeaders,
            null);
        var endpoints = await InvokeJsonAsync(
            http,
            HttpMethod.Get,
            $"{baseUrl}/api/v1/endpoints",
            ownerHeaders,
            null);
        var auditBefore = await InvokeJsonAsync(
            http,
            HttpMethod.Get,
            $"{baseUrl}/api/v1/events/audit",
            ownerHeaders,
            null);
        Ensure(Count(agents) >= 1, "Expected at least one registered agent.");
        Ensure(Count(endpoints) >= 1, "Expected at least one endpoint snapshot.");
        Ensure(Count(auditBefore) >= 1, "Expected at least one audit event before smoke session.");

        var session = await InvokeJsonAsync(
            http,
            HttpMethod.Post,
            $"{baseUrl}/api/v1/sessions",
            ownerHeaders,
            new
            {
                sessionId,
                profile = "UltraLowLatency",
                requestedBy = "smoke-runner",
                targetAgentId = "agent_hidbridge_uart_local",
                targetEndpointId = "endpoint_local_demo",
                shareMode = "Owner",
                tenantId = "local-tenant",
                organizationId = "local-org",
            });
        Ensure(string.Equals(GetStringProperty(session, "sessionId"), sessionId, StringComparison.Ordinal), "Opened session id mismatch.");

        _ = await InvokeJsonAsync(
            http,
            HttpMethod.Post,
            $"{baseUrl}/api/v1/sessions/{Uri.EscapeDataString(sessionId)}/participants",
            ownerHeaders,
            new
            {
                participantId,
                principalId = "observer-user",
                role = "Observer",
                addedBy = "smoke-runner",
                tenantId = "local-tenant",
                organizationId = "local-org",
            });

        _ = await InvokeJsonAsync(
            http,
            HttpMethod.Post,
            $"{baseUrl}/api/v1/sessions/{Uri.EscapeDataString(sessionId)}/shares",
            ownerHeaders,
            new
            {
                shareId,
                principalId = "observer-user",
                grantedBy = "smoke-runner",
                role = "Observer",
                tenantId = "local-tenant",
                organizationId = "local-org",
            });

        _ = await InvokeJsonAsync(
            http,
            HttpMethod.Post,
            $"{baseUrl}/api/v1/sessions/{Uri.EscapeDataString(sessionId)}/invitations/requests",
            controllerHeaders,
            new
            {
                shareId = invitationShareId,
                principalId = "controller-user",
                requestedBy = "controller-user",
                requestedRole = "Controller",
                message = "Need temporary control",
                tenantId = "local-tenant",
                organizationId = "local-org",
            });

        JsonElement viewerDeniedApproval = default;
        if (requireBearer && multiActorBearerMode)
        {
            viewerDeniedApproval = await InvokeExpectedFailureAsync(
                http,
                HttpMethod.Post,
                $"{baseUrl}/api/v1/sessions/{Uri.EscapeDataString(sessionId)}/invitations/{Uri.EscapeDataString(invitationShareId)}/approve",
                controllerHeaders,
                new
                {
                    shareId = invitationShareId,
                    actedBy = "controller-user",
                    grantedRole = "Controller",
                    reason = "viewer should be denied",
                    tenantId = "local-tenant",
                    organizationId = "local-org",
                },
                expectedStatusCode: HttpStatusCode.Forbidden);
            var viewerDeniedCode = GetStringProperty(viewerDeniedApproval, "code");
            Ensure(string.Equals(viewerDeniedCode, "moderation_access_required", StringComparison.OrdinalIgnoreCase),
                $"Expected viewer denial code 'moderation_access_required', got '{viewerDeniedCode}'.");
        }

        _ = await InvokeJsonAsync(
            http,
            HttpMethod.Post,
            $"{baseUrl}/api/v1/sessions/{Uri.EscapeDataString(sessionId)}/invitations/{Uri.EscapeDataString(invitationShareId)}/approve",
            ownerHeaders,
            new
            {
                shareId = invitationShareId,
                actedBy = "smoke-runner",
                grantedRole = "Controller",
                reason = "approved by smoke runner",
                tenantId = "local-tenant",
                organizationId = "local-org",
            });

        _ = await InvokeJsonAsync(
            http,
            HttpMethod.Post,
            $"{baseUrl}/api/v1/sessions/{Uri.EscapeDataString(sessionId)}/shares/{Uri.EscapeDataString(invitationShareId)}/accept",
            controllerHeaders,
            new
            {
                shareId = invitationShareId,
                actedBy = "controller-user",
                reason = "joining approved invitation",
                tenantId = "local-tenant",
                organizationId = "local-org",
            });

        var controllerParticipantId = $"share:{invitationShareId}";
        var controlLease = await InvokeJsonAsync(
            http,
            HttpMethod.Post,
            $"{baseUrl}/api/v1/sessions/{Uri.EscapeDataString(sessionId)}/control/request",
            controllerHeaders,
            new
            {
                participantId = controllerParticipantId,
                requestedBy = "controller-user",
                leaseSeconds = 30,
                reason = "smoke control request",
                tenantId = "local-tenant",
                organizationId = "local-org",
            });
        Ensure(string.Equals(GetStringProperty(controlLease, "principalId"), "controller-user", StringComparison.OrdinalIgnoreCase),
            "Expected controller-user to hold control lease.");

        var requestControlConflict = await InvokeExpectedFailureAsync(
            http,
            HttpMethod.Post,
            $"{baseUrl}/api/v1/sessions/{Uri.EscapeDataString(sessionId)}/control/request",
            ownerHeaders,
            new
            {
                participantId = "owner:smoke-runner",
                requestedBy = "smoke-runner",
                leaseSeconds = 30,
                reason = "owner should wait for active lease",
                tenantId = "local-tenant",
                organizationId = "local-org",
            },
            expectedStatusCode: HttpStatusCode.Conflict);
        Ensure(string.Equals(GetStringProperty(requestControlConflict, "code"), "E_CONTROL_LEASE_HELD_BY_OTHER", StringComparison.OrdinalIgnoreCase),
            "Expected control request conflict code E_CONTROL_LEASE_HELD_BY_OTHER.");

        var notEligibleControlConflict = await InvokeExpectedFailureAsync(
            http,
            HttpMethod.Post,
            $"{baseUrl}/api/v1/sessions/{Uri.EscapeDataString(sessionId)}/control/grant",
            ownerHeaders,
            new
            {
                participantId,
                grantedBy = "smoke-runner",
                leaseSeconds = 30,
                reason = "observer should be ineligible",
                tenantId = "local-tenant",
                organizationId = "local-org",
            },
            expectedStatusCode: HttpStatusCode.Conflict);
        Ensure(string.Equals(GetStringProperty(notEligibleControlConflict, "code"), "E_CONTROL_NOT_ELIGIBLE", StringComparison.OrdinalIgnoreCase),
            "Expected control grant conflict code E_CONTROL_NOT_ELIGIBLE.");

        var releaseMismatchConflict = await InvokeExpectedFailureAsync(
            http,
            HttpMethod.Post,
            $"{baseUrl}/api/v1/sessions/{Uri.EscapeDataString(sessionId)}/control/release",
            ownerHeaders,
            new
            {
                participantId = "owner:smoke-runner",
                actedBy = "smoke-runner",
                reason = "release target mismatch",
                tenantId = "local-tenant",
                organizationId = "local-org",
            },
            expectedStatusCode: HttpStatusCode.Conflict);
        Ensure(string.Equals(GetStringProperty(releaseMismatchConflict, "code"), "E_CONTROL_PARTICIPANT_MISMATCH", StringComparison.OrdinalIgnoreCase),
            "Expected control release conflict code E_CONTROL_PARTICIPANT_MISMATCH.");

        var activeControl = await InvokeJsonAsync(
            http,
            HttpMethod.Get,
            $"{baseUrl}/api/v1/sessions/{Uri.EscapeDataString(sessionId)}/control",
            controllerHeaders,
            null);
        Ensure(string.Equals(GetStringProperty(activeControl, "principalId"), "controller-user", StringComparison.OrdinalIgnoreCase),
            "Expected controller-user in active control snapshot.");

        var commandAck = await InvokeJsonAsync(
            http,
            HttpMethod.Post,
            $"{baseUrl}/api/v1/sessions/{Uri.EscapeDataString(sessionId)}/commands",
            controllerHeaders,
            new
            {
                commandId,
                sessionId,
                channel = "Hid",
                action = "noop",
                args = new
                {
                    principalId = "controller-user",
                    participantId = controllerParticipantId,
                    shareId = invitationShareId,
                },
                timeoutMs = 250,
                idempotencyKey = commandId,
                tenantId = "local-tenant",
                organizationId = "local-org",
            });
        var commandStatus = GetStringProperty(commandAck, "status");
        var commandErrorCode = string.Empty;
        if (commandAck.ValueKind == JsonValueKind.Object
            && commandAck.TryGetProperty("error", out var commandError)
            && commandError.ValueKind == JsonValueKind.Object)
        {
            commandErrorCode = GetStringProperty(commandError, "code");
        }

        var commandTransportIssue =
            string.Equals(commandStatus, "Timeout", StringComparison.OrdinalIgnoreCase)
            || (string.Equals(commandStatus, "Rejected", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(commandErrorCode)
                && (commandErrorCode.StartsWith("E_TRANSPORT_", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(commandErrorCode, "E_COMMAND_EXECUTION_FAILED", StringComparison.OrdinalIgnoreCase)));

        if (!string.Equals(commandStatus, "Applied", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(commandStatus, "Accepted", StringComparison.OrdinalIgnoreCase)
            && !commandTransportIssue)
        {
            throw new InvalidOperationException(
                $"Expected command to be Applied/Accepted (or transport-level Timeout/Rejected), got '{commandStatus}' ({commandErrorCode}).");
        }

        var releasedControl = await InvokeJsonAsync(
            http,
            HttpMethod.Post,
            $"{baseUrl}/api/v1/sessions/{Uri.EscapeDataString(sessionId)}/control/release",
            controllerHeaders,
            new
            {
                participantId = controllerParticipantId,
                actedBy = "controller-user",
                reason = "smoke release",
                tenantId = "local-tenant",
                organizationId = "local-org",
            });
        Ensure(string.Equals(GetStringProperty(releasedControl, "participantId"), controllerParticipantId, StringComparison.OrdinalIgnoreCase),
            "Expected released control participant to match controller.");

        var takeoverControl = await InvokeJsonAsync(
            http,
            HttpMethod.Post,
            $"{baseUrl}/api/v1/sessions/{Uri.EscapeDataString(sessionId)}/control/force-takeover",
            ownerHeaders,
            new
            {
                participantId = "owner:smoke-runner",
                grantedBy = "smoke-runner",
                leaseSeconds = 30,
                reason = "owner takeover",
                tenantId = "local-tenant",
                organizationId = "local-org",
            });
        Ensure(string.Equals(GetStringProperty(takeoverControl, "principalId"), "smoke-runner", StringComparison.OrdinalIgnoreCase),
            "Expected owner takeover principal smoke-runner.");

        JsonElement foreignDenied = default;
        if (requireBearer && multiActorBearerMode)
        {
            foreignDenied = await InvokeExpectedFailureAsync(
                http,
                HttpMethod.Get,
                $"{baseUrl}/api/v1/collaboration/sessions/{Uri.EscapeDataString(sessionId)}/summary",
                foreignHeaders,
                null,
                expectedStatusCode: HttpStatusCode.Forbidden);
            var denialCode = GetStringProperty(foreignDenied, "code");
            if (!string.Equals(denialCode, "tenant_scope_mismatch", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Expected foreign denial code 'tenant_scope_mismatch', got '{denialCode}'.");
            }
        }

        var sessions = await InvokeJsonAsync(
            http,
            HttpMethod.Get,
            $"{baseUrl}/api/v1/sessions",
            ownerHeaders,
            null);
        var participants = await InvokeJsonAsync(
            http,
            HttpMethod.Get,
            $"{baseUrl}/api/v1/sessions/{Uri.EscapeDataString(sessionId)}/participants",
            ownerHeaders,
            null);
        var shares = await InvokeJsonAsync(
            http,
            HttpMethod.Get,
            $"{baseUrl}/api/v1/sessions/{Uri.EscapeDataString(sessionId)}/shares",
            ownerHeaders,
            null);
        var invitations = await InvokeJsonAsync(
            http,
            HttpMethod.Get,
            $"{baseUrl}/api/v1/sessions/{Uri.EscapeDataString(sessionId)}/invitations",
            ownerHeaders,
            null);
        var commandJournal = await InvokeJsonAsync(
            http,
            HttpMethod.Get,
            $"{baseUrl}/api/v1/sessions/{Uri.EscapeDataString(sessionId)}/commands/journal",
            ownerHeaders,
            null);
        var timeline = await InvokeJsonAsync(
            http,
            HttpMethod.Get,
            $"{baseUrl}/api/v1/events/timeline/{Uri.EscapeDataString(sessionId)}?take=20",
            ownerHeaders,
            null);
        var auditAfter = await InvokeJsonAsync(
            http,
            HttpMethod.Get,
            $"{baseUrl}/api/v1/events/audit",
            ownerHeaders,
            null);
        Ensure(Count(sessions) >= 1, "Expected at least one persisted session.");
        Ensure(Count(participants) >= 2, "Expected at least two participants.");
        Ensure(Count(shares) >= 2, "Expected at least two shares.");
        Ensure(Count(invitations) >= 1, "Expected at least one invitation.");
        Ensure(Count(commandJournal) >= 1, "Expected at least one command journal entry.");
        Ensure(Count(timeline) >= 1, "Expected at least one timeline entry.");
        Ensure(Count(auditAfter) > Count(auditBefore), "Expected audit stream growth after smoke actions.");

        var inventoryDashboard = await InvokeJsonAsync(
            http,
            HttpMethod.Get,
            $"{baseUrl}/api/v1/dashboards/inventory",
            ownerHeaders,
            null);
        var auditDashboard = await InvokeJsonAsync(
            http,
            HttpMethod.Get,
            $"{baseUrl}/api/v1/dashboards/audit?take=20",
            ownerHeaders,
            null);
        var telemetryDashboard = await InvokeJsonAsync(
            http,
            HttpMethod.Get,
            $"{baseUrl}/api/v1/dashboards/telemetry?take=20",
            ownerHeaders,
            null);
        var sessionProjection = await InvokeJsonAsync(
            http,
            HttpMethod.Get,
            $"{baseUrl}/api/v1/projections/sessions?principalId=smoke-runner&take=50",
            ownerHeaders,
            null);
        var endpointProjection = await InvokeJsonAsync(
            http,
            HttpMethod.Get,
            $"{baseUrl}/api/v1/projections/endpoints?take=50",
            ownerHeaders,
            null);
        var auditProjection = await InvokeJsonAsync(
            http,
            HttpMethod.Get,
            $"{baseUrl}/api/v1/projections/audit?sessionId={Uri.EscapeDataString(sessionId)}&take=20",
            ownerHeaders,
            null);
        var telemetryProjection = await InvokeJsonAsync(
            http,
            HttpMethod.Get,
            $"{baseUrl}/api/v1/projections/telemetry?sessionId={Uri.EscapeDataString(sessionId)}&scope=command&take=20",
            ownerHeaders,
            null);
        var replayBundle = await InvokeJsonAsync(
            http,
            HttpMethod.Get,
            $"{baseUrl}/api/v1/diagnostics/replay/sessions/{Uri.EscapeDataString(sessionId)}?take=20",
            ownerHeaders,
            null);
        var archiveSummary = await InvokeJsonAsync(
            http,
            HttpMethod.Get,
            $"{baseUrl}/api/v1/diagnostics/archive/summary?sessionId={Uri.EscapeDataString(sessionId)}",
            ownerHeaders,
            null);
        var archiveAudit = await InvokeJsonAsync(
            http,
            HttpMethod.Get,
            $"{baseUrl}/api/v1/diagnostics/archive/audit?sessionId={Uri.EscapeDataString(sessionId)}&take=20",
            ownerHeaders,
            null);
        var archiveTelemetry = await InvokeJsonAsync(
            http,
            HttpMethod.Get,
            $"{baseUrl}/api/v1/diagnostics/archive/telemetry?sessionId={Uri.EscapeDataString(sessionId)}&scope=command&take=20",
            ownerHeaders,
            null);
        var archiveCommands = await InvokeJsonAsync(
            http,
            HttpMethod.Get,
            $"{baseUrl}/api/v1/diagnostics/archive/commands?sessionId={Uri.EscapeDataString(sessionId)}&take=20",
            ownerHeaders,
            null);
        Ensure(GetNumberProperty(inventoryDashboard, "totalAgents") >= 1, "Expected inventory dashboard totalAgents >= 1.");
        Ensure(GetNumberProperty(auditDashboard, "totalEvents") >= 1, "Expected audit dashboard totalEvents >= 1.");
        _ = telemetryDashboard;
        Ensure(GetNumberProperty(auditProjection, "total") >= 1, "Expected audit projection total >= 1.");
        Ensure(GetNumberProperty(telemetryProjection, "total") >= 1, "Expected telemetry projection total >= 1.");
        Ensure(string.Equals(GetStringProperty(replayBundle, "sessionId"), sessionId, StringComparison.OrdinalIgnoreCase), "Replay bundle sessionId mismatch.");
        Ensure(GetNumberProperty(archiveSummary, "commandCount") >= 1, "Expected archive summary commandCount >= 1.");
        Ensure(GetNumberProperty(archiveAudit, "total") >= 1, "Expected archive audit total >= 1.");
        Ensure(GetNumberProperty(archiveTelemetry, "total") >= 1, "Expected archive telemetry total >= 1.");
        Ensure(GetNumberProperty(archiveCommands, "total") >= 1, "Expected archive commands total >= 1.");
        _ = sessionProjection;
        _ = endpointProjection;

        _ = await InvokeJsonAsync(
            http,
            HttpMethod.Post,
            $"{baseUrl}/api/v1/sessions/{Uri.EscapeDataString(sessionId)}/close",
            ownerHeaders,
            new
            {
                sessionId,
                reason = "smoke teardown",
            });

        var sessionsAfterClose = await InvokeJsonAsync(
            http,
            HttpMethod.Get,
            $"{baseUrl}/api/v1/sessions",
            ownerHeaders,
            null);
        if (sessionsAfterClose.ValueKind == JsonValueKind.Array)
        {
            var stillVisible = sessionsAfterClose.EnumerateArray()
                .Any(x => string.Equals(GetStringProperty(x, "sessionId"), sessionId, StringComparison.Ordinal));
            if (stillVisible)
            {
                throw new InvalidOperationException("Expected closed smoke session to disappear from session list.");
            }
        }

        var inventoryAfterClose = await InvokeJsonAsync(
            http,
            HttpMethod.Get,
            $"{baseUrl}/api/v1/dashboards/inventory",
            ownerHeaders,
            null);

        return JsonSerializer.SerializeToElement(new
        {
            sessionId,
            commandId,
            commandStatus,
            commandErrorCode,
            commandTransportIssue,
            provider = options.Provider,
            multiActorBearerMode,
            sessionCount = Count(sessions),
            participantCount = Count(participants),
            shareCount = Count(shares),
            invitationCount = Count(invitations),
            commandJournalCount = Count(commandJournal),
            timelineCount = Count(timeline),
            auditCount = Count(auditAfter),
            requestControlConflictCode = GetStringProperty(requestControlConflict, "code"),
            notEligibleControlConflictCode = GetStringProperty(notEligibleControlConflict, "code"),
            releaseMismatchConflictCode = GetStringProperty(releaseMismatchConflict, "code"),
            foreignDeniedCode = GetStringProperty(foreignDenied, "code"),
            viewerDeniedCode = GetStringProperty(viewerDeniedApproval, "code"),
            inventoryAfterCloseTotalAgents = GetNumberProperty(inventoryAfterClose, "totalAgents"),
        });
    }

    private static async Task<JsonElement> InvokeJsonAsync(
        HttpClient http,
        HttpMethod method,
        string uri,
        IReadOnlyDictionary<string, string> headers,
        object? body)
    {
        using var request = new HttpRequestMessage(method, uri);
        foreach (var header in headers)
        {
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (body is not null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        }

        using var response = await http.SendAsync(request);
        var payload = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"HTTP {method.Method} {uri} failed ({(int)response.StatusCode}): {payload}");
        }

        if (string.IsNullOrWhiteSpace(payload))
        {
            return JsonSerializer.SerializeToElement(new { });
        }

        using var doc = JsonDocument.Parse(payload);
        return doc.RootElement.Clone();
    }

    private static async Task<JsonElement> InvokeExpectedFailureAsync(
        HttpClient http,
        HttpMethod method,
        string uri,
        IReadOnlyDictionary<string, string> headers,
        object? body,
        HttpStatusCode expectedStatusCode)
    {
        using var request = new HttpRequestMessage(method, uri);
        foreach (var header in headers)
        {
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (body is not null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        }

        using var response = await http.SendAsync(request);
        var payload = await response.Content.ReadAsStringAsync();
        if (response.StatusCode != expectedStatusCode)
        {
            throw new InvalidOperationException(
                $"Expected {(int)expectedStatusCode} for {method.Method} {uri}, got {(int)response.StatusCode}: {payload}");
        }

        if (string.IsNullOrWhiteSpace(payload))
        {
            return JsonSerializer.SerializeToElement(new { });
        }

        using var doc = JsonDocument.Parse(payload);
        return doc.RootElement.Clone();
    }

    private static Dictionary<string, string> BuildCallerHeaders(
        string subjectId,
        string principalId,
        string roles,
        string tenantId,
        string organizationId,
        bool includeFallbackHeaders,
        string? bearerToken)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (includeFallbackHeaders)
        {
            headers["X-HidBridge-UserId"] = subjectId;
            headers["X-HidBridge-PrincipalId"] = principalId;
            headers["X-HidBridge-TenantId"] = tenantId;
            headers["X-HidBridge-OrganizationId"] = organizationId;
            headers["X-HidBridge-Role"] = roles;
        }

        if (!string.IsNullOrWhiteSpace(bearerToken))
        {
            headers["Authorization"] = $"Bearer {bearerToken}";
        }

        return headers;
    }

    private static string GetStringProperty(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(name, out var property)
            || property.ValueKind == JsonValueKind.Null)
        {
            return string.Empty;
        }

        return property.ValueKind == JsonValueKind.String
            ? (property.GetString() ?? string.Empty)
            : property.GetRawText();
    }

    private static int GetNumberProperty(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(name, out var property))
        {
            return 0;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var parsed))
        {
            return parsed;
        }

        if (property.ValueKind == JsonValueKind.String && int.TryParse(property.GetString(), out var fromString))
        {
            return fromString;
        }

        return 0;
    }

    private sealed class BearerSmokeOptions
    {
        public string Provider { get; set; } = "Sql";
        public bool ProviderExplicit { get; set; }
        public string Configuration { get; set; } = "Debug";
        public string ConnectionString { get; set; } = "Host=127.0.0.1;Port=5434;Database=hidbridge;Username=hidbridge;Password=hidbridge";
        public string Schema { get; set; } = "hidbridge";
        public string BaseUrl { get; set; } = "http://127.0.0.1:18093";
        public string DataRoot { get; set; } = string.Empty;
        public string AccessToken { get; set; } = string.Empty;
        public bool EnableApiAuth { get; set; } = true;
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
        public bool DisableHeaderFallback { get; set; } = true;
        public bool BearerOnly { get; set; } = true;
        public bool NoBuild { get; set; }

        public static bool TryParse(IReadOnlyList<string> args, out BearerSmokeOptions options, out string? error)
        {
            options = new BearerSmokeOptions();
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
                        options.ProviderExplicit = true;
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
                    case "dataroot": options.DataRoot = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "accesstoken": options.AccessToken = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "enableapiauth": options.EnableApiAuth = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "authauthority": options.AuthAuthority = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "authaudience": options.AuthAudience = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "tokenclientid": options.TokenClientId = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "tokenclientsecret": options.TokenClientSecret = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "tokenscope": options.TokenScope = RequireValue(name, value, ref i, ref hasValue, ref error); break;
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
                        error = $"Unsupported bearer-smoke option '{token}'.";
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

internal static class ArtifactExportCommand
{
    public static async Task<int> RunAsync(string platformRoot, IReadOnlyList<string> args)
    {
        if (!ArtifactExportOptions.TryParse(args, out var options, out var parseError))
        {
            Console.Error.WriteLine($"export-artifacts options error: {parseError}");
            return 1;
        }

        var outputRoot = options.OutputRoot;
        if (string.IsNullOrWhiteSpace(outputRoot))
        {
            outputRoot = Path.Combine(platformRoot, "Artifacts", DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss"));
        }

        try
        {
            var result = await ExportAsync(
                platformRoot,
                outputRoot,
                options.IncludeSmokeData,
                options.IncludeBackups,
                options.LogPath);

            Console.WriteLine("=== Artifact Export Summary ===");
            Console.WriteLine($"OutputRoot: {result.OutputRoot}");
            Console.WriteLine($"CopiedCount: {result.CopiedPaths.Count}");
            foreach (var path in result.CopiedPaths)
            {
                Console.WriteLine(path);
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Artifact export failed: {ex.Message}");
            return 1;
        }
    }

    public static async Task<ArtifactExportResult> ExportAsync(
        string platformRoot,
        string outputRoot,
        bool includeSmokeData,
        bool includeBackups,
        string? logPath = null)
    {
        Directory.CreateDirectory(outputRoot);

        var copied = new List<string>();
        var targets = new List<(string Source, string Destination)>
        {
            (Path.Combine(platformRoot, ".logs"), Path.Combine(outputRoot, ".logs")),
        };

        if (includeSmokeData)
        {
            targets.Add((Path.Combine(platformRoot, ".smoke-data"), Path.Combine(outputRoot, ".smoke-data")));
        }

        if (includeBackups)
        {
            targets.Add((Path.Combine(platformRoot, "Identity", "Keycloak", "backups"), Path.Combine(outputRoot, "keycloak-backups")));
        }

        foreach (var target in targets)
        {
            if (!Directory.Exists(target.Source))
            {
                continue;
            }

            CopyDirectory(target.Source, target.Destination);
            copied.Add(target.Destination);
        }

        if (!string.IsNullOrWhiteSpace(logPath))
        {
            var lines = new List<string>
            {
                "=== Artifact Export Summary ===",
                $"OutputRoot: {outputRoot}",
                $"CopiedCount: {copied.Count}",
            };
            lines.AddRange(copied);
            await File.WriteAllLinesAsync(logPath, lines);
        }

        return new ArtifactExportResult(outputRoot, copied);
    }

    private static void CopyDirectory(string sourceDir, string destinationDir)
    {
        var source = new DirectoryInfo(sourceDir);
        if (!source.Exists)
        {
            return;
        }

        Directory.CreateDirectory(destinationDir);

        foreach (var file in source.GetFiles())
        {
            var destinationFile = Path.Combine(destinationDir, file.Name);
            file.CopyTo(destinationFile, overwrite: true);
        }

        foreach (var directory in source.GetDirectories())
        {
            CopyDirectory(directory.FullName, Path.Combine(destinationDir, directory.Name));
        }
    }

    public sealed record ArtifactExportResult(string OutputRoot, IReadOnlyList<string> CopiedPaths);

    private sealed class ArtifactExportOptions
    {
        public string OutputRoot { get; set; } = string.Empty;
        public bool IncludeSmokeData { get; set; }
        public bool IncludeBackups { get; set; }
        public string? LogPath { get; set; }

        public static bool TryParse(IReadOnlyList<string> args, out ArtifactExportOptions options, out string? error)
        {
            options = new ArtifactExportOptions();
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
                    case "outputroot": options.OutputRoot = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "includesmokedata": options.IncludeSmokeData = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "includebackups": options.IncludeBackups = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "logpath": options.LogPath = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    default:
                        error = $"Unsupported export-artifacts option '{token}'.";
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

internal static class CleanLogsCommand
{
    public static Task<int> RunAsync(string platformRoot, IReadOnlyList<string> args)
    {
        if (!CleanLogsOptions.TryParse(args, out var options, out var parseError))
        {
            Console.Error.WriteLine($"clean-logs options error: {parseError}");
            return Task.FromResult(1);
        }

        var targets = new List<string>
        {
            Path.Combine(platformRoot, ".logs"),
        };
        if (options.IncludeSmokeData)
        {
            targets.Add(Path.Combine(platformRoot, ".smoke-data"));
        }

        var cutoff = DateTimeOffset.UtcNow.AddDays(-1 * Math.Abs(options.KeepDays));
        var removed = new List<string>();

        foreach (var target in targets)
        {
            if (!Directory.Exists(target))
            {
                continue;
            }

            var directories = Directory.EnumerateDirectories(target, "*", SearchOption.AllDirectories)
                .OrderByDescending(static x => x.Length)
                .ThenByDescending(static x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            foreach (var directory in directories)
            {
                var info = new DirectoryInfo(directory);
                var lastWriteUtc = new DateTimeOffset(info.LastWriteTimeUtc);
                if (lastWriteUtc > cutoff)
                {
                    continue;
                }

                removed.Add(directory);
                if (!options.WhatIf)
                {
                    try
                    {
                        Directory.Delete(directory, recursive: true);
                    }
                    catch
                    {
                        // best effort for cleanup mode
                    }
                }
            }
        }

        Console.WriteLine("=== Cleanup Summary ===");
        Console.WriteLine($"KeepDays: {options.KeepDays}");
        Console.WriteLine($"IncludeSmokeData: {options.IncludeSmokeData}");
        Console.WriteLine($"RemovedCount: {removed.Count}");
        if (removed.Count > 0)
        {
            Console.WriteLine("Removed paths:");
            foreach (var item in removed)
            {
                Console.WriteLine(item);
            }
        }

        return Task.FromResult(0);
    }

    private sealed class CleanLogsOptions
    {
        public int KeepDays { get; set; } = 7;
        public bool IncludeSmokeData { get; set; }
        public bool WhatIf { get; set; }

        public static bool TryParse(IReadOnlyList<string> args, out CleanLogsOptions options, out string? error)
        {
            options = new CleanLogsOptions();
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
                    case "keepdays":
                        options.KeepDays = ParseInt(name, value, hasValue, ref i, ref error);
                        break;
                    case "includesmokedata":
                        options.IncludeSmokeData = ParseSwitch(name, value, hasValue, ref i, ref error);
                        break;
                    case "whatif":
                        options.WhatIf = ParseSwitch(name, value, hasValue, ref i, ref error);
                        break;
                    default:
                        error = $"Unsupported clean-logs option '{token}'.";
                        return false;
                }

                if (error is not null)
                {
                    return false;
                }
            }

            return true;
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

internal static class TokenDebugCommand
{
    public static async Task<int> RunAsync(string platformRoot, IReadOnlyList<string> args)
    {
        if (!TokenDebugOptions.TryParse(args, out var options, out var parseError))
        {
            Console.Error.WriteLine($"token-debug options error: {parseError}");
            return 1;
        }

        var logRoot = Path.Combine(platformRoot, ".logs", "token-debug", DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(logRoot);
        var summaryPath = Path.Combine(logRoot, "token.summary.txt");
        var headerPath = Path.Combine(logRoot, "token.header.json");
        var payloadPath = Path.Combine(logRoot, "token.payload.json");
        var rawPath = Path.Combine(logRoot, "token.raw.txt");
        var rawResponsePath = Path.Combine(logRoot, "token.response.json");
        var idTokenHeaderPath = Path.Combine(logRoot, "id_token.header.json");
        var idTokenPayloadPath = Path.Combine(logRoot, "id_token.payload.json");
        var idTokenRawPath = Path.Combine(logRoot, "id_token.raw.txt");
        var userinfoPath = Path.Combine(logRoot, "userinfo.json");

        try
        {
            var source = "provided-token";
            var accessToken = options.AccessToken;
            JsonDocument? tokenResponse = null;

            if (string.IsNullOrWhiteSpace(accessToken)
                || string.Equals(accessToken, "auto", StringComparison.OrdinalIgnoreCase)
                || string.Equals(accessToken, "<token>", StringComparison.OrdinalIgnoreCase))
            {
                tokenResponse = await RequestTokenResponseAsync(options);
                if (!tokenResponse.RootElement.TryGetProperty("access_token", out var accessTokenElement))
                {
                    throw new InvalidOperationException("OIDC token response did not contain access_token.");
                }
                accessToken = accessTokenElement.GetString() ?? string.Empty;
                source = "oidc-password-grant";
                await File.WriteAllTextAsync(rawResponsePath, JsonSerializer.Serialize(tokenResponse.RootElement, new JsonSerializerOptions { WriteIndented = true }));
            }

            var parts = accessToken.Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                throw new InvalidOperationException("Access token is not a JWT.");
            }

            var headerJson = DecodeJwtSegment(parts[0]);
            var payloadJson = DecodeJwtSegment(parts[1]);
            await File.WriteAllTextAsync(rawPath, accessToken);
            await File.WriteAllTextAsync(headerPath, headerJson);
            await File.WriteAllTextAsync(payloadPath, payloadJson);

            using var headerDoc = JsonDocument.Parse(headerJson);
            using var payloadDoc = JsonDocument.Parse(payloadJson);

            string? idTokenSubject = null;
            if (string.Equals(source, "oidc-password-grant", StringComparison.OrdinalIgnoreCase)
                && tokenResponse is not null
                && tokenResponse.RootElement.TryGetProperty("id_token", out var idTokenElement)
                && !string.IsNullOrWhiteSpace(idTokenElement.GetString()))
            {
                var idToken = idTokenElement.GetString() ?? string.Empty;
                var idParts = idToken.Split('.', StringSplitOptions.RemoveEmptyEntries);
                if (idParts.Length >= 2)
                {
                    var idHeader = DecodeJwtSegment(idParts[0]);
                    var idPayload = DecodeJwtSegment(idParts[1]);
                    await File.WriteAllTextAsync(idTokenRawPath, idToken);
                    await File.WriteAllTextAsync(idTokenHeaderPath, idHeader);
                    await File.WriteAllTextAsync(idTokenPayloadPath, idPayload);

                    using var idPayloadDoc = JsonDocument.Parse(idPayload);
                    idTokenSubject = GetStringClaim(idPayloadDoc.RootElement, "sub");
                }
            }

            JsonDocument? userinfoDoc = null;
            if (string.Equals(source, "oidc-password-grant", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    userinfoDoc = await RequestUserInfoAsync(options, accessToken);
                    await File.WriteAllTextAsync(userinfoPath, JsonSerializer.Serialize(userinfoDoc.RootElement, new JsonSerializerOptions { WriteIndented = true }));
                }
                catch (Exception ex)
                {
                    await File.WriteAllTextAsync(userinfoPath, ex.Message);
                }
            }

            var roles = GetRoles(payloadDoc.RootElement);
            var preferredUsername = GetStringClaim(payloadDoc.RootElement, "preferred_username");
            var tenantId = GetStringClaim(payloadDoc.RootElement, "tenant_id");
            var organizationId = GetStringClaim(payloadDoc.RootElement, "org_id");
            var callerContextReady = !string.IsNullOrWhiteSpace(preferredUsername)
                                     && !string.IsNullOrWhiteSpace(tenantId)
                                     && !string.IsNullOrWhiteSpace(organizationId)
                                     && roles.Any(static x => x.StartsWith("operator.", StringComparison.Ordinal));

            var userinfoRoot = userinfoDoc?.RootElement;
            var issuedAt = GetLongClaim(payloadDoc.RootElement, "iat");
            var expiresAt = GetLongClaim(payloadDoc.RootElement, "exp");

            var summary = new List<string>
            {
                "=== Token Debug Summary ===",
                $"Realm: {options.Realm}",
                $"ClientId: {options.ClientId}",
                $"Username: {options.Username}",
                $"Source: {source}",
                $"Subject: {GetStringClaim(payloadDoc.RootElement, "sub")}",
                $"PreferredUsername: {preferredUsername}",
                $"Email: {GetStringClaim(payloadDoc.RootElement, "email")}",
                $"TenantId: {tenantId}",
                $"OrganizationId: {organizationId}",
                $"IdTokenSubject: {idTokenSubject}",
                $"UserInfoSubject: {GetStringClaim(userinfoRoot, "sub")}",
                $"UserInfoPreferredUsername: {GetStringClaim(userinfoRoot, "preferred_username")}",
                $"UserInfoEmail: {GetStringClaim(userinfoRoot, "email")}",
                $"UserInfoTenantId: {GetStringClaim(userinfoRoot, "tenant_id")}",
                $"UserInfoOrganizationId: {GetStringClaim(userinfoRoot, "org_id")}",
                $"CallerContextReady: {(callerContextReady ? "true" : "false")}",
                $"Audience: {string.Join(", ", GetAudience(payloadDoc.RootElement))}",
                $"IssuedAtUtc: {FormatUnix(issuedAt)}",
                $"ExpiresAtUtc: {FormatUnix(expiresAt)}",
                $"Roles: {string.Join(", ", roles)}",
                $"RawTokenResponse: {rawResponsePath}",
                $"HeaderJson: {headerPath}",
                $"PayloadJson: {payloadPath}",
                $"IdTokenHeaderJson: {idTokenHeaderPath}",
                $"IdTokenPayloadJson: {idTokenPayloadPath}",
                $"IdTokenRaw: {idTokenRawPath}",
                $"UserInfoJson: {userinfoPath}",
                $"RawToken: {rawPath}",
            };

            await File.WriteAllLinesAsync(summaryPath, summary);
            foreach (var line in summary)
            {
                Console.WriteLine(line);
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static string DecodeJwtSegment(string segment)
    {
        var padded = segment.Replace('-', '+').Replace('_', '/');
        var remainder = padded.Length % 4;
        if (remainder == 2)
        {
            padded += "==";
        }
        else if (remainder == 3)
        {
            padded += "=";
        }
        var bytes = Convert.FromBase64String(padded);
        using var doc = JsonDocument.Parse(bytes);
        return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string? GetStringClaim(JsonElement? element, string name)
    {
        if (element is null || element.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        if (!element.Value.TryGetProperty(name, out var claim))
        {
            return null;
        }
        return claim.ValueKind switch
        {
            JsonValueKind.String => claim.GetString(),
            JsonValueKind.Number => claim.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => claim.GetRawText(),
        };
    }

    private static long? GetLongClaim(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var claim))
        {
            return null;
        }
        if (claim.ValueKind == JsonValueKind.Number && claim.TryGetInt64(out var value))
        {
            return value;
        }
        if (claim.ValueKind == JsonValueKind.String && long.TryParse(claim.GetString(), out var parsed))
        {
            return parsed;
        }
        return null;
    }

    private static IReadOnlyList<string> GetRoles(JsonElement payload)
    {
        var roles = new HashSet<string>(StringComparer.Ordinal);
        if (payload.TryGetProperty("realm_access", out var realmAccess)
            && realmAccess.ValueKind == JsonValueKind.Object
            && realmAccess.TryGetProperty("roles", out var roleArray)
            && roleArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in roleArray.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                {
                    roles.Add(item.GetString()!);
                }
            }
        }

        if (payload.TryGetProperty("role", out var topLevelRoles))
        {
            if (topLevelRoles.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(topLevelRoles.GetString()))
            {
                roles.Add(topLevelRoles.GetString()!);
            }
            else if (topLevelRoles.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in topLevelRoles.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                    {
                        roles.Add(item.GetString()!);
                    }
                }
            }
        }

        return roles.ToArray();
    }

    private static IReadOnlyList<string> GetAudience(JsonElement payload)
    {
        if (!payload.TryGetProperty("aud", out var aud))
        {
            return Array.Empty<string>();
        }
        if (aud.ValueKind == JsonValueKind.String)
        {
            var value = aud.GetString();
            return string.IsNullOrWhiteSpace(value) ? Array.Empty<string>() : new[] { value };
        }
        if (aud.ValueKind == JsonValueKind.Array)
        {
            return aud.EnumerateArray()
                .Where(static x => x.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(x.GetString()))
                .Select(static x => x.GetString()!)
                .ToArray();
        }
        return Array.Empty<string>();
    }

    private static string FormatUnix(long? unixSeconds)
    {
        if (!unixSeconds.HasValue)
        {
            return string.Empty;
        }
        return DateTimeOffset.FromUnixTimeSeconds(unixSeconds.Value).UtcDateTime.ToString("O");
    }

    private static async Task<JsonDocument> RequestTokenResponseAsync(TokenDebugOptions options)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var scopeCandidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(options.Scope))
        {
            scopeCandidates.Add(options.Scope);
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
        foreach (var scope in scopeCandidates)
        {
            var form = new Dictionary<string, string>
            {
                ["grant_type"] = "password",
                ["client_id"] = options.ClientId,
                ["username"] = options.Username,
                ["password"] = options.Password,
            };
            if (!string.IsNullOrWhiteSpace(options.ClientSecret))
            {
                form["client_secret"] = options.ClientSecret;
            }
            if (!string.IsNullOrWhiteSpace(scope))
            {
                form["scope"] = scope;
            }

            using var response = await client.PostAsync(
                $"{options.KeycloakBaseUrl.TrimEnd('/')}/realms/{Uri.EscapeDataString(options.Realm)}/protocol/openid-connect/token",
                new FormUrlEncodedContent(form));
            var payload = await response.Content.ReadAsStringAsync();
            if (response.IsSuccessStatusCode)
            {
                return JsonDocument.Parse(payload);
            }

            lastError = $"Token request failed ({(int)response.StatusCode}): {payload}";
            var invalidScope = response.StatusCode == HttpStatusCode.BadRequest
                               && payload.Contains("invalid_scope", StringComparison.OrdinalIgnoreCase);
            var hasMore = !string.Equals(scope, scopeCandidates[^1], StringComparison.Ordinal);
            if (invalidScope && hasMore)
            {
                continue;
            }

            throw new InvalidOperationException(lastError);
        }

        throw new InvalidOperationException(lastError ?? "Token request failed.");
    }

    private static async Task<JsonDocument> RequestUserInfoAsync(TokenDebugOptions options, string accessToken)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{options.KeycloakBaseUrl.TrimEnd('/')}/realms/{Uri.EscapeDataString(options.Realm)}/protocol/openid-connect/userinfo");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await client.SendAsync(request);
        var payload = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"UserInfo request failed ({(int)response.StatusCode}): {payload}");
        }
        return JsonDocument.Parse(payload);
    }

    private sealed class TokenDebugOptions
    {
        public string KeycloakBaseUrl { get; set; } = "http://127.0.0.1:18096";
        public string Realm { get; set; } = "hidbridge-dev";
        public string ClientId { get; set; } = "controlplane-smoke";
        public string ClientSecret { get; set; } = string.Empty;
        public string Username { get; set; } = "operator.smoke.admin";
        public string Password { get; set; } = "ChangeMe123!";
        public string Scope { get; set; } = "openid profile email";
        public string AccessToken { get; set; } = "auto";

        public static bool TryParse(IReadOnlyList<string> args, out TokenDebugOptions options, out string? error)
        {
            options = new TokenDebugOptions();
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
                    case "keycloakbaseurl": options.KeycloakBaseUrl = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "realm": options.Realm = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "clientid": options.ClientId = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "clientsecret": options.ClientSecret = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "username": options.Username = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "password": options.Password = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "scope": options.Scope = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "accesstoken": options.AccessToken = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    default:
                        error = $"Unsupported token-debug option '{token}'.";
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
    }
}

internal static class CloseFailedRoomsCommand
{
    public static Task<int> RunAsync(string platformRoot, IReadOnlyList<string> args) =>
        RoomCleanupCommand.RunAsync(
            args,
            new RoomCleanupCommand.Mode(
                SummaryTitle: "Close Failed Rooms",
                FoundLabel: "Failed sessions found",
                EndpointPath: "/api/v1/sessions/actions/close-failed",
                DefaultPrincipalId: "failed-room-cleanup",
                DefaultReason: "manual failed-room cleanup",
                SupportsStaleAfterMinutes: false));
}

internal static class CloseStaleRoomsCommand
{
    public static Task<int> RunAsync(string platformRoot, IReadOnlyList<string> args) =>
        RoomCleanupCommand.RunAsync(
            args,
            new RoomCleanupCommand.Mode(
                SummaryTitle: "Close Stale Rooms",
                FoundLabel: "Stale sessions found",
                EndpointPath: "/api/v1/sessions/actions/close-stale",
                DefaultPrincipalId: "stale-room-cleanup",
                DefaultReason: "manual stale-room cleanup",
                SupportsStaleAfterMinutes: true));
}

internal static class RoomCleanupCommand
{
    internal sealed record Mode(
        string SummaryTitle,
        string FoundLabel,
        string EndpointPath,
        string DefaultPrincipalId,
        string DefaultReason,
        bool SupportsStaleAfterMinutes);

    public static async Task<int> RunAsync(IReadOnlyList<string> args, Mode mode)
    {
        if (!CleanupOptions.TryParse(args, mode, out var options, out var parseError))
        {
            Console.Error.WriteLine($"{mode.EndpointPath} options error: {parseError}");
            return 1;
        }

        try
        {
            var token = options.AccessToken;
            if (string.IsNullOrWhiteSpace(token))
            {
                token = await RequestTokenAsync(options);
            }

            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{options.BaseUrl.TrimEnd('/')}{mode.EndpointPath}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Add("X-HidBridge-UserId", options.PrincipalId);
            request.Headers.Add("X-HidBridge-PrincipalId", options.PrincipalId);
            request.Headers.Add("X-HidBridge-TenantId", options.TenantId);
            request.Headers.Add("X-HidBridge-OrganizationId", options.OrganizationId);
            request.Headers.Add("X-HidBridge-Role", "operator.admin,operator.moderator,operator.viewer");

            var payload = new Dictionary<string, object?>
            {
                ["dryRun"] = options.DryRun,
                ["reason"] = options.Reason,
            };
            if (mode.SupportsStaleAfterMinutes)
            {
                payload["staleAfterMinutes"] = options.StaleAfterMinutes;
            }

            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(request);
            var responseBody = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"HTTP POST {mode.EndpointPath} failed ({(int)response.StatusCode}): {responseBody}");
            }

            using var resultDoc = JsonDocument.Parse(responseBody);
            var root = resultDoc.RootElement;
            var scanned = GetInt(root, "scannedSessions");
            var matched = GetInt(root, "matchedSessions");
            var closed = GetInt(root, "closedSessions");
            var skipped = GetInt(root, "skippedSessions");
            var failedClosures = GetInt(root, "failedClosures");
            var matchedIds = GetStringArray(root, "matchedSessionIds");
            var closedIds = GetStringArray(root, "closedSessionIds");
            var errors = GetStringArray(root, "errors");

            Console.WriteLine();
            Console.WriteLine($"=== {mode.SummaryTitle} Summary ===");
            Console.WriteLine($"BaseUrl: {options.BaseUrl}");
            if (mode.SupportsStaleAfterMinutes)
            {
                Console.WriteLine($"Stale after minutes: {options.StaleAfterMinutes}");
            }
            Console.WriteLine($"Sessions visible: {scanned}");
            Console.WriteLine($"{mode.FoundLabel}: {matched}");
            Console.WriteLine($"Dry run: {options.DryRun}");
            Console.WriteLine($"Closed sessions: {closed}");
            Console.WriteLine($"Skipped sessions: {skipped}");
            Console.WriteLine($"Close failures: {failedClosures}");
            if (matchedIds.Count > 0)
            {
                Console.WriteLine($"{mode.FoundLabel.Replace(" found", " IDs")}: {string.Join(", ", matchedIds)}");
            }
            if (closedIds.Count > 0)
            {
                Console.WriteLine($"Closed session IDs: {string.Join(", ", closedIds)}");
            }
            if (errors.Count > 0)
            {
                Console.WriteLine("Close errors:");
                foreach (var error in errors)
                {
                    Console.WriteLine($"  - {error}");
                }
            }

            return errors.Count > 0 ? 1 : 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static int GetInt(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            return 0;
        }
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var numeric))
        {
            return numeric;
        }
        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out var parsed))
        {
            return parsed;
        }
        return 0;
    }

    private static List<string> GetStringArray(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            return new List<string>();
        }
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            return string.IsNullOrWhiteSpace(text) ? new List<string>() : new List<string> { text };
        }
        if (value.ValueKind != JsonValueKind.Array)
        {
            return new List<string>();
        }
        return value.EnumerateArray()
            .Where(static x => x.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(x.GetString()))
            .Select(static x => x.GetString()!)
            .ToList();
    }

    private static async Task<string> RequestTokenAsync(CleanupOptions options)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
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

        string? lastError = null;
        foreach (var scope in scopeCandidates)
        {
            var form = new Dictionary<string, string>
            {
                ["grant_type"] = "password",
                ["client_id"] = options.TokenClientId,
                ["username"] = options.TokenUsername,
                ["password"] = options.TokenPassword,
            };
            if (!string.IsNullOrWhiteSpace(options.TokenClientSecret))
            {
                form["client_secret"] = options.TokenClientSecret;
            }
            if (!string.IsNullOrWhiteSpace(scope))
            {
                form["scope"] = scope;
            }

            using var response = await client.PostAsync(
                $"{options.KeycloakBaseUrl.TrimEnd('/')}/realms/{Uri.EscapeDataString(options.RealmName)}/protocol/openid-connect/token",
                new FormUrlEncodedContent(form));
            var payload = await response.Content.ReadAsStringAsync();
            if (response.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(payload);
                if (!doc.RootElement.TryGetProperty("access_token", out var token))
                {
                    throw new InvalidOperationException("OIDC token response did not contain access_token.");
                }
                return token.GetString() ?? string.Empty;
            }

            lastError = $"Token request failed ({(int)response.StatusCode}): {payload}";
            var invalidScope = response.StatusCode == HttpStatusCode.BadRequest
                               && payload.Contains("invalid_scope", StringComparison.OrdinalIgnoreCase);
            var hasMore = !string.Equals(scope, scopeCandidates[^1], StringComparison.Ordinal);
            if (invalidScope && hasMore)
            {
                continue;
            }
            throw new InvalidOperationException(lastError);
        }

        throw new InvalidOperationException(lastError ?? "Token request failed.");
    }

    private sealed class CleanupOptions
    {
        public string BaseUrl { get; set; } = "http://127.0.0.1:18093";
        public string KeycloakBaseUrl { get; set; } = "http://127.0.0.1:18096";
        public string RealmName { get; set; } = "hidbridge-dev";
        public string TokenClientId { get; set; } = "controlplane-smoke";
        public string TokenClientSecret { get; set; } = string.Empty;
        public string TokenScope { get; set; } = "openid profile email";
        public string TokenUsername { get; set; } = "operator.smoke.admin";
        public string TokenPassword { get; set; } = "ChangeMe123!";
        public string AccessToken { get; set; } = string.Empty;
        public string PrincipalId { get; set; } = string.Empty;
        public string TenantId { get; set; } = "local-tenant";
        public string OrganizationId { get; set; } = "local-org";
        public string Reason { get; set; } = string.Empty;
        public int StaleAfterMinutes { get; set; } = 30;
        public bool DryRun { get; set; }

        public static bool TryParse(IReadOnlyList<string> args, Mode mode, out CleanupOptions options, out string? error)
        {
            options = new CleanupOptions
            {
                PrincipalId = mode.DefaultPrincipalId,
                Reason = mode.DefaultReason,
            };
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
                    case "baseurl": options.BaseUrl = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "keycloakbaseurl": options.KeycloakBaseUrl = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "realmname": options.RealmName = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "tokenclientid": options.TokenClientId = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "tokenclientsecret": options.TokenClientSecret = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "tokenscope": options.TokenScope = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "tokenusername": options.TokenUsername = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "tokenpassword": options.TokenPassword = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "accesstoken": options.AccessToken = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "principalid": options.PrincipalId = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "tenantid": options.TenantId = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "organizationid": options.OrganizationId = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "reason": options.Reason = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "dryrun": options.DryRun = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "staleafterminutes":
                        if (!mode.SupportsStaleAfterMinutes)
                        {
                            if (hasValue)
                            {
                                i++;
                            }
                            break;
                        }
                        options.StaleAfterMinutes = ParseInt(name, value, hasValue, ref i, ref error);
                        break;
                    default:
                        error = $"Unsupported cleanup option '{token}'.";
                        return false;
                }

                if (error is not null)
                {
                    return false;
                }
            }

            if (mode.SupportsStaleAfterMinutes && options.StaleAfterMinutes < 1)
            {
                error = "StaleAfterMinutes must be greater than 0.";
                return false;
            }

            return true;
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

internal static class WebRtcStackCommand
{
    public static async Task<int> RunAsync(string platformRoot, IReadOnlyList<string> args)
    {
        if (!WebRtcStackOptions.TryParse(args, out var options, out var parseError))
        {
            Console.Error.WriteLine($"webrtc-stack options error: {parseError}");
            return 1;
        }

        if (!string.Equals(options.CommandExecutor, "uart", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(options.CommandExecutor, "controlws", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("CommandExecutor must be 'uart' or 'controlws'.");
            return 1;
        }

        if (string.Equals(options.CommandExecutor, "controlws", StringComparison.OrdinalIgnoreCase))
        {
            if (!options.AllowLegacyControlWs)
            {
                Console.Error.WriteLine("CommandExecutor 'controlws' is legacy compatibility mode. Use 'uart' or pass -AllowLegacyControlWs.");
                return 1;
            }

            if (!TestLegacyExp022Enabled())
            {
                Console.Error.WriteLine("run_webrtc_stack controlws mode is disabled. Set HIDBRIDGE_ENABLE_LEGACY_EXP022=true in your shell to run exp-022/controlws lab flows.");
                return 1;
            }
        }

        if (options.PeerReadyTimeoutSpecified)
        {
            Console.WriteLine("WARNING: PeerReadyTimeoutSec is deprecated for webrtc-stack and ignored.");
        }
        if (options.TokenScopeSpecified && !string.IsNullOrWhiteSpace(options.TokenScope))
        {
            Console.WriteLine("WARNING: TokenScope is deprecated for webrtc-stack and ignored.");
        }
        if (options.AdapterDurationSecSpecified)
        {
            Console.WriteLine("WARNING: AdapterDurationSec is deprecated for webrtc-stack and ignored.");
        }

        var scriptsRoot = Path.Combine(platformRoot, "Scripts");
        var repoRoot = Directory.GetParent(platformRoot)?.FullName ?? platformRoot;
        var logRoot = Path.Combine(platformRoot, ".logs", "webrtc-stack", DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(logRoot);

        var sessionId = $"room-webrtc-peer-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
        var peerId = string.Equals(options.CommandExecutor, "uart", StringComparison.OrdinalIgnoreCase)
            ? "peer-local-uart-edge"
            : "peer-local-exp022";

        var sessionEnvPath = Path.Combine(platformRoot, ".logs", "webrtc-peer-adapter.session.env");
        var stackSummaryPath = Path.Combine(logRoot, "webrtc-stack.summary.json");
        var edgeAgentProject = Path.Combine(repoRoot, "Platform", "Edge", "HidBridge.EdgeProxy.Agent", "HidBridge.EdgeProxy.Agent.csproj");
        var exp022Script = Path.Combine(repoRoot, "WebRtcTests", "exp-022-datachanneldotnet", "start.ps1");
        var exp022Stdout = Path.Combine(logRoot, "exp022.stdout.log");
        var exp022Stderr = Path.Combine(logRoot, "exp022.stderr.log");
        var adapterStdout = Path.Combine(logRoot, "webrtc-peer-adapter.stdout.log");
        var adapterStderr = Path.Combine(logRoot, "webrtc-peer-adapter.stderr.log");

        if (options.StopExisting)
        {
            await StopExistingProcessesAsync();
        }

        if (!options.SkipRuntimeBootstrap)
        {
            var demoFlowArgs = new List<string>
            {
                "-ApiBaseUrl", options.ApiBaseUrl,
                "-WebBaseUrl", options.WebBaseUrl,
                "-SkipDoctor",
                "-SkipDemoGate",
                "-IncludeWebRtcEdgeAgentSmoke",
                "-WebRtcCommandExecutor", options.CommandExecutor,
            };

            if (options.AllowLegacyControlWs)
            {
                demoFlowArgs.Add("-AllowLegacyControlWs");
            }
            if (options.SkipIdentityReset)
            {
                demoFlowArgs.Add("-SkipIdentityReset");
            }
            if (options.SkipCiLocal)
            {
                demoFlowArgs.Add("-SkipCiLocal");
            }
            if (string.Equals(options.CommandExecutor, "controlws", StringComparison.OrdinalIgnoreCase))
            {
                demoFlowArgs.Add("-WebRtcControlHealthUrl");
                demoFlowArgs.Add(ConvertControlWsToHealthUrl(options.ControlWsUrl));
            }

            var demoExitCode = await DemoFlowCommand.RunAsync(platformRoot, demoFlowArgs);
            if (demoExitCode != 0)
            {
                Console.Error.WriteLine($"demo-flow failed with exit code {demoExitCode}.");
                return demoExitCode;
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(sessionEnvPath) ?? platformRoot);
        var sessionEnv = string.Join(
            Environment.NewLine,
            [
                $"SESSION_ID={sessionId}",
                $"PEER_ID={peerId}",
                $"ENDPOINT_ID={options.EndpointId}",
                $"PRINCIPAL_ID={options.PrincipalId}",
                "TENANT_ID=local-tenant",
                "ORGANIZATION_ID=local-org",
                $"COMMAND_EXECUTOR={options.CommandExecutor}",
                string.Empty,
            ]);
        await File.WriteAllTextAsync(sessionEnvPath, sessionEnv, Encoding.ASCII);

        EnsureFile(exp022Stdout);
        EnsureFile(exp022Stderr);
        EnsureFile(adapterStdout);
        EnsureFile(adapterStderr);

        Process? exp022Process = null;
        Process? adapterProcess = null;

        if (string.Equals(options.CommandExecutor, "controlws", StringComparison.OrdinalIgnoreCase))
        {
            if (!File.Exists(exp022Script))
            {
                Console.Error.WriteLine($"Legacy exp-022 script not found: {exp022Script}");
                return 1;
            }

            var wsUri = new Uri(options.ControlWsUrl);
            var wsBind = string.Equals(wsUri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
                ? "127.0.0.1"
                : wsUri.Host;

            var (shell, shellPrefix) = ResolvePowerShellExecutable();
            var exp022Start = new ProcessStartInfo
            {
                FileName = shell,
                WorkingDirectory = repoRoot,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var item in shellPrefix)
            {
                exp022Start.ArgumentList.Add(item);
            }
            exp022Start.ArgumentList.Add("-File");
            exp022Start.ArgumentList.Add(exp022Script);
            exp022Start.ArgumentList.Add("-Mode");
            exp022Start.ArgumentList.Add("dc-hid-poc");
            exp022Start.ArgumentList.Add("-UartPort");
            exp022Start.ArgumentList.Add(options.UartPort);
            exp022Start.ArgumentList.Add("-UartBaud");
            exp022Start.ArgumentList.Add(options.UartBaud.ToString());
            exp022Start.ArgumentList.Add("-UartHmacKey");
            exp022Start.ArgumentList.Add(options.UartHmacKey);
            exp022Start.ArgumentList.Add("-ControlWsBind");
            exp022Start.ArgumentList.Add(wsBind);
            exp022Start.ArgumentList.Add("-ControlWsPort");
            exp022Start.ArgumentList.Add(wsUri.Port.ToString());
            exp022Start.ArgumentList.Add("-DurationSec");
            exp022Start.ArgumentList.Add(options.Exp022DurationSec.ToString());

            exp022Process = StartRedirectedProcess(exp022Start, exp022Stdout, exp022Stderr);
        }

        if (!File.Exists(edgeAgentProject))
        {
            Console.Error.WriteLine($"EdgeProxy.Agent project not found: {edgeAgentProject}");
            return 1;
        }

        var adapterStart = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = repoRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        adapterStart.ArgumentList.Add("run");
        adapterStart.ArgumentList.Add("--project");
        adapterStart.ArgumentList.Add(edgeAgentProject);
        adapterStart.ArgumentList.Add("-c");
        adapterStart.ArgumentList.Add("Debug");

        adapterStart.Environment["HIDBRIDGE_EDGE_PROXY_BASEURL"] = options.ApiBaseUrl;
        adapterStart.Environment["HIDBRIDGE_EDGE_PROXY_SESSIONID"] = sessionId;
        adapterStart.Environment["HIDBRIDGE_EDGE_PROXY_PEERID"] = peerId;
        adapterStart.Environment["HIDBRIDGE_EDGE_PROXY_ENDPOINTID"] = options.EndpointId;
        adapterStart.Environment["HIDBRIDGE_EDGE_PROXY_CONTROLWSURL"] = options.ControlWsUrl;
        adapterStart.Environment["HIDBRIDGE_EDGE_PROXY_PRINCIPALID"] = options.PrincipalId;
        adapterStart.Environment["HIDBRIDGE_EDGE_PROXY_TOKENUSERNAME"] = options.TokenUsername;
        adapterStart.Environment["HIDBRIDGE_EDGE_PROXY_TOKENPASSWORD"] = options.TokenPassword;
        adapterStart.Environment["HIDBRIDGE_EDGE_PROXY_TOKENSCOPE"] = "openid";
        adapterStart.Environment["HIDBRIDGE_EDGE_PROXY_OPERATORROLESCSV"] = "operator.admin,operator.moderator,operator.viewer,operator.edge";
        adapterStart.Environment["HIDBRIDGE_EDGE_PROXY_KEYCLOAKBASEURL"] = options.KeycloakBaseUrl;
        adapterStart.Environment["HIDBRIDGE_EDGE_PROXY_KEYCLOAKREALM"] = "hidbridge-dev";
        adapterStart.Environment["HIDBRIDGE_EDGE_PROXY_COMMANDEXECUTOR"] = options.CommandExecutor;
        adapterStart.Environment["HIDBRIDGE_EDGE_PROXY_UARTPORT"] = options.UartPort;
        adapterStart.Environment["HIDBRIDGE_EDGE_PROXY_UARTBAUD"] = options.UartBaud.ToString();
        adapterStart.Environment["HIDBRIDGE_EDGE_PROXY_UARTHMACKEY"] = options.UartHmacKey;
        adapterStart.Environment["HIDBRIDGE_EDGE_PROXY_UARTRELEASEPORTAFTEREXECUTE"] = "true";
        adapterStart.Environment["HIDBRIDGE_EDGE_PROXY_UARTMOUSEINTERFACESELECTOR"] = "255";
        adapterStart.Environment["HIDBRIDGE_EDGE_PROXY_UARTKEYBOARDINTERFACESELECTOR"] = "254";
        adapterStart.Environment["HIDBRIDGE_EDGE_PROXY_MEDIAPLAYBACKURL"] = $"{options.WebBaseUrl.TrimEnd('/')}/media/edge-main";
        if (string.Equals(options.CommandExecutor, "uart", StringComparison.OrdinalIgnoreCase))
        {
            adapterStart.Environment["HIDBRIDGE_EDGE_PROXY_MEDIAHEALTHURL"] = string.Empty;
            adapterStart.Environment["HIDBRIDGE_EDGE_PROXY_ASSUMEMEDIAREADYWITHOUTPROBE"] = "true";
        }
        else
        {
            adapterStart.Environment["HIDBRIDGE_EDGE_PROXY_MEDIAHEALTHURL"] = ConvertControlWsToHealthUrl(options.ControlWsUrl);
            adapterStart.Environment["HIDBRIDGE_EDGE_PROXY_ASSUMEMEDIAREADYWITHOUTPROBE"] = "false";
        }

        adapterProcess = StartRedirectedProcess(adapterStart, adapterStdout, adapterStderr);

        await Task.Delay(500);
        if (adapterProcess.HasExited)
        {
            var tail = await ReadLogTailAsync(adapterStderr, 40);
            if (string.IsNullOrWhiteSpace(tail))
            {
                Console.Error.WriteLine($"Edge proxy agent exited early with code {adapterProcess.ExitCode}. See: {adapterStderr}");
                return 1;
            }

            Console.Error.WriteLine($"Edge proxy agent exited early with code {adapterProcess.ExitCode}.");
            Console.Error.WriteLine(tail);
            return 1;
        }

        var summary = new
        {
            apiBaseUrl = options.ApiBaseUrl,
            webBaseUrl = options.WebBaseUrl,
            commandExecutor = options.CommandExecutor,
            controlWsUrl = options.ControlWsUrl,
            runtimeBootstrapSkipped = options.SkipRuntimeBootstrap,
            sessionId,
            peerId,
            exp022Pid = exp022Process?.Id,
            adapterPid = adapterProcess.Id,
            sessionEnvPath,
            exp022Stdout,
            exp022Stderr,
            adapterStdout,
            adapterStderr,
        };

        var summaryJson = JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(stackSummaryPath, summaryJson, Encoding.UTF8);

        if (!string.IsNullOrWhiteSpace(options.OutputJsonPath))
        {
            var outputPath = Path.IsPathRooted(options.OutputJsonPath)
                ? options.OutputJsonPath
                : Path.Combine(platformRoot, options.OutputJsonPath);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? platformRoot);
            await File.WriteAllTextAsync(outputPath, summaryJson, Encoding.UTF8);
        }

        Console.WriteLine();
        Console.WriteLine("=== WebRTC Stack Summary ===");
        Console.WriteLine($"API:            {options.ApiBaseUrl}");
        Console.WriteLine($"Web:            {options.WebBaseUrl}");
        Console.WriteLine($"Executor:       {options.CommandExecutor}");
        if (exp022Process is not null)
        {
            Console.WriteLine($"Control WS:     {options.ControlWsUrl}");
        }
        Console.WriteLine($"Session:        {sessionId}");
        Console.WriteLine($"Peer:           {peerId}");
        Console.WriteLine($"Adapter PID:    {adapterProcess.Id}");
        Console.WriteLine($"Session env:    {sessionEnvPath}");
        Console.WriteLine($"Summary JSON:   {stackSummaryPath}");
        if (exp022Process is not null)
        {
            Console.WriteLine($"exp-022 stdout: {exp022Stdout}");
            Console.WriteLine($"exp-022 stderr: {exp022Stderr}");
        }
        Console.WriteLine($"adapter stdout: {adapterStdout}");
        Console.WriteLine($"adapter stderr: {adapterStderr}");
        Console.WriteLine();
        Console.WriteLine("Stop commands:");
        Console.WriteLine($"- Stop-Process -Id {adapterProcess.Id} -Force");
        if (exp022Process is not null)
        {
            Console.WriteLine($"- Stop-Process -Id {exp022Process.Id} -Force");
        }
        Console.WriteLine("- Stop runtime via demo-flow output (API/Web PIDs).");

        return 0;
    }

    private static void EnsureFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? Directory.GetCurrentDirectory());
        using var _ = File.Create(path);
    }

    internal static Process StartRedirectedProcess(ProcessStartInfo startInfo, string stdoutPath, string stderrPath)
    {
        var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Failed to start process: {startInfo.FileName}");
        _ = Task.Run(async () =>
        {
            await using var writer = new StreamWriter(stdoutPath, append: false, new UTF8Encoding(false));
            while (!process.StandardOutput.EndOfStream)
            {
                var line = await process.StandardOutput.ReadLineAsync();
                if (line is not null)
                {
                    await writer.WriteLineAsync(line);
                    await writer.FlushAsync();
                }
            }
        });
        _ = Task.Run(async () =>
        {
            await using var writer = new StreamWriter(stderrPath, append: false, new UTF8Encoding(false));
            while (!process.StandardError.EndOfStream)
            {
                var line = await process.StandardError.ReadLineAsync();
                if (line is not null)
                {
                    await writer.WriteLineAsync(line);
                    await writer.FlushAsync();
                }
            }
        });
        return process;
    }

    private static async Task<string> ReadLogTailAsync(string path, int lines)
    {
        if (!File.Exists(path))
        {
            return string.Empty;
        }

        var all = await File.ReadAllLinesAsync(path);
        if (all.Length <= lines)
        {
            return string.Join(Environment.NewLine, all);
        }

        return string.Join(Environment.NewLine, all.Skip(Math.Max(0, all.Length - lines)));
    }

    private static string ConvertControlWsToHealthUrl(string controlWsUrl)
    {
        var uri = new Uri(controlWsUrl);
        var scheme = string.Equals(uri.Scheme, "wss", StringComparison.OrdinalIgnoreCase) ? "https" : "http";
        return $"{scheme}://{uri.Host}:{uri.Port}/health";
    }

    private static bool TestLegacyExp022Enabled()
    {
        foreach (var candidate in ReadLegacyEnvCandidates("HIDBRIDGE_ENABLE_LEGACY_EXP022"))
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            switch (candidate.Trim().ToLowerInvariant())
            {
                case "1":
                case "true":
                case "yes":
                case "on":
                    return true;
            }
        }

        return false;
    }

    private static IEnumerable<string?> ReadLegacyEnvCandidates(string name)
    {
        yield return Environment.GetEnvironmentVariable(name);
        if (OperatingSystem.IsWindows())
        {
            string? user = null;
            string? machine = null;
            try { user = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User); } catch { }
            try { machine = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine); } catch { }
            yield return user;
            yield return machine;
        }
    }

    private static async Task StopExistingProcessesAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var script = """
            $needles = @('HidBridge.EdgeProxy.Agent', 'exp-022-datachanneldotnet')
            function Stop-MatchingProcesses {
                param([string]$ProcessName, [string[]]$Needles)
                $processes = @(Get-CimInstance Win32_Process -Filter "Name='$ProcessName'" -ErrorAction SilentlyContinue)
                $currentPid = $PID
                foreach ($proc in $processes) {
                    if ($proc.ProcessId -eq $currentPid) { continue }
                    $line = [string]$proc.CommandLine
                    if ([string]::IsNullOrWhiteSpace($line)) { continue }
                    $matches = $false
                    foreach ($needle in $Needles) {
                        if ($line -like "*$needle*") { $matches = $true; break }
                    }
                    if (-not $matches) { continue }
                    try {
                        Stop-Process -Id $proc.ProcessId -Force -ErrorAction Stop
                        Write-Host "Stopped PID $($proc.ProcessId): $ProcessName"
                    } catch {
                        Write-Warning "Failed to stop PID $($proc.ProcessId): $($_.Exception.Message)"
                    }
                }
            }
            Stop-MatchingProcesses -ProcessName 'dotnet.exe' -Needles $needles
            Stop-MatchingProcesses -ProcessName 'pwsh.exe' -Needles $needles
            Stop-MatchingProcesses -ProcessName 'powershell.exe' -Needles $needles
            """;

        var (shell, shellPrefix) = ResolvePowerShellExecutable();
        var psi = new ProcessStartInfo
        {
            FileName = shell,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var token in shellPrefix)
        {
            psi.ArgumentList.Add(token);
        }
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(script);

        using var process = Process.Start(psi);
        if (process is null)
        {
            return;
        }

        var stdOut = await process.StandardOutput.ReadToEndAsync();
        var stdErr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (!string.IsNullOrWhiteSpace(stdOut))
        {
            Console.Write(stdOut);
        }
        if (!string.IsNullOrWhiteSpace(stdErr))
        {
            Console.Write(stdErr);
        }
    }

    private static (string Shell, IReadOnlyList<string> PrefixArgs) ResolvePowerShellExecutable()
    {
        static string? FindInPath(IReadOnlyList<string> names)
        {
            var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            var entries = path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var name in names)
            {
                foreach (var entry in entries)
                {
                    var candidate = Path.Combine(entry, name);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }

            return null;
        }

        if (OperatingSystem.IsWindows())
        {
            var shell = FindInPath(["powershell.exe", "pwsh.exe"])
                ?? throw new InvalidOperationException("Unable to locate powershell.exe or pwsh.exe in PATH.");
            return (shell, ["-NoProfile", "-ExecutionPolicy", "Bypass"]);
        }

        var unixShell = FindInPath(["pwsh"])
            ?? throw new InvalidOperationException("Unable to locate pwsh in PATH.");
        return (unixShell, ["-NoProfile"]);
    }

    private sealed class WebRtcStackOptions
    {
        public string ApiBaseUrl { get; set; } = "http://127.0.0.1:18093";
        public string WebBaseUrl { get; set; } = "http://127.0.0.1:18110";
        public string CommandExecutor { get; set; } = "uart";
        public bool AllowLegacyControlWs { get; set; }
        public string ControlWsUrl { get; set; } = "ws://127.0.0.1:28092/ws/control";
        public string UartPort { get; set; } = "COM6";
        public int UartBaud { get; set; } = 3000000;
        public string UartHmacKey { get; set; } = "your-master-secret";
        public int Exp022DurationSec { get; set; } = 600;
        public int AdapterDurationSec { get; set; } = 900;
        public string EndpointId { get; set; } = "endpoint_local_demo";
        public string PrincipalId { get; set; } = "smoke-runner";
        public string TokenUsername { get; set; } = "operator.smoke.admin";
        public string TokenPassword { get; set; } = "ChangeMe123!";
        public string KeycloakBaseUrl { get; set; } = "http://host.docker.internal:18096";
        public string TokenScope { get; set; } = string.Empty;
        public int PeerReadyTimeoutSec { get; set; } = 30;
        public string OutputJsonPath { get; set; } = string.Empty;
        public bool SkipIdentityReset { get; set; } = true;
        public bool SkipCiLocal { get; set; } = true;
        public bool SkipRuntimeBootstrap { get; set; }
        public bool StopExisting { get; set; }
        public bool PeerReadyTimeoutSpecified { get; set; }
        public bool AdapterDurationSecSpecified { get; set; }
        public bool TokenScopeSpecified { get; set; }

        public static bool TryParse(IReadOnlyList<string> args, out WebRtcStackOptions options, out string? error)
        {
            options = new WebRtcStackOptions();
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
                    case "apibaseurl": options.ApiBaseUrl = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "webbaseurl": options.WebBaseUrl = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "commandexecutor": options.CommandExecutor = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "allowlegacycontrolws": options.AllowLegacyControlWs = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "controlwsurl": options.ControlWsUrl = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "uartport": options.UartPort = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "uartbaud": options.UartBaud = ParseInt(name, value, hasValue, ref i, ref error); break;
                    case "uarthmackey": options.UartHmacKey = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "exp022durationsec": options.Exp022DurationSec = ParseInt(name, value, hasValue, ref i, ref error); break;
                    case "adapterdurationsec":
                        options.AdapterDurationSec = ParseInt(name, value, hasValue, ref i, ref error);
                        options.AdapterDurationSecSpecified = true;
                        break;
                    case "endpointid": options.EndpointId = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "principalid": options.PrincipalId = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "tokenusername": options.TokenUsername = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "tokenpassword": options.TokenPassword = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "keycloakbaseurl": options.KeycloakBaseUrl = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "tokenscope":
                        options.TokenScope = RequireValue(name, value, ref i, ref hasValue, ref error);
                        options.TokenScopeSpecified = true;
                        break;
                    case "peerreadytimeoutsec":
                        options.PeerReadyTimeoutSec = ParseInt(name, value, hasValue, ref i, ref error);
                        options.PeerReadyTimeoutSpecified = true;
                        break;
                    case "outputjsonpath": options.OutputJsonPath = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "skipidentityreset": options.SkipIdentityReset = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "skipcilocal": options.SkipCiLocal = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "skipruntimebootstrap": options.SkipRuntimeBootstrap = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "stopexisting": options.StopExisting = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    default:
                        error = $"Unsupported webrtc-stack option '{token}'.";
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
                error = $"Option -{name} requires boolean value true/false when explicitly set.";
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

internal static class DemoFlowCommand
{
    public static async Task<int> RunAsync(string platformRoot, IReadOnlyList<string> args)
    {
        if (!DemoFlowOptions.TryParse(args, out var options, out var parseError))
        {
            Console.Error.WriteLine($"demo-flow options error: {parseError}");
            return 1;
        }

        var scriptsRoot = Path.Combine(platformRoot, "Scripts");
        var repoRoot = Directory.GetParent(platformRoot)?.FullName ?? platformRoot;
        var logRoot = Path.Combine(platformRoot, ".logs", "demo-flow", DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(logRoot);

        var results = new List<DemoFlowStepResult>();
        var runtimeServices = new List<DemoRuntimeService>();
        var runtimeStarted = new List<DemoRuntimeService>();
        var serviceStartupAttempted = false;
        var effectiveReuse = options.ReuseRunningServices;
        var effectiveSkipCiLocal = options.SkipCiLocal;
        var effectiveSkipDemoGate = options.SkipDemoGate;

        var authUri = new Uri(options.AuthAuthority);
        var keycloakBaseUrl = authUri.IsDefaultPort
            ? $"{authUri.Scheme}://{authUri.Host}"
            : $"{authUri.Scheme}://{authUri.Host}:{authUri.Port}";
        var realmName = "hidbridge-dev";
        var path = authUri.AbsolutePath;
        if (path.StartsWith("/realms/", StringComparison.OrdinalIgnoreCase))
        {
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                realmName = parts[1];
            }
        }

        if (options.IncludeWebRtcEdgeAgentSmoke && string.Equals(options.WebRtcCommandExecutor, "controlws", StringComparison.OrdinalIgnoreCase))
        {
            if (!options.AllowLegacyControlWs)
            {
                Console.Error.WriteLine("WebRtcCommandExecutor 'controlws' is legacy compatibility mode. Use 'uart' for production path, or pass -AllowLegacyControlWs explicitly.");
                return 1;
            }

            if (!TestLegacyExp022Enabled())
            {
                Console.Error.WriteLine("run_demo_flow controlws mode is disabled. Set HIDBRIDGE_ENABLE_LEGACY_EXP022=true in your shell to run exp-022/controlws lab flows.");
                return 1;
            }
            Console.WriteLine("WARNING: Legacy controlws executor enabled for demo-flow WebRTC validation (exp-022 compatibility mode).");
        }

        if (options.IncludeWebRtcEdgeAgentSmoke)
        {
            if (!effectiveReuse)
            {
                if (await TestApiHealthAsync(options.ApiBaseUrl))
                {
                    Console.WriteLine("WARNING: IncludeWebRtcEdgeAgentSmoke enabled: forcing service reuse to avoid restarting live WebRTC relay sessions.");
                    effectiveReuse = true;
                }
                else
                {
                    Console.WriteLine($"WARNING: IncludeWebRtcEdgeAgentSmoke enabled but API is not healthy at {options.ApiBaseUrl.TrimEnd('/')}/health. Starting runtime services normally (no reuse).");
                }
            }

            if (!effectiveSkipCiLocal)
            {
                Console.WriteLine("WARNING: IncludeWebRtcEdgeAgentSmoke enabled: skipping CI Local to avoid replacing the active API runtime during WebRTC relay validation.");
                effectiveSkipCiLocal = true;
            }
        }

        try
        {
            var apiUri = new Uri(options.ApiBaseUrl);
            var webUri = new Uri(options.WebBaseUrl);
            if (!effectiveReuse)
            {
                await StopPortListenersAsync(apiUri.Port, "API");
                await StopPortListenersAsync(webUri.Port, "Web");
            }

            var skipDoctorInCi = false;
            if (!options.SkipIdentityReset)
            {
                await RunRuntimeCtlStepAsync(platformRoot, logRoot, results, "Identity Reset", "identity-reset", Array.Empty<string>());
            }

            if (!options.SkipDoctor)
            {
                var doctorRequireApi = !options.IncludeWebRtcEdgeAgentSmoke;
                var doctorStartApiProbe = !options.IncludeWebRtcEdgeAgentSmoke;
                var doctorArgs = new List<string>
                {
                    "-StartApiProbe", ToBoolLiteral(doctorStartApiProbe),
                    "-RequireApi", ToBoolLiteral(doctorRequireApi),
                    "-ApiConfiguration", options.Configuration,
                    "-ApiPersistenceProvider", "Sql",
                    "-ApiConnectionString", options.ConnectionString,
                    "-ApiSchema", options.Schema,
                    "-KeycloakBaseUrl", keycloakBaseUrl,
                };
                await RunRuntimeCtlStepAsync(platformRoot, logRoot, results, "Doctor", "doctor", doctorArgs);
                skipDoctorInCi = true;
            }

            if (!effectiveSkipCiLocal)
            {
                var ciArgs = new List<string>
                {
                    "-Configuration", options.Configuration,
                    "-ConnectionString", options.ConnectionString,
                    "-Schema", options.Schema,
                    "-AuthAuthority", options.AuthAuthority,
                    "-SkipDoctor", ToBoolLiteral(skipDoctorInCi),
                };
                await RunRuntimeCtlStepAsync(platformRoot, logRoot, results, "CI Local", "ci-local", ciArgs);
            }

            if (options.IncludeFull)
            {
                var fullArgs = new List<string>
                {
                    "-Configuration", options.Configuration,
                    "-ConnectionString", options.ConnectionString,
                    "-Schema", options.Schema,
                    "-AuthAuthority", options.AuthAuthority,
                };
                await RunRuntimeCtlStepAsync(platformRoot, logRoot, results, "Full", "full", fullArgs);
            }

            if (!options.SkipServiceStartup)
            {
                serviceStartupAttempted = true;
                var apiProjectPath = Path.Combine(repoRoot, "Platform", "Platform", "HidBridge.ControlPlane.Api", "HidBridge.ControlPlane.Api.csproj");
                var webProjectPath = Path.Combine(repoRoot, "Platform", "Clients", "HidBridge.ControlPlane.Web", "HidBridge.ControlPlane.Web.csproj");

                var apiEnvironment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["ASPNETCORE_ENVIRONMENT"] = "Development",
                    ["ASPNETCORE_URLS"] = options.ApiBaseUrl,
                    ["HIDBRIDGE_PERSISTENCE_PROVIDER"] = "Sql",
                    ["HIDBRIDGE_SQL_CONNECTION"] = options.ConnectionString,
                    ["HIDBRIDGE_SQL_SCHEMA"] = options.Schema,
                    ["HIDBRIDGE_SQL_APPLY_MIGRATIONS"] = "true",
                    ["HIDBRIDGE_AUTH_ENABLED"] = "true",
                    ["HIDBRIDGE_AUTH_AUTHORITY"] = options.AuthAuthority,
                    ["HIDBRIDGE_AUTH_AUDIENCE"] = "",
                    ["HIDBRIDGE_AUTH_REQUIRE_HTTPS_METADATA"] = "false",
                    ["HIDBRIDGE_AUTH_BEARER_ONLY_PREFIXES"] = "",
                    ["HIDBRIDGE_AUTH_CALLER_CONTEXT_REQUIRED_PREFIXES"] = "",
                    ["HIDBRIDGE_AUTH_HEADER_FALLBACK_DISABLED_PATTERNS"] = "",
                };
                if (options.IncludeWebRtcEdgeAgentSmoke)
                {
                    apiEnvironment["HIDBRIDGE_UART_PASSIVE_HEALTH_MODE"] = "true";
                    apiEnvironment["HIDBRIDGE_UART_RELEASE_PORT_AFTER_EXECUTE"] = "true";
                    apiEnvironment["HIDBRIDGE_UART_PORT"] = "COM255";
                    apiEnvironment["HIDBRIDGE_TRANSPORT_PROVIDER"] = "webrtc-datachannel";
                    apiEnvironment["HIDBRIDGE_TRANSPORT_FALLBACK_TO_DEFAULT_ON_WEBRTC_ERROR"] = "false";
                }

                var apiRuntime = await StartDemoServiceAsync(
                    platformRoot, repoRoot, logRoot, results, options, "Start API", apiProjectPath,
                    options.ApiBaseUrl, new Uri(options.ApiBaseUrl).Host, new Uri(options.ApiBaseUrl).Port, "api-runtime",
                    useHealthProbe: true, effectiveReuse, apiEnvironment);
                runtimeServices.Add(apiRuntime);
                if (apiRuntime.Process is not null)
                {
                    runtimeStarted.Add(apiRuntime);
                }

                var demoSeedArgs = new List<string>
                {
                    "-BaseUrl", options.ApiBaseUrl,
                    "-KeycloakBaseUrl", keycloakBaseUrl,
                    "-RealmName", realmName,
                };
                await RunRuntimeCtlStepAsync(platformRoot, logRoot, results, "Demo Seed", "demo-seed", demoSeedArgs);

                var webrtcSessionEnvPath = Path.Combine(platformRoot, ".logs", "webrtc-peer-adapter.session.env");
                var shouldBootstrapWebRtcStack = false;
                if (options.IncludeWebRtcEdgeAgentSmoke)
                {
                    if (!effectiveReuse)
                    {
                        shouldBootstrapWebRtcStack = true;
                    }
                    else if (string.Equals(options.WebRtcCommandExecutor, "controlws", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!await TestControlHealthAsync(options.WebRtcControlHealthUrl, Math.Max(1, options.WebRtcControlHealthAttempts), 500))
                        {
                            shouldBootstrapWebRtcStack = true;
                        }
                    }
                    else if (!File.Exists(webrtcSessionEnvPath))
                    {
                        shouldBootstrapWebRtcStack = true;
                    }
                }

                if (shouldBootstrapWebRtcStack)
                {
                    Console.WriteLine("WARNING: Bootstrapping WebRTC edge stack automatically for demo-flow WebRTC gate.");
                    var bootstrapArgs = new List<string>
                    {
                        "-ApiBaseUrl", options.ApiBaseUrl,
                        "-WebBaseUrl", options.WebBaseUrl,
                        "-CommandExecutor", options.WebRtcCommandExecutor,
                        "-SkipRuntimeBootstrap",
                        "-SkipIdentityReset",
                        "-SkipCiLocal",
                        "-StopExisting",
                    };
                    if (options.AllowLegacyControlWs)
                    {
                        bootstrapArgs.Add("-AllowLegacyControlWs");
                    }
                    if (string.Equals(options.WebRtcCommandExecutor, "controlws", StringComparison.OrdinalIgnoreCase))
                    {
                        bootstrapArgs.Add("-ControlWsUrl");
                        bootstrapArgs.Add(ConvertControlHealthToWebSocketUrl(options.WebRtcControlHealthUrl));
                    }

                    await RunRuntimeCtlStepAsync(platformRoot, logRoot, results, "WebRTC Stack Bootstrap", "webrtc-stack", bootstrapArgs);
                }

                if (options.IncludeWebRtcEdgeAgentSmoke
                    && string.Equals(options.WebRtcCommandExecutor, "controlws", StringComparison.OrdinalIgnoreCase)
                    && !await TestControlHealthAsync(options.WebRtcControlHealthUrl, Math.Max(1, options.WebRtcControlHealthAttempts), 500))
                {
                    throw new InvalidOperationException($"WebRTC control health endpoint is still not ready after bootstrap: {options.WebRtcControlHealthUrl}");
                }

                if (!effectiveSkipDemoGate)
                {
                    var demoGateArgs = new List<string>
                    {
                        "-BaseUrl", options.ApiBaseUrl,
                        "-KeycloakBaseUrl", keycloakBaseUrl,
                        "-RealmName", realmName,
                        "-OutputJsonPath", Path.Combine(logRoot, "demo-gate.result.json"),
                    };
                    if (options.IncludeWebRtcEdgeAgentSmoke)
                    {
                        demoGateArgs.AddRange(
                        [
                            "-TransportProvider", "webrtc-datachannel",
                            "-ControlHealthUrl", options.WebRtcControlHealthUrl,
                            "-RequestTimeoutSec", Math.Max(1, options.WebRtcRequestTimeoutSec).ToString(),
                            "-ControlHealthAttempts", Math.Max(1, options.WebRtcControlHealthAttempts).ToString(),
                            "-PrincipalId", "smoke-runner",
                        ]);
                        if (options.SkipTransportHealthCheck)
                        {
                            demoGateArgs.Add("-SkipTransportHealthCheck");
                        }
                        if (options.TransportHealthAttempts > 0)
                        {
                            demoGateArgs.Add("-TransportHealthAttempts");
                            demoGateArgs.Add(Math.Max(1, options.TransportHealthAttempts).ToString());
                        }
                        if (options.TransportHealthDelayMs > 0)
                        {
                            demoGateArgs.Add("-TransportHealthDelayMs");
                            demoGateArgs.Add(Math.Max(100, options.TransportHealthDelayMs).ToString());
                        }
                    }
                    if (options.RequireDemoGateDeviceAck)
                    {
                        demoGateArgs.Add("-RequireDeviceAck");
                        if (options.DemoGateKeyboardInterfaceSelector >= 0 && options.DemoGateKeyboardInterfaceSelector <= 255)
                        {
                            demoGateArgs.Add("-KeyboardInterfaceSelector");
                            demoGateArgs.Add(options.DemoGateKeyboardInterfaceSelector.ToString());
                        }
                    }

                    await RunRuntimeCtlStepAsync(platformRoot, logRoot, results, "Demo Gate", "demo-gate", demoGateArgs);
                }

                if (options.IncludeWebRtcEdgeAgentSmoke)
                {
                    if (effectiveSkipDemoGate)
                    {
                        var smokeArgs = new List<string>
                        {
                            "-ApiBaseUrl", options.ApiBaseUrl,
                            "-ControlHealthUrl", options.WebRtcControlHealthUrl,
                            "-RequestTimeoutSec", Math.Max(1, options.WebRtcRequestTimeoutSec).ToString(),
                            "-ControlHealthAttempts", Math.Max(1, options.WebRtcControlHealthAttempts).ToString(),
                            "-SkipTransportHealthCheck", ToBoolLiteral(options.SkipTransportHealthCheck),
                            "-TransportHealthAttempts", (options.TransportHealthAttempts > 0 ? Math.Max(1, options.TransportHealthAttempts) : 20).ToString(),
                            "-TransportHealthDelayMs", (options.TransportHealthDelayMs > 0 ? Math.Max(100, options.TransportHealthDelayMs) : 500).ToString(),
                            "-OutputJsonPath", Path.Combine(logRoot, "webrtc-edge-agent-smoke.result.json"),
                        };
                        await RunRuntimeCtlStepAsync(platformRoot, logRoot, results, "WebRTC Edge Agent Smoke", "webrtc-edge-agent-smoke", smokeArgs);
                    }
                    else
                    {
                        Console.WriteLine("WARNING: Skipping standalone WebRTC Edge Agent Smoke step because Demo Gate already executed WebRTC transport validation in this run.");
                    }
                }

                var webEnvironment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["ASPNETCORE_ENVIRONMENT"] = "Development",
                    ["ASPNETCORE_URLS"] = options.WebBaseUrl,
                    ["ControlPlaneApi__BaseUrl"] = options.ApiBaseUrl,
                    ["ControlPlaneApi__PropagateAccessToken"] = "true",
                    ["ControlPlaneApi__PropagateIdentityHeaders"] = "true",
                    ["Identity__Enabled"] = "true",
                    ["Identity__ProviderDisplayName"] = "Keycloak",
                    ["Identity__Authority"] = options.AuthAuthority,
                    ["Identity__ClientId"] = "controlplane-web",
                    ["Identity__ClientSecret"] = "hidbridge-web-dev-secret",
                    ["Identity__RequireHttpsMetadata"] = "false",
                    ["Identity__DisablePushedAuthorization"] = "true",
                    ["Identity__PrincipalClaimType"] = "principal_id",
                    ["Identity__TenantClaimType"] = "tenant_id",
                    ["Identity__OrganizationClaimType"] = "org_id",
                    ["Identity__RoleClaimType"] = "role",
                };
                if (options.EnableCallerContextScope)
                {
                    webEnvironment["Identity__Scopes__0"] = "hidbridge-caller-context-v2";
                }

                var webRuntime = await StartDemoServiceAsync(
                    platformRoot, repoRoot, logRoot, results, options, "Start Web", webProjectPath,
                    options.WebBaseUrl, new Uri(options.WebBaseUrl).Host, new Uri(options.WebBaseUrl).Port, "web-runtime",
                    useHealthProbe: false, effectiveReuse, webEnvironment);
                runtimeServices.Add(webRuntime);
                if (webRuntime.Process is not null)
                {
                    runtimeStarted.Add(webRuntime);
                }
            }
        }
        catch (Exception ex)
        {
            var abortLogPath = Path.Combine(logRoot, "demo-flow.abort.log");
            await File.AppendAllTextAsync(abortLogPath, ex + Environment.NewLine);
            results.Add(new DemoFlowStepResult("Demo flow orchestration", "FAIL", 0, 1, abortLogPath));
            Console.WriteLine();
            Console.WriteLine($"Demo flow aborted: {ex.Message}");
        }

        WriteSummary(results, logRoot, "Demo Flow Summary");

        if (runtimeServices.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Demo endpoints:");
            foreach (var runtime in runtimeServices)
            {
                Console.WriteLine($"- {runtime.Name}: {runtime.BaseUrl}");
            }

            if (runtimeStarted.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("Stop commands:");
                foreach (var runtime in runtimeStarted)
                {
                    Console.WriteLine($"- Stop-Process -Id {runtime.Process!.Id}");
                }
            }
        }
        else if (serviceStartupAttempted)
        {
            Console.WriteLine();
            Console.WriteLine("Runtime services were already running; no new processes were started.");
        }

        return results.Any(static r => string.Equals(r.Status, "FAIL", StringComparison.OrdinalIgnoreCase)) ? 1 : 0;
    }

    private static async Task RunRuntimeCtlStepAsync(
        string platformRoot,
        string logRoot,
        List<DemoFlowStepResult> results,
        string name,
        string command,
        IReadOnlyList<string> commandArgs)
    {
        Console.WriteLine();
        Console.WriteLine($"=== {name} ===");
        var stopwatch = Stopwatch.StartNew();
        var logPath = Path.Combine(logRoot, $"{NormalizeName(name)}.log");

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

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            results.Add(new DemoFlowStepResult(name, "FAIL", stopwatch.Elapsed.TotalSeconds, 1, logPath));
            throw new InvalidOperationException($"Failed to start RuntimeCtl command '{command}'.");
        }

        var stdOutTask = process.StandardOutput.ReadToEndAsync();
        var stdErrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var stdOut = await stdOutTask;
        var stdErr = await stdErrTask;
        stopwatch.Stop();
        Directory.CreateDirectory(Path.GetDirectoryName(logPath) ?? platformRoot);
        await File.WriteAllTextAsync(logPath, $"{stdOut}{Environment.NewLine}{stdErr}".Trim());

        if (process.ExitCode != 0)
        {
            results.Add(new DemoFlowStepResult(name, "FAIL", stopwatch.Elapsed.TotalSeconds, process.ExitCode, logPath));
            Console.WriteLine($"FAIL  {name}");
            Console.WriteLine($"Log:   {logPath}");
            throw new InvalidOperationException($"{name} failed with exit code {process.ExitCode}. See log: {logPath}");
        }

        results.Add(new DemoFlowStepResult(name, "PASS", stopwatch.Elapsed.TotalSeconds, 0, logPath));
        Console.WriteLine($"PASS  {name}");
        Console.WriteLine($"Log:   {logPath}");
    }

    private static async Task<DemoRuntimeService> StartDemoServiceAsync(
        string platformRoot,
        string repoRoot,
        string logRoot,
        List<DemoFlowStepResult> results,
        DemoFlowOptions options,
        string name,
        string projectPath,
        string baseUrl,
        string targetHost,
        int port,
        string category,
        bool useHealthProbe,
        bool reuseRunningService,
        IReadOnlyDictionary<string, string> environment)
    {
        Console.WriteLine();
        Console.WriteLine($"=== {name} ===");
        var stdoutLog = Path.Combine(logRoot, $"{category}.stdout.log");
        var stderrLog = Path.Combine(logRoot, $"{category}.stderr.log");
        EnsureEmptyFile(stdoutLog);
        EnsureEmptyFile(stderrLog);

        var sw = Stopwatch.StartNew();
        if (await TestTcpPortAsync(targetHost, port))
        {
            if (reuseRunningService)
            {
                if (useHealthProbe && !await TestApiHealthAsync(baseUrl))
                {
                    throw new InvalidOperationException($"{name} reuse requested but health probe failed at {baseUrl.TrimEnd('/')}/health. Ensure runtime is running and healthy, or run without -ReuseRunningServices.");
                }

                sw.Stop();
                results.Add(new DemoFlowStepResult(name, "PASS", sw.Elapsed.TotalSeconds, 0, stdoutLog));
                Console.WriteLine($"PASS  {name}");
                Console.WriteLine($"URL:   {baseUrl} (already running)");
                return new DemoRuntimeService(name, baseUrl, Started: false, AlreadyRunning: true, Process: null, stdoutLog, stderrLog);
            }

            await StopPortListenersAsync(port, name);
            await Task.Delay(500);
            if (await TestTcpPortAsync(targetHost, port))
            {
                if (useHealthProbe && await TestApiHealthAsync(baseUrl))
                {
                    sw.Stop();
                    results.Add(new DemoFlowStepResult(name, "PASS", sw.Elapsed.TotalSeconds, 0, stdoutLog));
                    Console.WriteLine($"PASS  {name}");
                    Console.WriteLine($"URL:   {baseUrl} (already running)");
                    return new DemoRuntimeService(name, baseUrl, Started: false, AlreadyRunning: true, Process: null, stdoutLog, stderrLog);
                }

                throw new InvalidOperationException($"{name} could not claim port {port} because another process is still listening.");
            }
        }

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
        startInfo.ArgumentList.Add(projectPath);
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(options.Configuration);
        startInfo.ArgumentList.Add("--no-launch-profile");
        if (options.NoBuild)
        {
            startInfo.ArgumentList.Add("--no-build");
        }
        foreach (var entry in environment)
        {
            startInfo.Environment[entry.Key] = entry.Value;
        }

        var process = WebRtcStackCommand.StartRedirectedProcess(startInfo, stdoutLog, stderrLog);
        var started = false;
        for (var attempt = 0; attempt < 60; attempt++)
        {
            await Task.Delay(500);
            if (useHealthProbe)
            {
                if (await TestApiHealthAsync(baseUrl))
                {
                    started = true;
                    break;
                }
            }
            else
            {
                if (await TestTcpPortAsync(targetHost, port))
                {
                    started = true;
                    break;
                }
            }

            if (process.HasExited)
            {
                break;
            }
        }

        sw.Stop();
        if (started)
        {
            results.Add(new DemoFlowStepResult(name, "PASS", sw.Elapsed.TotalSeconds, 0, stdoutLog));
            Console.WriteLine($"PASS  {name}");
            Console.WriteLine($"URL:   {baseUrl}");
            Console.WriteLine($"PID:   {process.Id}");
            Console.WriteLine($"Out:   {stdoutLog}");
            Console.WriteLine($"Err:   {stderrLog}");
            return new DemoRuntimeService(name, baseUrl, Started: true, AlreadyRunning: false, process, stdoutLog, stderrLog);
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
            // ignore cleanup errors
        }

        results.Add(new DemoFlowStepResult(name, "FAIL", sw.Elapsed.TotalSeconds, 1, stderrLog));
        Console.WriteLine($"FAIL  {name}");
        Console.WriteLine($"Out:   {stdoutLog}");
        Console.WriteLine($"Err:   {stderrLog}");
        throw new InvalidOperationException($"{name} failed to start. See logs: {stdoutLog} / {stderrLog}");
    }

    private static async Task<bool> TestApiHealthAsync(string baseUrl)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var response = await http.GetAsync($"{baseUrl.TrimEnd('/')}/health");
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }
            using var stream = await response.Content.ReadAsStreamAsync();
            using var json = await JsonDocument.ParseAsync(stream);
            return json.RootElement.TryGetProperty("status", out var status)
                   && string.Equals(status.GetString(), "ok", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> TestControlHealthAsync(string url, int attempts, int delayMs)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        for (var attempt = 0; attempt < Math.Max(1, attempts); attempt++)
        {
            try
            {
                using var response = await http.GetAsync(url);
                if (response.IsSuccessStatusCode)
                {
                    using var stream = await response.Content.ReadAsStreamAsync();
                    using var json = await JsonDocument.ParseAsync(stream);
                    if (json.RootElement.TryGetProperty("ok", out var okProp)
                        && (okProp.ValueKind is JsonValueKind.True or JsonValueKind.False))
                    {
                        if (okProp.GetBoolean())
                        {
                            return true;
                        }
                    }
                }
            }
            catch
            {
                // ignore
            }

            await Task.Delay(Math.Max(100, delayMs));
        }

        return false;
    }

    private static async Task<bool> TestTcpPortAsync(string host, int port, int timeoutMs = 1500)
    {
        using var client = new System.Net.Sockets.TcpClient();
        try
        {
            var connectTask = client.ConnectAsync(host, port);
            var timeoutTask = Task.Delay(timeoutMs);
            var completed = await Task.WhenAny(connectTask, timeoutTask);
            if (completed == timeoutTask)
            {
                return false;
            }
            await connectTask;
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }

    private static async Task StopPortListenersAsync(int port, string label)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var script = $$"""
            $port = {{port}}
            $label = '{{label}}'
            $listeners = @(Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue | Select-Object -ExpandProperty OwningProcess -Unique)
            foreach ($listenerPid in $listeners) {
                if (-not $listenerPid -or $listenerPid -le 0) { continue }
                try {
                    $existingProcess = Get-Process -Id $listenerPid -ErrorAction SilentlyContinue
                    if ($null -eq $existingProcess) {
                        Write-Warning "Preflight cleanup ($label): PID $listenerPid already stopped before port $port cleanup."
                        continue
                    }
                    Stop-Process -Id $listenerPid -Force -ErrorAction Stop
                    Write-Host "Preflight cleanup ($label): stopped listener PID $listenerPid on port $port."
                } catch {
                    $existingProcess = Get-Process -Id $listenerPid -ErrorAction SilentlyContinue
                    if ($null -eq $existingProcess) {
                        Write-Warning "Preflight cleanup ($label): PID $listenerPid exited during port $port cleanup."
                        continue
                    }
                    throw "Preflight cleanup ($label) failed to stop PID $listenerPid on port $port."
                }
            }
            """;

        var (shell, prefix) = ResolvePowerShellExecutable();
        var psi = new ProcessStartInfo
        {
            FileName = shell,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var item in prefix)
        {
            psi.ArgumentList.Add(item);
        }
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(script);

        using var process = Process.Start(psi);
        if (process is null)
        {
            return;
        }
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (!string.IsNullOrWhiteSpace(stdout))
        {
            Console.Write(stdout);
        }
        if (!string.IsNullOrWhiteSpace(stderr))
        {
            Console.Write(stderr);
        }
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Preflight cleanup ({label}) failed for port {port}.");
        }
    }

    private static (string Shell, IReadOnlyList<string> PrefixArgs) ResolvePowerShellExecutable()
    {
        static string? FindInPath(IReadOnlyList<string> names)
        {
            var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            var entries = path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var name in names)
            {
                foreach (var entry in entries)
                {
                    var candidate = Path.Combine(entry, name);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }
            return null;
        }

        if (OperatingSystem.IsWindows())
        {
            var shell = FindInPath(["powershell.exe", "pwsh.exe"])
                ?? throw new InvalidOperationException("Unable to locate powershell.exe or pwsh.exe in PATH.");
            return (shell, ["-NoProfile", "-ExecutionPolicy", "Bypass"]);
        }

        var unixShell = FindInPath(["pwsh"])
            ?? throw new InvalidOperationException("Unable to locate pwsh in PATH.");
        return (unixShell, ["-NoProfile"]);
    }

    private static string ConvertControlHealthToWebSocketUrl(string controlHealthUrl)
    {
        var uri = new Uri(controlHealthUrl);
        var builder = new UriBuilder(uri)
        {
            Scheme = string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws",
            Path = uri.AbsolutePath.EndsWith("/health", StringComparison.OrdinalIgnoreCase)
                ? uri.AbsolutePath[..^"/health".Length] + "/ws/control"
                : "/ws/control",
        };
        return builder.Uri.AbsoluteUri;
    }

    private static bool TestLegacyExp022Enabled()
    {
        foreach (var candidate in ReadLegacyEnvCandidates("HIDBRIDGE_ENABLE_LEGACY_EXP022"))
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }
            switch (candidate.Trim().ToLowerInvariant())
            {
                case "1":
                case "true":
                case "yes":
                case "on":
                    return true;
            }
        }
        return false;
    }

    private static IEnumerable<string?> ReadLegacyEnvCandidates(string name)
    {
        yield return Environment.GetEnvironmentVariable(name);
        if (OperatingSystem.IsWindows())
        {
            string? user = null;
            string? machine = null;
            try { user = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User); } catch { }
            try { machine = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine); } catch { }
            yield return user;
            yield return machine;
        }
    }

    private static string ToBoolLiteral(bool value) => value ? "true" : "false";

    private static string NormalizeName(string name)
        => string.Concat(name.Select(ch => char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-' ? ch : '-'))
            .Trim('-');

    private static void WriteSummary(IReadOnlyList<DemoFlowStepResult> results, string logRoot, string header)
    {
        Console.WriteLine();
        Console.WriteLine($"=== {header} ===");
        Console.WriteLine();
        Console.WriteLine($"{"Name",-28} {"Status",-6} {"Seconds",7} {"ExitCode",8}");
        foreach (var result in results)
        {
            Console.WriteLine($"{result.Name,-28} {result.Status,-6} {result.Seconds,7:F2} {result.ExitCode,8}");
        }
        Console.WriteLine();
        Console.WriteLine($"Logs root: {logRoot}");
        foreach (var result in results)
        {
            Console.WriteLine($"{result.Name}: {result.LogPath}");
        }
    }

    private static void EnsureEmptyFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? Directory.GetCurrentDirectory());
        File.WriteAllText(path, string.Empty);
    }

    private sealed record DemoFlowStepResult(string Name, string Status, double Seconds, int ExitCode, string LogPath);

    private sealed record DemoRuntimeService(
        string Name,
        string BaseUrl,
        bool Started,
        bool AlreadyRunning,
        Process? Process,
        string StdoutLog,
        string StderrLog);

    private sealed class DemoFlowOptions
    {
        public string Configuration { get; set; } = "Debug";
        public string ConnectionString { get; set; } = "Host=127.0.0.1;Port=5434;Database=hidbridge;Username=hidbridge;Password=hidbridge";
        public string Schema { get; set; } = "hidbridge";
        public string AuthAuthority { get; set; } = "http://127.0.0.1:18096/realms/hidbridge-dev";
        public string ApiBaseUrl { get; set; } = "http://127.0.0.1:18093";
        public string WebBaseUrl { get; set; } = "http://127.0.0.1:18110";
        public bool SkipIdentityReset { get; set; }
        public bool SkipDoctor { get; set; }
        public bool SkipCiLocal { get; set; }
        public bool IncludeFull { get; set; }
        public bool SkipServiceStartup { get; set; }
        public bool SkipDemoGate { get; set; }
        public bool NoBuild { get; set; }
        public bool ShowServiceWindows { get; set; }
        public bool ReuseRunningServices { get; set; }
        public bool EnableCallerContextScope { get; set; }
        public bool RequireDemoGateDeviceAck { get; set; }
        public int DemoGateKeyboardInterfaceSelector { get; set; } = -1;
        public bool IncludeWebRtcEdgeAgentSmoke { get; set; }
        public string WebRtcCommandExecutor { get; set; } = "uart";
        public bool AllowLegacyControlWs { get; set; }
        public string WebRtcControlHealthUrl { get; set; } = "http://127.0.0.1:28092/health";
        public int WebRtcRequestTimeoutSec { get; set; } = 15;
        public int WebRtcControlHealthAttempts { get; set; } = 20;
        public bool SkipTransportHealthCheck { get; set; }
        public int TransportHealthAttempts { get; set; } = -1;
        public int TransportHealthDelayMs { get; set; } = -1;

        public static bool TryParse(IReadOnlyList<string> args, out DemoFlowOptions options, out string? error)
        {
            options = new DemoFlowOptions();
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
                    case "configuration": options.Configuration = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "connectionstring": options.ConnectionString = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "schema": options.Schema = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "authauthority": options.AuthAuthority = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "apibaseurl": options.ApiBaseUrl = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "webbaseurl": options.WebBaseUrl = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "skipidentityreset": options.SkipIdentityReset = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "skipdoctor": options.SkipDoctor = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "skipcilocal": options.SkipCiLocal = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "includefull": options.IncludeFull = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "skipservicestartup": options.SkipServiceStartup = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "skipdemogate": options.SkipDemoGate = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "nobuild": options.NoBuild = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "showservicewindows": options.ShowServiceWindows = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "reuserunningservices": options.ReuseRunningServices = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "enablecallercontextscope": options.EnableCallerContextScope = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "requiredemogatedeviceack": options.RequireDemoGateDeviceAck = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "demogatekeyboardinterfaceselector": options.DemoGateKeyboardInterfaceSelector = ParseInt(name, value, hasValue, ref i, ref error); break;
                    case "includewebrtcedgeagentsmoke": options.IncludeWebRtcEdgeAgentSmoke = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "webrtccommandexecutor":
                    case "webrtccommandeexecutor":
                        options.WebRtcCommandExecutor = RequireValue(name, value, ref i, ref hasValue, ref error);
                        break;
                    case "allowlegacycontrolws": options.AllowLegacyControlWs = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "webrtccontrolhealthurl": options.WebRtcControlHealthUrl = RequireValue(name, value, ref i, ref hasValue, ref error); break;
                    case "webrtcrequesttimeoutsec": options.WebRtcRequestTimeoutSec = ParseInt(name, value, hasValue, ref i, ref error); break;
                    case "webrtccontrolhealthattempts": options.WebRtcControlHealthAttempts = ParseInt(name, value, hasValue, ref i, ref error); break;
                    case "skiptransporthealthcheck": options.SkipTransportHealthCheck = ParseSwitch(name, value, hasValue, ref i, ref error); break;
                    case "transporthealthattempts": options.TransportHealthAttempts = ParseInt(name, value, hasValue, ref i, ref error); break;
                    case "transporthealthdelayms": options.TransportHealthDelayMs = ParseInt(name, value, hasValue, ref i, ref error); break;
                    default:
                        error = $"Unsupported demo-flow option '{token}'.";
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
                error = $"Option -{name} requires boolean value true/false when explicitly set.";
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
