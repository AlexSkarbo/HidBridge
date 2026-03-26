using System.Diagnostics;
using System.Text;

/// <summary>
/// Implements phased bearer-fallback rollout presets and validation lanes.
/// </summary>
internal static class BearerRolloutCommand
{
    private static readonly IReadOnlyDictionary<int, PhasePreset> Presets = new Dictionary<int, PhasePreset>
    {
        [0] = new("Phase 0", "current baseline", string.Empty),
        [1] = new("Phase 1", "control mutation paths", "/api/v1/sessions/*/control/request;/api/v1/sessions/*/control/grant;/api/v1/sessions/*/control/force-takeover;/api/v1/sessions/*/control/release"),
        [2] = new("Phase 2", "control + invitation mutation paths", "/api/v1/sessions/*/control/request;/api/v1/sessions/*/control/grant;/api/v1/sessions/*/control/force-takeover;/api/v1/sessions/*/control/release;/api/v1/sessions/*/invitations/requests;/api/v1/sessions/*/invitations/*/approve;/api/v1/sessions/*/invitations/*/decline"),
        [3] = new("Phase 3", "control + invitation + share/participant mutation paths", "/api/v1/sessions/*/control/request;/api/v1/sessions/*/control/grant;/api/v1/sessions/*/control/force-takeover;/api/v1/sessions/*/control/release;/api/v1/sessions/*/invitations/requests;/api/v1/sessions/*/invitations/*/approve;/api/v1/sessions/*/invitations/*/decline;/api/v1/sessions/*/participants;/api/v1/sessions/*/participants/*;/api/v1/sessions/*/shares;/api/v1/sessions/*/shares/*/accept;/api/v1/sessions/*/shares/*/reject;/api/v1/sessions/*/shares/*/revoke"),
        [4] = new("Phase 4", "control + invitation + share/participant + command mutation paths", "/api/v1/sessions/*/control/request;/api/v1/sessions/*/control/grant;/api/v1/sessions/*/control/force-takeover;/api/v1/sessions/*/control/release;/api/v1/sessions/*/invitations/requests;/api/v1/sessions/*/invitations/*/approve;/api/v1/sessions/*/invitations/*/decline;/api/v1/sessions/*/participants;/api/v1/sessions/*/participants/*;/api/v1/sessions/*/shares;/api/v1/sessions/*/shares/*/accept;/api/v1/sessions/*/shares/*/reject;/api/v1/sessions/*/shares/*/revoke;/api/v1/sessions/*/commands"),
    };

    public static async Task<int> RunAsync(string platformRoot, IReadOnlyList<string> args)
    {
        if (!Options.TryParse(args, out var options, out var parseError))
        {
            Console.Error.WriteLine($"bearer-rollout options error: {parseError}");
            return 1;
        }

        var requestedPreset = Presets[options.Phase];
        if (options.PrintOnly)
        {
            Console.WriteLine($"$env:HIDBRIDGE_AUTH_HEADER_FALLBACK_DISABLED_PATTERNS=\"{requestedPreset.Value}\"");
            return 0;
        }

        Environment.SetEnvironmentVariable("HIDBRIDGE_AUTH_HEADER_FALLBACK_DISABLED_PATTERNS", requestedPreset.Value);
        Console.WriteLine($"Applied bearer rollout {requestedPreset.Name} preset for {requestedPreset.Label}.");
        Console.WriteLine($"HIDBRIDGE_AUTH_HEADER_FALLBACK_DISABLED_PATTERNS={requestedPreset.Value}");
        if (options.ApplyOnly)
        {
            Console.WriteLine();
            Console.WriteLine("Apply-only mode complete.");
            return 0;
        }

        var initialAttempt = await ValidatePhaseAsync(platformRoot, options.Phase, string.Empty, options);
        if (!initialAttempt.Failed)
        {
            PrintAttemptSummary(initialAttempt, "Bearer Rollout Summary");
            return 0;
        }

        if (options.NoAutoRollback || options.Phase <= 1)
        {
            PrintAttemptSummary(initialAttempt, "Bearer Rollout Summary");
            return 1;
        }

        Console.WriteLine();
        Console.WriteLine($"{initialAttempt.Preset.Name} failed. Starting auto-rollback search.");
        for (var fallbackPhase = options.Phase - 1; fallbackPhase >= 0; fallbackPhase--)
        {
            var fallbackAttempt = await ValidatePhaseAsync(platformRoot, fallbackPhase, "-rollback", options);
            if (!fallbackAttempt.Failed)
            {
                Console.WriteLine();
                Console.WriteLine($"Requested {initialAttempt.Preset.Name} failed. Effective rollout remains {fallbackAttempt.Preset.Name}.");
                PrintAttemptSummary(fallbackAttempt, $"Bearer Rollout Fallback Summary ({fallbackAttempt.Preset.Name})");
                return 0;
            }

            Console.WriteLine($"Rollback candidate {fallbackAttempt.Preset.Name} failed.");
        }

        PrintAttemptSummary(initialAttempt, "Bearer Rollout Summary (all fallback phases failed)");
        return 1;
    }

