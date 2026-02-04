namespace HidControl.Infrastructure.Services;

/// <summary>
/// Resolves and validates executable paths.
/// </summary>
public static class ExecutableService
{
    /// <summary>
    /// Validates an executable path or PATH lookup.
    /// </summary>
    /// <param name="path">Executable path or name.</param>
    /// <param name="error">Validation error.</param>
    /// <returns>True if valid.</returns>
    public static bool TryValidateExecutablePath(string path, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            error = "path is empty";
            return false;
        }
        bool looksLikePath = Path.IsPathRooted(path) ||
            path.Contains(Path.DirectorySeparatorChar) ||
            path.Contains(Path.AltDirectorySeparatorChar);
        if (!looksLikePath)
        {
            if (!TryFindExecutableOnPath(path, out _))
            {
                error = $"not found on PATH: {path}";
                return false;
            }
            return true;
        }
        if (looksLikePath && !File.Exists(path))
        {
            error = $"not found: {path}";
            return false;
        }
        return true;
    }

    /// <summary>
    /// Attempts to resolve an executable by searching PATH.
    /// </summary>
    /// <param name="exe">Executable name.</param>
    /// <param name="resolvedPath">Resolved full path.</param>
    /// <returns>True if found.</returns>
    public static bool TryFindExecutableOnPath(string exe, out string resolvedPath)
    {
        resolvedPath = string.Empty;
        if (string.IsNullOrWhiteSpace(exe)) return false;
        string fileName = exe;
        if (OperatingSystem.IsWindows() && !fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            fileName += ".exe";
        }
        string? pathVar = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathVar)) return false;
        foreach (string raw in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string dir = raw.Trim();
            if (string.IsNullOrWhiteSpace(dir)) continue;
            try
            {
                string candidate = Path.Combine(dir, fileName);
                if (File.Exists(candidate))
                {
                    resolvedPath = candidate;
                    return true;
                }
            }
            catch
            {
            }
        }
        return false;
    }
}
