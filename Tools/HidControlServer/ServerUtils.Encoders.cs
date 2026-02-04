using HidControl.UseCases;
using HidControlServer.Services;

namespace HidControlServer;

// Encoder probing and profile fallbacks (NVENC/AMF/V4L2/CPU).
/// <summary>
/// Provides server utility helpers for encoders.
/// </summary>
internal static partial class ServerUtils
{
    /// <summary>
    /// Ensures supported encoder.
    /// </summary>
    /// <param name="opt">The opt.</param>
    /// <param name="profiles">The profiles.</param>
    /// <param name="profileArgs">The profileArgs.</param>
    /// <returns>Result.</returns>
    private static string EnsureSupportedEncoder(Options opt, VideoProfileStore profiles, string profileArgs)
    {
        return VideoProfileService.EnsureSupportedEncoder(opt, profiles, profileArgs);
    }
}
