using HidControlServer.Services;

namespace HidControlServer;

// FLV tag parsing utilities and AVC config extraction for WS-FLV.
/// <summary>
/// Provides server utility helpers for flv.
/// </summary>
internal static partial class ServerUtils
{
    /// <summary>
    /// Tries to build avc config tag from keyframe.
    /// </summary>
    /// <param name="tag">The tag.</param>
    /// <param name="configTag">The configTag.</param>
    /// <returns>Result.</returns>
    internal static bool TryBuildAvcConfigTagFromKeyframe(byte[] tag, out byte[] configTag)
    {
        return FlvParserService.TryBuildAvcConfigTagFromKeyframe(tag, out configTag);
    }
}
