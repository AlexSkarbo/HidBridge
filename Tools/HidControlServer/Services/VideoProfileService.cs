using HidControl.Contracts;
using HidControl.UseCases;
using InfraVideoProfileService = HidControl.Infrastructure.Services.VideoProfileService;

namespace HidControlServer.Services;

/// <summary>
/// Resolves video profiles and encoder availability.
/// </summary>
internal static class VideoProfileService
{
    /// <summary>
    /// Ensures a supported encoder is used in profile arguments.
    /// </summary>
    /// <param name="opt">Options.</param>
    /// <param name="profiles">Profile store.</param>
    /// <param name="profileArgs">Profile arguments.</param>
    /// <returns>Adjusted arguments.</returns>
    public static string EnsureSupportedEncoder(Options opt, VideoProfileStore profiles, string profileArgs)
    {
        return InfraVideoProfileService.EnsureSupportedEncoder(opt.ToVideoPipelineOptions(), profiles, profileArgs);
    }

    /// <summary>
    /// Ensures a supported encoder is used in profile arguments.
    /// </summary>
    /// <param name="opt">Video pipeline options.</param>
    /// <param name="profiles">Profile store.</param>
    /// <param name="profileArgs">Profile arguments.</param>
    /// <returns>Adjusted arguments.</returns>
    public static string EnsureSupportedEncoder(VideoPipelineOptions opt, VideoProfileStore profiles, string profileArgs)
    {
        return InfraVideoProfileService.EnsureSupportedEncoder(opt, profiles, profileArgs);
    }
}
