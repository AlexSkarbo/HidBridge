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
/// Implements the <c>WebRtcStackCommand</c> RuntimeCtl lane.
/// Execution contract:
/// 1) Parse CLI arguments into strongly typed options.
/// 2) Execute lane-specific orchestration (native RuntimeCtl commands first, script bridge only when explicitly needed).
/// 3) Persist step logs/summaries under <c>Platform/.logs</c> and return process-style exit code semantics (0 = success).
/// </summary>
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

        var configuredWhepUrl = (Environment.GetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_MEDIAWHEPURL") ?? string.Empty).Trim();
        var configuredWhipUrl = (Environment.GetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_MEDIAWHIPURL") ?? string.Empty).Trim();
        var configuredPlaybackUrl = (Environment.GetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_MEDIAPLAYBACKURL") ?? string.Empty).Trim();
        var configuredMediaHealthUrl = (Environment.GetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_MEDIAHEALTHURL") ?? string.Empty).Trim();
        var configuredMediaEngine = (Environment.GetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_MEDIAENGINE") ?? string.Empty).Trim();
        var configuredAssumeMediaReadyWithoutProbe = (Environment.GetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_ASSUMEMEDIAREADYWITHOUTPROBE") ?? string.Empty).Trim();
        var configuredMediaBackendAutoStart = (Environment.GetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_MEDIABACKENDAUTOSTART") ?? string.Empty).Trim();
        var configuredMediaBackendExecutablePath = (Environment.GetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_MEDIABACKENDEXECUTABLEPATH") ?? string.Empty).Trim();
        var configuredMediaBackendArgumentsTemplate = (Environment.GetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_MEDIABACKENDARGUMENTSTEMPLATE") ?? string.Empty).Trim();
        var configuredMediaBackendWorkingDirectory = (Environment.GetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_MEDIABACKENDWORKINGDIRECTORY") ?? string.Empty).Trim();
        var configuredMediaBackendStartupTimeoutSec = (Environment.GetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_MEDIABACKENDSTARTUPTIMEOUTSEC") ?? string.Empty).Trim();
        var configuredMediaBackendProbeDelayMs = (Environment.GetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_MEDIABACKENDPROBEDELAYMS") ?? string.Empty).Trim();
        var configuredMediaBackendProbeTimeoutMs = (Environment.GetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_MEDIABACKENDPROBETIMEOUTMS") ?? string.Empty).Trim();
        var configuredMediaBackendStopTimeoutMs = (Environment.GetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_MEDIABACKENDSTOPTIMEOUTMS") ?? string.Empty).Trim();
        var useFfmpegDcdMediaEngine = string.Equals(configuredMediaEngine, "ffmpeg-dcd", StringComparison.OrdinalIgnoreCase);
        var effectiveMediaBackendAutoStart = !string.IsNullOrWhiteSpace(configuredMediaBackendAutoStart)
            ? configuredMediaBackendAutoStart
            : (useFfmpegDcdMediaEngine ? "true" : string.Empty);
        var effectiveMediaBackendExecutablePath = !string.IsNullOrWhiteSpace(configuredMediaBackendExecutablePath)
            ? configuredMediaBackendExecutablePath
            : (TryParseOptionalBool(effectiveMediaBackendAutoStart) ? "ffmpeg" : string.Empty);

        if (!ValidateMediaBackendAutoStartPreflight(
                repoRoot,
                effectiveMediaBackendAutoStart,
                effectiveMediaBackendExecutablePath,
                configuredMediaBackendWorkingDirectory,
                out var resolvedMediaBackendExecutablePath,
                out var mediaBackendPreflightError))
        {
            Console.Error.WriteLine($"webrtc-stack media backend preflight failed: {mediaBackendPreflightError}");
            return 1;
        }

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
            var authAuthority = $"{options.KeycloakBaseUrl.TrimEnd('/')}/realms/hidbridge-dev";
            var demoFlowArgs = new List<string>
            {
                "-ApiBaseUrl", options.ApiBaseUrl,
                "-WebBaseUrl", options.WebBaseUrl,
                "-AuthAuthority", authAuthority,
                "-SkipDoctor",
                "-SkipDemoGate",
                "-SkipCiLocal",
                "-IncludeWebRtcEdgeAgentSmoke",
                "-SkipWebRtcSmokeStep",
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

        var edgeAgentDll = Path.Combine(
            repoRoot,
            "Platform",
            "Edge",
            "HidBridge.EdgeProxy.Agent",
            "bin",
            "Debug",
            "net10.0",
            "HidBridge.EdgeProxy.Agent.dll");

        var edgeAgentBuildExit = await RunProcessAsync(
            "dotnet",
            new[]
            {
                "build",
                edgeAgentProject,
                "-c",
                "Debug",
                "-v",
                "minimal",
                "-nologo",
            },
            repoRoot);
        if (edgeAgentBuildExit != 0)
        {
            Console.Error.WriteLine($"Failed to build EdgeProxy.Agent (exit code {edgeAgentBuildExit}).");
            return 1;
        }

        if (!File.Exists(edgeAgentDll))
        {
            Console.Error.WriteLine($"EdgeProxy.Agent build output not found: {edgeAgentDll}");
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
        adapterStart.ArgumentList.Add(edgeAgentDll);

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
        // Use aggressive heartbeat/poll cadence for smoke stability so route readiness
        // does not flap during short command retries.
        adapterStart.Environment["HIDBRIDGE_EDGE_PROXY_HEARTBEATINTERVALSEC"] = "3";
        adapterStart.Environment["HIDBRIDGE_EDGE_PROXY_POLLINTERVALMS"] = "100";
        var effectivePlaybackUrl = !string.IsNullOrWhiteSpace(configuredWhepUrl)
            ? configuredWhepUrl
            : configuredPlaybackUrl;
        adapterStart.Environment["HIDBRIDGE_EDGE_PROXY_MEDIAPLAYBACKURL"] = effectivePlaybackUrl;
        adapterStart.Environment["HIDBRIDGE_EDGE_PROXY_MEDIAWHEPURL"] = configuredWhepUrl;
        adapterStart.Environment["HIDBRIDGE_EDGE_PROXY_MEDIAWHIPURL"] = configuredWhipUrl;
        adapterStart.Environment["HIDBRIDGE_EDGE_PROXY_MEDIABACKENDAUTOSTART"] = effectiveMediaBackendAutoStart;
        adapterStart.Environment["HIDBRIDGE_EDGE_PROXY_MEDIABACKENDEXECUTABLEPATH"] = resolvedMediaBackendExecutablePath;
        adapterStart.Environment["HIDBRIDGE_EDGE_PROXY_MEDIABACKENDARGUMENTSTEMPLATE"] = configuredMediaBackendArgumentsTemplate;
        adapterStart.Environment["HIDBRIDGE_EDGE_PROXY_MEDIABACKENDWORKINGDIRECTORY"] = configuredMediaBackendWorkingDirectory;
        adapterStart.Environment["HIDBRIDGE_EDGE_PROXY_MEDIABACKENDSTARTUPTIMEOUTSEC"] = configuredMediaBackendStartupTimeoutSec;
        adapterStart.Environment["HIDBRIDGE_EDGE_PROXY_MEDIABACKENDPROBEDELAYMS"] = configuredMediaBackendProbeDelayMs;
        adapterStart.Environment["HIDBRIDGE_EDGE_PROXY_MEDIABACKENDPROBETIMEOUTMS"] = configuredMediaBackendProbeTimeoutMs;
        adapterStart.Environment["HIDBRIDGE_EDGE_PROXY_MEDIABACKENDSTOPTIMEOUTMS"] = configuredMediaBackendStopTimeoutMs;
        if (string.Equals(options.CommandExecutor, "uart", StringComparison.OrdinalIgnoreCase))
        {
            // Preserve explicit media-health and assume-ready settings from caller.
            // Do not force optimistic media-ready defaults for UART when media path is not configured.
            adapterStart.Environment["HIDBRIDGE_EDGE_PROXY_MEDIAHEALTHURL"] = configuredMediaHealthUrl;
            if (!string.IsNullOrWhiteSpace(configuredAssumeMediaReadyWithoutProbe))
            {
                adapterStart.Environment["HIDBRIDGE_EDGE_PROXY_ASSUMEMEDIAREADYWITHOUTPROBE"] = configuredAssumeMediaReadyWithoutProbe;
            }
            else
            {
                adapterStart.Environment["HIDBRIDGE_EDGE_PROXY_ASSUMEMEDIAREADYWITHOUTPROBE"] = "false";
            }
        }
        else
        {
            adapterStart.Environment["HIDBRIDGE_EDGE_PROXY_MEDIAHEALTHURL"] = string.IsNullOrWhiteSpace(configuredMediaHealthUrl)
                ? ConvertControlWsToHealthUrl(options.ControlWsUrl)
                : configuredMediaHealthUrl;
            adapterStart.Environment["HIDBRIDGE_EDGE_PROXY_ASSUMEMEDIAREADYWITHOUTPROBE"] = string.IsNullOrWhiteSpace(configuredAssumeMediaReadyWithoutProbe)
                ? "false"
                : configuredAssumeMediaReadyWithoutProbe;
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
            mediaHealthUrl = configuredMediaHealthUrl,
            mediaWhipUrl = configuredWhipUrl,
            mediaWhepUrl = configuredWhepUrl,
            mediaPlaybackUrl = effectivePlaybackUrl,
            mediaBackendAutoStart = TryParseOptionalBool(effectiveMediaBackendAutoStart),
            mediaBackendExecutablePath = resolvedMediaBackendExecutablePath,
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
        Console.WriteLine($"Media WHEP:     {(string.IsNullOrWhiteSpace(configuredWhepUrl) ? "<not configured>" : configuredWhepUrl)}");
        Console.WriteLine($"Media WHIP:     {(string.IsNullOrWhiteSpace(configuredWhipUrl) ? "<not configured>" : configuredWhipUrl)}");
        Console.WriteLine($"Media playback: {(string.IsNullOrWhiteSpace(effectivePlaybackUrl) ? "<not configured>" : effectivePlaybackUrl)}");
        Console.WriteLine($"Media backend auto-start: {(string.IsNullOrWhiteSpace(effectiveMediaBackendAutoStart) ? "false" : effectiveMediaBackendAutoStart)}");
        if (!string.IsNullOrWhiteSpace(resolvedMediaBackendExecutablePath))
        {
            Console.WriteLine($"Media backend executable: {resolvedMediaBackendExecutablePath}");
        }
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
            while (true)
            {
                var line = await process.StandardOutput.ReadLineAsync();
                if (line is null)
                {
                    break;
                }

                await writer.WriteLineAsync(line);
                await writer.FlushAsync();
            }
        });
        _ = Task.Run(async () =>
        {
            await using var writer = new StreamWriter(stderrPath, append: false, new UTF8Encoding(false));
            while (true)
            {
                var line = await process.StandardError.ReadLineAsync();
                if (line is null)
                {
                    break;
                }

                await writer.WriteLineAsync(line);
                await writer.FlushAsync();
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

    private static async Task<int> RunProcessAsync(
        string fileName,
        IEnumerable<string> args,
        string workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
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

    private static bool ValidateMediaBackendAutoStartPreflight(
        string repoRoot,
        string autoStartRaw,
        string executablePathRaw,
        string workingDirectoryRaw,
        out string resolvedExecutablePath,
        out string? error)
    {
        resolvedExecutablePath = executablePathRaw;
        error = null;

        if (!TryParseOptionalBool(autoStartRaw))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(executablePathRaw))
        {
            error = "HIDBRIDGE_EDGE_PROXY_MEDIABACKENDAUTOSTART=true requires HIDBRIDGE_EDGE_PROXY_MEDIABACKENDEXECUTABLEPATH.";
            return false;
        }

        var resolvedWorkingDirectory = ResolveWorkingDirectory(repoRoot, workingDirectoryRaw);
        if (!string.IsNullOrWhiteSpace(resolvedWorkingDirectory) && !Directory.Exists(resolvedWorkingDirectory))
        {
            error = $"Configured media backend working directory does not exist: {resolvedWorkingDirectory}";
            return false;
        }

        if (!TryResolveExecutablePath(executablePathRaw, resolvedWorkingDirectory, out resolvedExecutablePath))
        {
            var scope = string.IsNullOrWhiteSpace(resolvedWorkingDirectory)
                ? "PATH/current directory"
                : $"'{resolvedWorkingDirectory}' or PATH";
            error = $"Media backend executable '{executablePathRaw}' was not found in {scope}.";
            return false;
        }

        return true;
    }

    private static string? ResolveWorkingDirectory(string repoRoot, string configuredWorkingDirectory)
    {
        if (string.IsNullOrWhiteSpace(configuredWorkingDirectory))
        {
            return null;
        }

        if (Path.IsPathRooted(configuredWorkingDirectory))
        {
            return Path.GetFullPath(configuredWorkingDirectory);
        }

        return Path.GetFullPath(Path.Combine(repoRoot, configuredWorkingDirectory));
    }

    private static bool TryResolveExecutablePath(
        string executablePath,
        string? workingDirectory,
        out string resolvedPath)
    {
        resolvedPath = executablePath;
        var candidate = executablePath.Trim();
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        var treatAsPath = candidate.Contains(Path.DirectorySeparatorChar)
            || candidate.Contains(Path.AltDirectorySeparatorChar)
            || Path.IsPathRooted(candidate);

        if (treatAsPath)
        {
            var pathCandidate = Path.IsPathRooted(candidate)
                ? candidate
                : Path.Combine(workingDirectory ?? Directory.GetCurrentDirectory(), candidate);
            pathCandidate = Path.GetFullPath(pathCandidate);
            if (File.Exists(pathCandidate))
            {
                resolvedPath = pathCandidate;
                return true;
            }

            if (OperatingSystem.IsWindows() && !pathCandidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                var exeCandidate = $"{pathCandidate}.exe";
                if (File.Exists(exeCandidate))
                {
                    resolvedPath = exeCandidate;
                    return true;
                }
            }

            return false;
        }

        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            var localCandidate = Path.Combine(workingDirectory, candidate);
            if (File.Exists(localCandidate))
            {
                resolvedPath = localCandidate;
                return true;
            }

            if (OperatingSystem.IsWindows() && !localCandidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                var localExeCandidate = $"{localCandidate}.exe";
                if (File.Exists(localExeCandidate))
                {
                    resolvedPath = localExeCandidate;
                    return true;
                }
            }
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var entry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var fullCandidate = Path.Combine(entry, candidate);
            if (File.Exists(fullCandidate))
            {
                resolvedPath = fullCandidate;
                return true;
            }

            if (OperatingSystem.IsWindows() && !fullCandidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                var fullExeCandidate = $"{fullCandidate}.exe";
                if (File.Exists(fullExeCandidate))
                {
                    resolvedPath = fullExeCandidate;
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryParseOptionalBool(string? value)
    {
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
                return true;
            case "0":
            case "false":
            case "no":
            case "off":
                return false;
            default:
                return false;
        }
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
        var waitTask = process.WaitForExitAsync();
        var exited = await Task.WhenAny(waitTask, Task.Delay(TimeSpan.FromSeconds(20))) == waitTask;
        if (!exited)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // best effort
            }
            Console.WriteLine("WARNING: StopExisting process cleanup timed out after 20s and was terminated.");
            return;
        }
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
