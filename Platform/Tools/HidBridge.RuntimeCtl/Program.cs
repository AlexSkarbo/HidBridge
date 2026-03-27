using System.Diagnostics;
using System.ComponentModel;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using System.Text;

// RuntimeCtl is the CLI-first orchestration entrypoint for local developer workflows.
// All heavy command lanes live in `Commands/*.cs`; this file keeps only command routing
// and shared process helpers used by multiple lanes.
var app = RuntimeCtlApp.Parse(args);
return await app.RunAsync();

/// <summary>
/// Parses top-level CLI invocation, resolves command aliases and dispatches execution
/// to lane implementations in <c>Commands/*.cs</c>.
/// </summary>
internal sealed class RuntimeCtlApp
{
    // Native RuntimeCtl commands. `Name` is also used by help output and command matching.
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
            ["bearer-rollout"] = new("bearer-rollout", "Runs phased bearer fallback rollout validation (native C# orchestration).", CommandKind.BearerRollout, null),
            ["identity-onboard"] = new("identity-onboard", "Onboards operator identity in Keycloak (native C#).", CommandKind.IdentityOnboard, null),
            ["uart-diagnostics"] = new("uart-diagnostics", "Runs UART selector/scenario diagnostics (native C#).", CommandKind.UartDiagnostics, null),
            ["webrtc-relay-smoke"] = new("webrtc-relay-smoke", "Runs legacy WebRTC relay smoke compatibility lane (native C#).", CommandKind.WebRtcRelaySmoke, null),
            ["webrtc-peer-adapter"] = new("webrtc-peer-adapter", "Runs deprecated exp-022 peer-adapter compatibility lane (native C#).", CommandKind.WebRtcPeerAdapter, null),
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
        // Parse optional `--platform-root` prefix before command token.
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
            return new RuntimeCtlApp(
                platformRoot,
                null,
                Array.Empty<string>(),
                showHelp: true,
                error: "Legacy 'task <name>' routing was removed. Use native command syntax: runtimectl <command> [-- <args...>].");
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
            CommandKind.BearerRollout => await BearerRolloutCommand.RunAsync(_platformRoot, _forwardArgs),
            CommandKind.IdentityOnboard => await IdentityOnboardCommand.RunAsync(_platformRoot, _forwardArgs),
            CommandKind.UartDiagnostics => await UartDiagnosticsCommand.RunAsync(_platformRoot, _forwardArgs),
            CommandKind.WebRtcRelaySmoke => await WebRtcRelaySmokeCommand.RunAsync(_platformRoot, _forwardArgs),
            CommandKind.WebRtcPeerAdapter => await WebRtcPeerAdapterCommand.RunAsync(_platformRoot, _forwardArgs),
            CommandKind.ScriptBridge => await RunScriptBridgeAsync(_platformRoot, _command.ScriptRelativePath!, _forwardArgs),
            _ => throw new InvalidOperationException($"Unsupported command kind '{_command.Kind}'."),
        };
    }

    /// <summary>
    /// Returns parse/dispatch metadata used by unit tests to validate alias and routing behavior
    /// without invoking full command execution.
    /// </summary>
    internal RuntimeCtlDiagnosticsSnapshot GetDiagnosticsSnapshot()
    {
        return new RuntimeCtlDiagnosticsSnapshot(
            ShowHelp: _showHelp,
            Error: _error,
            PlatformRoot: _platformRoot,
            CommandName: _command?.Name,
            CommandKind: _command?.Kind.ToString(),
            ScriptRelativePath: _command?.ScriptRelativePath,
            ForwardArgs: _forwardArgs.ToArray());
    }

    // For `runtimectl <command> -- <args...>` compatibility: strip leading `--`.
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
        // Script bridge is intentionally explicit and centralized so it can be removed
        // command-by-command without touching all callers.
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
        // Same as RunScriptBridgeAsync, but captures stdout/stderr into a log artifact
        // for summary tables used by CI-like lanes.
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
        static bool LooksLikePlatformRoot(string path)
        {
            return File.Exists(Path.Combine(path, "Tools", "HidBridge.RuntimeCtl", "HidBridge.RuntimeCtl.csproj"));
        }

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
            if (LooksLikePlatformRoot(directPlatform))
            {
                return directPlatform;
            }

            if (LooksLikePlatformRoot(probe.FullName)
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

        Console.WriteLine("HidBridge.RuntimeCtl - CLI-first entrypoint\n");
        Console.WriteLine("Usage:");
        Console.WriteLine("  runtimectl [--platform-root <path>] <command> [-- <command-args...>]");
        Console.WriteLine("  runtimectl [--platform-root <path>] <command> <command-args...>\n");

        Console.WriteLine("Commands:");
        foreach (var command in CommandAliases.Values.OrderBy(static x => x.Name, StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine($"  {command.Name,-18} {command.Description}");
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
        BearerRollout,
        IdentityOnboard,
        UartDiagnostics,
        WebRtcRelaySmoke,
        WebRtcPeerAdapter,
    }

    internal sealed record RuntimeCtlDiagnosticsSnapshot(
        bool ShowHelp,
        string? Error,
        string PlatformRoot,
        string? CommandName,
        string? CommandKind,
        string? ScriptRelativePath,
        IReadOnlyList<string> ForwardArgs);

    internal sealed record ScriptInvocationResult(int ExitCode, string StdOut, string StdErr);
}
