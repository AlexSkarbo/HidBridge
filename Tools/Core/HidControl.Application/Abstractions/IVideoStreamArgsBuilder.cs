using HidControl.Contracts;

namespace HidControl.Application.Abstractions;

/// <summary>
/// Builds FFmpeg stream arguments for a source given current pipeline state.
/// </summary>
public interface IVideoStreamArgsBuilder
{
    /// <summary>
    /// Builds FFmpeg arguments to run the stream pipeline for the specified source.
    /// </summary>
    /// <param name="enabledSources">Enabled sources list (used for index/ports and multi-output decisions).</param>
    /// <param name="source">Source for which to build args.</param>
    /// <param name="outputState">Current output state.</param>
    /// <returns>Argument string.</returns>
    string BuildArgs(IReadOnlyList<VideoSourceConfig> enabledSources, VideoSourceConfig source, VideoOutputState outputState);
}

