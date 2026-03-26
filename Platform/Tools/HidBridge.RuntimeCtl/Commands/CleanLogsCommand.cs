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
/// Implements the <c>CleanLogsCommand</c> RuntimeCtl lane.
/// Execution contract:
/// 1) Parse CLI arguments into strongly typed options.
/// 2) Execute lane-specific orchestration (native RuntimeCtl commands first, script bridge only when explicitly needed).
/// 3) Persist step logs/summaries under <c>Platform/.logs</c> and return process-style exit code semantics (0 = success).
/// </summary>
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
