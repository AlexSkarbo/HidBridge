using HidBridge.ControlPlane.Web.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace HidBridge.ControlPlane.Web.Services;

/// <summary>
/// Resolves recent operational artifacts produced by local scripts.
/// </summary>
public sealed class OperationalArtifactService
{
    private static readonly string[] DoctorPreferredFiles = ["doctor.log"];
    private static readonly string[] CiLocalPreferredFiles = ["Bearer-Smoke.log", "Checks-Sql.log", "Doctor.log"];
    private static readonly string[] ChecksPreferredFiles = ["Platform-smoke-Sql.log", "Platform-smoke-File.log", "Platform-unit-tests.log"];
    private static readonly string[] FullPreferredFiles = ["CI-Local.log", "Realm-Sync.log"];
    private static readonly string[] SmokePreferredFiles = ["smoke.summary.txt", "smoke.result.json", "smoke.failure.log", "controlplane.stderr.log", "controlplane.stdout.log"];

    private readonly string _platformRoot;
    private readonly string[] _allowedRoots;
    private readonly bool _isAvailable;

    /// <summary>
    /// Creates the artifact service.
    /// </summary>
    public OperationalArtifactService(
        IWebHostEnvironment environment,
        IConfiguration configuration,
        ILogger<OperationalArtifactService> logger)
    {
        var configuredRoot = (configuration["HIDBRIDGE_PLATFORM_ROOT"] ?? configuration["HidBridge:PlatformRoot"] ?? string.Empty).Trim();
        if (TryResolvePlatformRoot(environment.ContentRootPath, configuredRoot, out var resolvedRoot))
        {
            _platformRoot = resolvedRoot;
            _isAvailable = true;
        }
        else
        {
            _platformRoot = Path.GetFullPath(environment.ContentRootPath);
            _isAvailable = false;
            logger.LogWarning(
                "Operational artifacts are disabled because Platform root could not be resolved. " +
                "ContentRootPath={ContentRootPath}. Set HIDBRIDGE_PLATFORM_ROOT to enable artifact discovery.",
                environment.ContentRootPath);
        }

        _allowedRoots = BuildAllowedRoots(_platformRoot);
    }

    /// <summary>
    /// Reads the latest operational artifact summary.
    /// </summary>
    public OperationalArtifactSummaryViewModel GetSummary()
    {
        if (!_isAvailable)
        {
            return new OperationalArtifactSummaryViewModel(
                _platformRoot,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null);
        }

        return new(
            _platformRoot,
            ReadLatestGroup("doctor", "Doctor", DoctorPreferredFiles),
            ReadLatestGroup("ci-local", "CI Local", CiLocalPreferredFiles),
            ReadLatestGroup("checks", "Checks", ChecksPreferredFiles),
            ReadLatestSmokeGroup(),
            ReadLatestGroup("full", "Full", FullPreferredFiles),
            ReadLatestGroup("token-debug", "Token Debug", []),
            ReadLatestBearerRolloutGroup(),
            ReadLatestGroup("identity-reset", "Identity Reset", []));
    }

    /// <summary>
    /// Resolves one safe full path for download.
    /// </summary>
    public string ResolveSafePath(string relativePath)
    {
        if (!_isAvailable)
        {
            throw new InvalidOperationException("Operational artifacts are unavailable. Set HIDBRIDGE_PLATFORM_ROOT to enable artifact downloads.");
        }

        var normalized = relativePath
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar);

        var fullPath = Path.GetFullPath(Path.Combine(_platformRoot, normalized));
        if (_allowedRoots.All(root => !fullPath.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("Requested artifact is outside the allowed roots.");
        }

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Artifact not found.", fullPath);
        }

