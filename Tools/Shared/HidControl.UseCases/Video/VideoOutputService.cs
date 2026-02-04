using HidControl.Contracts;

namespace HidControl.UseCases.Video;

// Output policy helper for validating and applying video output requests.
/// <summary>
/// Use case model for VideoOutputService.
/// </summary>
public sealed class VideoOutputService
{
    private readonly IVideoOutputStateStore _store;

    /// <summary>
    /// Executes VideoOutputService.
    /// </summary>
    /// <param name="store">The store.</param>
    public VideoOutputService(IVideoOutputStateStore store)
    {
        _store = store;
    }

    /// <summary>
    /// Gets get.
    /// </summary>
    /// <returns>Result.</returns>
    public VideoOutputState Get() => _store.Get();

    /// <summary>
    /// Tries to apply.
    /// </summary>
    /// <param name="req">The req.</param>
    /// <param name="next">The next.</param>
    /// <param name="error">The error.</param>
    /// <returns>Result.</returns>
    public bool TryApply(VideoOutputRequest req, out VideoOutputState next, out string? error)
    {
        var current = _store.Get();
        bool hls = req.Hls ?? current.Hls;
        bool mjpeg = req.Mjpeg ?? current.Mjpeg;
        bool flv = req.Flv ?? current.Flv;
        bool mjpegPassthrough = req.MjpegPassthrough ?? current.MjpegPassthrough;

        if (!string.IsNullOrWhiteSpace(req.Mode))
        {
            string mode = req.Mode.Trim().ToLowerInvariant();
            if (mode == "hls")
            {
                hls = true;
                mjpeg = false;
                flv = false;
                mjpegPassthrough = false;
            }
            else if (mode == "mjpeg")
            {
                hls = false;
                mjpeg = true;
                flv = false;
                mjpegPassthrough = false;
            }
            else if (mode == "mjpeg-passthrough")
            {
                hls = false;
                mjpeg = true;
                flv = false;
                mjpegPassthrough = true;
            }
            else if (mode == "flv")
            {
                hls = false;
                mjpeg = false;
                flv = true;
                mjpegPassthrough = false;
            }
            else
            {
                next = current;
                error = "invalid_mode";
                return false;
            }
        }

        if (!hls && !mjpeg && !flv)
        {
            next = current;
            error = "no_outputs";
            return false;
        }

        if (!mjpeg)
        {
            mjpegPassthrough = false;
        }

        int mjpegFps = req.MjpegFps.HasValue
            ? Math.Clamp(req.MjpegFps.Value, 1, 60)
            : current.MjpegFps;
        string mjpegSize = !string.IsNullOrWhiteSpace(req.MjpegSize)
            ? req.MjpegSize.Trim()
            : current.MjpegSize;

        next = current with
        {
            Hls = hls,
            Mjpeg = mjpeg,
            Flv = flv,
            MjpegPassthrough = mjpegPassthrough,
            MjpegFps = mjpegFps,
            MjpegSize = mjpegSize
        };

        _store.Set(next);
        error = null;
        return true;
    }

    /// <summary>
    /// Resolves mode.
    /// </summary>
    /// <param name="state">The state.</param>
    /// <returns>Result.</returns>
    public static string ResolveMode(VideoOutputState state)
    {
        if (state.Flv) return "flv";
        if (state.MjpegPassthrough) return "mjpeg-passthrough";
        if (state.Mjpeg) return "mjpeg";
        return "hls";
    }
}
