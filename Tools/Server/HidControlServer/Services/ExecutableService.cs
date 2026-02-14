namespace HidControlServer.Services;

using InfraExecutableService = HidControl.Infrastructure.Services.ExecutableService;

/// <summary>
/// Resolves and validates executable paths.
/// </summary>
internal static class ExecutableService
{
    /// <summary>
    /// Validates an executable path or PATH lookup.
    /// </summary>
    /// <param name="path">Executable path or name.</param>
    /// <param name="error">Validation error.</param>
    /// <returns>True if valid.</returns>
    public static bool TryValidateExecutablePath(string path, out string error)
    {
        return InfraExecutableService.TryValidateExecutablePath(path, out error);
    }

    /// <summary>
    /// Attempts to resolve an executable by searching PATH.
    /// </summary>
    /// <param name="exe">Executable name.</param>
    /// <param name="resolvedPath">Resolved full path.</param>
    /// <returns>True if found.</returns>
    public static bool TryFindExecutableOnPath(string exe, out string resolvedPath)
    {
        return InfraExecutableService.TryFindExecutableOnPath(exe, out resolvedPath);
    }
}
