using HidControl.Application.Abstractions;
using HidControl.Application.Models;
using System.Collections.Generic;
using System.Linq;

namespace HidControl.Application.UseCases;

/// <summary>
/// Returns generated FFmpeg stream args for enabled sources (diagnostics/export).
/// </summary>
public sealed class GetVideoStreamsConfigUseCase
{
    private readonly IVideoSourceStore _sources;
    private readonly IVideoOutputStateProvider _output;
    private readonly IVideoStreamArgsBuilder _args;

    /// <summary>
    /// Creates a new instance.
    /// </summary>
    public GetVideoStreamsConfigUseCase(IVideoSourceStore sources, IVideoOutputStateProvider output, IVideoStreamArgsBuilder args)
    {
        _sources = sources;
        _output = output;
        _args = args;
    }

    /// <summary>
    /// Executes the use case.
    /// </summary>
    public IReadOnlyList<VideoStreamArgsInfo> Execute()
    {
        var enabled = _sources.GetAll().Where(s => s.Enabled).ToList();
        var state = _output.Get();

        var list = new List<VideoStreamArgsInfo>(enabled.Count);
        foreach (var src in enabled)
        {
            string args = _args.BuildArgs(enabled, src, state);
            list.Add(new VideoStreamArgsInfo(src.Id, args));
        }
        return list;
    }
}
