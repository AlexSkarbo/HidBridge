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
/// Implements the <c>TestsCommand</c> RuntimeCtl lane.
/// Execution contract:
/// 1) Parse CLI arguments into strongly typed options.
/// 2) Execute lane-specific orchestration (native RuntimeCtl commands first, script bridge only when explicitly needed).
/// 3) Persist step logs/summaries under <c>Platform/.logs</c> and return process-style exit code semantics (0 = success).
/// </summary>
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
