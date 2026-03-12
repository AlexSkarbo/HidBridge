using HidBridge.ControlPlane.Web.Models;

namespace HidBridge.ControlPlane.Web.Services;

/// <summary>
/// Resolves recent operational artifacts produced by local scripts.
/// </summary>
public sealed class OperationalArtifactService
{
    private readonly string _platformRoot;
    private readonly string[] _allowedRoots;

    /// <summary>
    /// Creates the artifact service.
    /// </summary>
    public OperationalArtifactService(IWebHostEnvironment environment)
    {
        var clientsRoot = Directory.GetParent(environment.ContentRootPath)
            ?? throw new InvalidOperationException("Unable to resolve Clients directory.");
        var platformRoot = Directory.GetParent(clientsRoot.FullName)
            ?? throw new InvalidOperationException("Unable to resolve Platform directory.");

        _platformRoot = platformRoot.FullName;
        _allowedRoots =
        [
            Path.Combine(_platformRoot, ".logs"),
            Path.Combine(_platformRoot, ".smoke-data"),
            Path.Combine(_platformRoot, "Artifacts"),
            Path.Combine(_platformRoot, "Identity", "Keycloak", "backups"),
        ];
    }

    /// <summary>
    /// Reads the latest operational artifact summary.
    /// </summary>
    public OperationalArtifactSummaryViewModel GetSummary()
        => new(
            _platformRoot,
            ReadLatestGroup("doctor", "doctor.log", "Doctor"),
            ReadLatestGroup("checks", null, "Checks"),
            ReadLatestGroup("full", null, "Full"),
            ReadLatestGroup("token-debug", null, "Token Debug"),
            ReadLatestBearerRolloutGroup(),
            ReadLatestGroup("identity-reset", null, "Identity Reset"));

    /// <summary>
    /// Resolves one safe full path for download.
    /// </summary>
    public string ResolveSafePath(string relativePath)
    {
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
            : BuildGroup("Bearer Rollout", latestDirectory, null);
    }

    private OperationalArtifactGroupViewModel? ReadLatestGroup(string category, string? preferredFileName, string displayName)
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
            : BuildGroup(displayName, latestDirectory, preferredFileName);
    }

    private OperationalArtifactGroupViewModel BuildGroup(string displayName, DirectoryInfo directory, string? preferredFileName)
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

        var preferredLink = !string.IsNullOrWhiteSpace(preferredFileName)
            ? links.FirstOrDefault(link => string.Equals(link.Name, preferredFileName, StringComparison.OrdinalIgnoreCase))
            : null;

        return new OperationalArtifactGroupViewModel(
            displayName,
            directory.Name,
            ToRelativePath(directory.FullName),
            directory.LastWriteTimeUtc,
            preferredLink,
            links);
    }

    private string ToRelativePath(string fullPath)
        => Path.GetRelativePath(_platformRoot, fullPath).Replace(Path.DirectorySeparatorChar, '/');
}
