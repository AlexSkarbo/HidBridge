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
/// Implements the <c>ArtifactExportCommand</c> RuntimeCtl lane.
/// Execution contract:
/// 1) Parse CLI arguments into strongly typed options.
/// 2) Execute lane-specific orchestration (native RuntimeCtl commands first, script bridge only when explicitly needed).
/// 3) Persist step logs/summaries under <c>Platform/.logs</c> and return process-style exit code semantics (0 = success).
/// </summary>
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