        return fullPath;
    }

    private OperationalArtifactGroupViewModel? ReadLatestBearerRolloutGroup()
    {
        var logsRoot = Path.Combine(_platformRoot, ".logs");
        if (!Directory.Exists(logsRoot))
        {
            return null;
        }

        var latestDirectory = new DirectoryInfo(logsRoot)
            .EnumerateDirectories("bearer-rollout-phase*")
            .SelectMany(static category => category.EnumerateDirectories())
            .OrderByDescending(static directory => directory.LastWriteTimeUtc)
            .FirstOrDefault();

        return latestDirectory is null
            ? null
            : BuildGroup("Bearer Rollout", latestDirectory, []);
    }

    private OperationalArtifactGroupViewModel? ReadLatestSmokeGroup()
    {
        var smokeRoot = Path.Combine(_platformRoot, ".smoke-data");
        if (!Directory.Exists(smokeRoot))
        {
            return null;
        }

        var latestDirectory = new DirectoryInfo(smokeRoot)
            .EnumerateDirectories()
            .SelectMany(static provider => provider.EnumerateDirectories())
            .OrderByDescending(static run => run.LastWriteTimeUtc)
            .FirstOrDefault();

        if (latestDirectory is null)
        {
            return null;
        }

        var provider = latestDirectory.Parent?.Name;
        var displayName = string.IsNullOrWhiteSpace(provider)
            ? "Smoke"
            : $"Smoke ({provider})";

        return BuildGroup(displayName, latestDirectory, SmokePreferredFiles);
    }

    private OperationalArtifactGroupViewModel? ReadLatestGroup(string category, string displayName, IReadOnlyList<string> preferredFileNames)
    {
        var categoryRoot = Path.Combine(_platformRoot, ".logs", category);
        if (!Directory.Exists(categoryRoot))
        {
            return null;
        }

        var latestDirectory = new DirectoryInfo(categoryRoot)
            .EnumerateDirectories()
            .OrderByDescending(static directory => directory.LastWriteTimeUtc)
            .FirstOrDefault();

        return latestDirectory is null
            ? null
            : BuildGroup(displayName, latestDirectory, preferredFileNames);
    }

    private OperationalArtifactGroupViewModel BuildGroup(string displayName, DirectoryInfo directory, IReadOnlyList<string> preferredFileNames)
    {
        var files = directory.EnumerateFiles("*", SearchOption.TopDirectoryOnly)
            .OrderBy(static file => file.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var links = files
            .Select(file => new OperationalArtifactLinkViewModel(
                file.Name,
                ToRelativePath(file.FullName),
                file.LastWriteTimeUtc,
                file.Length))
            .ToArray();

        var preferredLink = ResolvePreferredLink(links, preferredFileNames);

        return new OperationalArtifactGroupViewModel(
            displayName,
            directory.Name,
            ToRelativePath(directory.FullName),
            directory.LastWriteTimeUtc,
            preferredLink,
            links);
    }

    private static OperationalArtifactLinkViewModel? ResolvePreferredLink(
        IReadOnlyList<OperationalArtifactLinkViewModel> links,
        IReadOnlyList<string> preferredFileNames)
    {
        foreach (var preferredFileName in preferredFileNames)
        {
            var match = links.FirstOrDefault(link =>
                string.Equals(link.Name, preferredFileName, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        return links.FirstOrDefault();
    }

    private string ToRelativePath(string fullPath)
        => Path.GetRelativePath(_platformRoot, fullPath).Replace(Path.DirectorySeparatorChar, '/');

    private static string[] BuildAllowedRoots(string platformRoot)
        =>
        [
            Path.Combine(platformRoot, ".logs"),
            Path.Combine(platformRoot, ".smoke-data"),
            Path.Combine(platformRoot, "Artifacts"),
            Path.Combine(platformRoot, "Identity", "Keycloak", "backups"),
        ];

    private static bool TryResolvePlatformRoot(string contentRootPath, string configuredRoot, out string platformRoot)
    {
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            var candidate = Path.GetFullPath(configuredRoot);
            if (Directory.Exists(candidate))
            {
                platformRoot = candidate;
                return true;
            }
        }

        foreach (var candidate in EnumerateAncestorPaths(contentRootPath))
        {
            if (string.Equals(Path.GetFileName(candidate), "Platform", StringComparison.OrdinalIgnoreCase))
            {
                platformRoot = candidate;
                return true;
            }

            var childPlatform = Path.Combine(candidate, "Platform");
            if (Directory.Exists(childPlatform))
            {
                platformRoot = childPlatform;
                return true;
            }
        }

        platformRoot = string.Empty;
        return false;
    }

    private static IEnumerable<string> EnumerateAncestorPaths(string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrWhiteSpace(current))
        {
            yield return current;
            var parent = Directory.GetParent(current);
            if (parent is null)
            {
                yield break;
            }

            current = parent.FullName;
        }
    }
}
