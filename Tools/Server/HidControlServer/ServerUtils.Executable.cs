using HidControlServer.Services;

namespace HidControlServer;

// Executable resolution/validation helpers.
/// <summary>
/// Provides server utility helpers for executable.
/// </summary>
internal static partial class ServerUtils
{
    /// <summary>
    /// Tries to validate executable path.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <param name="error">The error.</param>
    /// <returns>Result.</returns>
    public static bool TryValidateExecutablePath(string path, out string error)
    {
        return ExecutableService.TryValidateExecutablePath(path, out error);
    }

    /// <summary>
    /// Tries to find executable on path.
    /// </summary>
    /// <param name="exe">The exe.</param>
    /// <param name="resolvedPath">The resolvedPath.</param>
    /// <returns>Result.</returns>
    public static bool TryFindExecutableOnPath(string exe, out string resolvedPath)
    {
        return ExecutableService.TryFindExecutableOnPath(exe, out resolvedPath);
    }
}
