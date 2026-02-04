using HidControl.Application.Abstractions;
using HidControl.Application.Models;
using System.Collections.Generic;
using System.Linq;

namespace HidControl.Application.UseCases;

/// <summary>
/// Returns client-facing stream URLs for enabled video sources.
/// </summary>
public sealed class GetVideoStreamsUseCase
{
    private readonly IVideoSourceStore _sources;
    private readonly IVideoOptionsProvider _options;
    private readonly IVideoUrlBuilder _urls;
    private readonly IVideoInputBuilder _inputs;

    /// <summary>
    /// Creates a new instance.
    /// </summary>
    public GetVideoStreamsUseCase(
        IVideoSourceStore sources,
        IVideoOptionsProvider options,
        IVideoUrlBuilder urls,
        IVideoInputBuilder inputs)
    {
        _sources = sources;
        _options = options;
        _urls = urls;
        _inputs = inputs;
    }

    /// <summary>
    /// Executes the use case.
    /// </summary>
    public IReadOnlyList<VideoStreamInfo> Execute()
    {
        var opt = _options.GetVideoOptions();
        string baseUrl = _urls.GetVideoBaseUrl(opt);

        var enabled = _sources.GetAll().Where(s => s.Enabled).ToList();
        var list = new List<VideoStreamInfo>(enabled.Count);
        for (int i = 0; i < enabled.Count; i++)
        {
            var s = enabled[i];
            string rtspUrl = _urls.BuildRtspClientUrl(opt, s.Id);
            string srtUrl = _urls.BuildSrtClientUrl(opt, i);
            string rtmpUrl = _urls.BuildRtmpClientUrl(opt, s.Id);
            string hlsUrl = _urls.BuildHlsUrl(opt, s.Id);

            string mjpegUrl = $"{baseUrl}/video/mjpeg/{s.Id}";
            string flvUrl = $"{baseUrl}/video/flv/{s.Id}";
            string snapshotUrl = $"{baseUrl}/video/snapshot/{s.Id}";

            string? ffmpegInput = _inputs.BuildFfmpegInput(s);

            list.Add(new VideoStreamInfo(
                Id: s.Id,
                Name: s.Name,
                Kind: s.Kind,
                Url: s.Url,
                Enabled: s.Enabled,
                RtspUrl: rtspUrl,
                SrtUrl: srtUrl,
                RtmpUrl: rtmpUrl,
                HlsUrl: hlsUrl,
                MjpegUrl: mjpegUrl,
                FlvUrl: flvUrl,
                SnapshotUrl: snapshotUrl,
                FfmpegInput: ffmpegInput));
        }

        return list;
    }
}
