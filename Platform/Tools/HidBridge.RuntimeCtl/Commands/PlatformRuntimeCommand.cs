using System;
using System.ComponentModel;
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
/// Implements the <c>PlatformRuntimeCommand</c> RuntimeCtl lane.
/// Execution contract:
/// 1) Parse CLI arguments into strongly typed options.
/// 2) Execute lane-specific orchestration (native RuntimeCtl commands first, script bridge only when explicitly needed).
/// 3) Persist step logs/summaries under <c>Platform/.logs</c> and return process-style exit code semantics (0 = success).
/// </summary>
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
                        await WaitHttpReadyAsync("Media backend API", "http://127.0.0.1:19851/api/v1/versions", options.ReadyTimeoutSec);
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
                        await WaitHttpReadyAsync("Media backend API", "http://127.0.0.1:19851/api/v1/versions", options.ReadyTimeoutSec);
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
        Console.WriteLine("Media API:    http://127.0.0.1:19851/api/v1/versions");
        Console.WriteLine("Media WHEP:   http://127.0.0.1:19851/rtc/v1/whep/?app=live&stream=edge-main");
        Console.WriteLine("Media WHIP:   http://127.0.0.1:19851/rtc/v1/whip/?app=live&stream=edge-main");
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