    private static async Task<RolloutAttempt> ValidatePhaseAsync(string platformRoot, int phase, string suffix, Options options)
    {
        var preset = Presets[phase];
        Environment.SetEnvironmentVariable("HIDBRIDGE_AUTH_HEADER_FALLBACK_DISABLED_PATTERNS", preset.Value);

        var logRoot = Path.Combine(platformRoot, ".logs", $"bearer-rollout-phase{phase}{suffix}", DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(logRoot);

        var results = new List<StepResult>();
        var failed = false;

        if (!options.SkipDoctor)
        {
            var doctor = await RunRuntimeCtlStepToLogAsync(platformRoot, logRoot, "Doctor", "doctor", Array.Empty<string>(), preset.Value);
            results.Add(doctor);
            failed |= doctor.ExitCode != 0;
        }

        if (!options.SkipSmoke && !failed)
        {
            var smoke = await RunRuntimeCtlStepToLogAsync(platformRoot, logRoot, "Bearer Smoke", "smoke-bearer", Array.Empty<string>(), preset.Value);
            results.Add(smoke);
            failed |= smoke.ExitCode != 0;
        }

        if (!options.SkipFull && !failed)
        {
            var full = await RunRuntimeCtlStepToLogAsync(platformRoot, logRoot, "Full", "full", Array.Empty<string>(), preset.Value);
            results.Add(full);
            failed |= full.ExitCode != 0;
        }

        return new RolloutAttempt(phase, preset, logRoot, results, failed);
    }

    private static async Task<StepResult> RunRuntimeCtlStepToLogAsync(
        string platformRoot,
        string logRoot,
        string stepName,
        string commandName,
        IReadOnlyList<string> commandArgs,
        string fallbackDisabledPatterns)
    {
        var logPath = Path.Combine(logRoot, $"{stepName.Replace(' ', '-')}.log");
        var runtimeCtlProject = Path.Combine(platformRoot, "Tools", "HidBridge.RuntimeCtl", "HidBridge.RuntimeCtl.csproj");
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = platformRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.Environment["HIDBRIDGE_AUTH_HEADER_FALLBACK_DISABLED_PATTERNS"] = fallbackDisabledPatterns;
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--project");
        startInfo.ArgumentList.Add(runtimeCtlProject);
        startInfo.ArgumentList.Add("--no-build");
        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add("--platform-root");
        startInfo.ArgumentList.Add(platformRoot);
        startInfo.ArgumentList.Add(commandName);
        foreach (var arg in commandArgs)
        {
            startInfo.ArgumentList.Add(arg);
        }

        var sw = Stopwatch.StartNew();
        using var process = Process.Start(startInfo);
        if (process is null)
        {
            await File.WriteAllTextAsync(logPath, "Failed to start dotnet process.");
            return new StepResult(stepName, 1, sw.Elapsed.TotalSeconds, logPath);
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        sw.Stop();

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
        return new StepResult(stepName, process.ExitCode, sw.Elapsed.TotalSeconds, logPath);
    }

    private static void PrintAttemptSummary(RolloutAttempt attempt, string title)
    {
        Console.WriteLine();
        Console.WriteLine($"=== {title} ===");
        Console.WriteLine($"Preset:  {attempt.Preset.Name}");
        Console.WriteLine($"Value:   {attempt.Preset.Value}");
        Console.WriteLine($"LogRoot: {attempt.LogRoot}");
        Console.WriteLine();
        Console.WriteLine($"{"Name",-24} {"Status",-8} {"Seconds",7} {"ExitCode",8}");
        Console.WriteLine(new string('-', 54));
        foreach (var step in attempt.Results)
        {
            var status = step.ExitCode == 0 ? "PASS" : "FAIL";
            Console.WriteLine($"{step.Name,-24} {status,-8} {step.Seconds,7:0.##} {step.ExitCode,8}");
            Console.WriteLine($"  Log: {step.LogPath}");
        }
    }

    private readonly record struct PhasePreset(string Name, string Label, string Value);
    private readonly record struct StepResult(string Name, int ExitCode, double Seconds, string LogPath);
    private readonly record struct RolloutAttempt(int Phase, PhasePreset Preset, string LogRoot, IReadOnlyList<StepResult> Results, bool Failed);

    private sealed class Options
    {
        public int Phase { get; set; }
        public bool PrintOnly { get; set; }
        public bool ApplyOnly { get; set; }
        public bool SkipDoctor { get; set; }
        public bool SkipSmoke { get; set; }
        public bool SkipFull { get; set; }
        public bool NoAutoRollback { get; set; }

        public static bool TryParse(IReadOnlyList<string> args, out Options options, out string? error)
        {
            options = new Options { Phase = 1 };
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
                    error = $"Unexpected token '{token}'.";
                    return false;
                }

                var name = token.TrimStart('-');
                var hasValue = i + 1 < args.Count && !args[i + 1].StartsWith("-", StringComparison.Ordinal);
                var value = hasValue ? args[i + 1] : null;
                switch (name.ToLowerInvariant())
                {
                    case "phase":
                        options.Phase = ParseInt(name, value, hasValue, ref i, ref error);
                        break;
                    case "printonly":
                    case "dryrun":
                        options.PrintOnly = ParseSwitch(name, value, hasValue, ref i, ref error);
                        break;
                    case "applyonly":
                        options.ApplyOnly = ParseSwitch(name, value, hasValue, ref i, ref error);
                        break;
                    case "skipdoctor":
                        options.SkipDoctor = ParseSwitch(name, value, hasValue, ref i, ref error);
                        break;
                    case "skipsmoke":
                        options.SkipSmoke = ParseSwitch(name, value, hasValue, ref i, ref error);
                        break;
                    case "skipfull":
                        options.SkipFull = ParseSwitch(name, value, hasValue, ref i, ref error);
                        break;
                    case "noautorollback":
                        options.NoAutoRollback = ParseSwitch(name, value, hasValue, ref i, ref error);
                        break;
                    default:
                        error = $"Unsupported bearer-rollout option '{token}'.";
                        break;
                }

                if (error is not null)
                {
                    return false;
                }
            }

            if (options.Phase is < 1 or > 4)
            {
                error = "Option -Phase supports values 1..4.";
                return false;
            }

            return true;
        }

        private static int ParseInt(string name, string? value, bool hasValue, ref int index, ref string? error)
        {
            if (!hasValue || string.IsNullOrWhiteSpace(value))
            {
                error = $"Option -{name} requires a value.";
                return 0;
            }

            if (!int.TryParse(value, out var parsed))
            {
                error = $"Option -{name} requires integer value.";
                return 0;
            }

            index++;
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
                error = $"Option -{name} requires true/false when value is supplied.";
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
