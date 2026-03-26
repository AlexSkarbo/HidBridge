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
/// Implements the <c>ChecksCommand</c> RuntimeCtl lane.
/// Execution contract:
/// 1) Parse CLI arguments into strongly typed options.
/// 2) Execute lane-specific orchestration (native RuntimeCtl commands first, script bridge only when explicitly needed).
/// 3) Persist step logs/summaries under <c>Platform/.logs</c> and return process-style exit code semantics (0 = success).
/// </summary>
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

                const int smokeMaxAttempts = 3;
                var smokeDelay = TimeSpan.FromSeconds(3);
                var smokeExitCode = 1;
                for (var smokeAttempt = 1; smokeAttempt <= smokeMaxAttempts; smokeAttempt++)
                {
                    smokeExitCode = await BearerSmokeCommand.RunAsync(platformRoot, smokeArgs);
                    if (smokeExitCode == 0)
                    {
                        break;
                    }

                    if (smokeAttempt < smokeMaxAttempts)
                    {
                        Console.WriteLine(
                            $"WARN bearer-smoke retry attempt={smokeAttempt}/{smokeMaxAttempts} failed (exit={smokeExitCode}); waiting {smokeDelay.TotalSeconds:0}s before retry.");
                        await Task.Delay(smokeDelay);
                    }
                }
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
        public string AuthAuthority { get; set; } = "http://host.docker.internal:18096/realms/hidbridge-dev";
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
