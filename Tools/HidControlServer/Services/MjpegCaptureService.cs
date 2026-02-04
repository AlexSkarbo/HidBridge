namespace HidControlServer.Services;

using System.Buffers;

/// <summary>
/// Builds MJPEG capture arguments and decodes MJPEG streams into frames.
/// </summary>
internal static class MjpegCaptureService
{
    /// <summary>
    /// Tries to build FFmpeg arguments for MJPEG capture.
    /// </summary>
    /// <param name="opt">Options.</param>
    /// <param name="source">Video source.</param>
    /// <param name="appState">Application state.</param>
    /// <param name="fps">Requested FPS.</param>
    /// <param name="width">Requested width.</param>
    /// <param name="height">Requested height.</param>
    /// <param name="quality">Requested quality.</param>
    /// <param name="passthrough">True to copy MJPEG without re-encode.</param>
    /// <param name="input">Resolved FFmpeg input string.</param>
    /// <param name="args">Output FFmpeg arguments.</param>
    /// <param name="needsLock">True if capture lock is required.</param>
    /// <param name="error">Error message.</param>
    /// <returns>True if args were built.</returns>
    public static bool TryBuildCaptureArgs(
        Options opt,
        VideoSourceConfig source,
        AppState appState,
        int? fps,
        int? width,
        int? height,
        int? quality,
        bool passthrough,
        out string? input,
        out string args,
        out bool needsLock,
        out string? error)
    {
        input = VideoInputService.BuildMjpegInput(opt, source, appState);
        args = string.Empty;
        error = null;
        if (string.IsNullOrWhiteSpace(input))
        {
            error = "no input (source_os_mismatch?)";
            needsLock = false;
            return false;
        }

        bool isRtsp = input.Contains("rtsp://", StringComparison.OrdinalIgnoreCase);
        needsLock = !isRtsp;

        if (passthrough)
        {
            args = $"{input} -c:v copy -f mjpeg pipe:1";
            return true;
        }

        int fpsValue = Math.Clamp(fps ?? 30, 1, 60);
        int qualityValue = Math.Clamp(quality ?? 5, 2, 20);
        string? scale = null;
        if (width is > 0 || height is > 0)
        {
            int sw = width is > 0 ? width.Value : -1;
            int sh = height is > 0 ? height.Value : -1;
            scale = $"scale={sw}:{sh}";
        }
        string vf = scale is null ? $"fps={fpsValue}" : $"fps={fpsValue},{scale}";
        args = $"{input} -vf {vf} -q:v {qualityValue} -f mjpeg pipe:1";
        return true;
    }

    /// <summary>
    /// Pumps MJPEG frames from a stream into a callback.
    /// </summary>
    /// <param name="opt">Options.</param>
    /// <param name="stream">Input stream.</param>
    /// <param name="onFrame">Frame callback.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Task.</returns>
    public static async Task PumpMjpegFramesAsync(Options opt, Stream stream, Action<byte[]> onFrame, CancellationToken ct)
    {
        int bufferSize = Math.Clamp(opt.VideoMjpegReadBufferBytes, 4 * 1024, 1024 * 1024);
        int maxFrameBytes = Math.Clamp(opt.VideoMjpegMaxFrameBytes, 256 * 1024, 16 * 1024 * 1024);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        try
        {
            int prev = -1;
            bool inFrame = false;
            var frame = new MemoryStream();
            while (!ct.IsCancellationRequested)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
                if (read <= 0) break;
                for (int i = 0; i < read; i++)
                {
                    int b = buffer[i];
                    if (!inFrame)
                    {
                        if (prev == 0xFF && b == 0xD8)
                        {
                            inFrame = true;
                            frame.SetLength(0);
                            frame.WriteByte(0xFF);
                            frame.WriteByte(0xD8);
                        }
                    }
                    else
                    {
                        frame.WriteByte((byte)b);
                        if (frame.Length > maxFrameBytes)
                        {
                            inFrame = false;
                            frame.SetLength(0);
                            prev = -1;
                            continue;
                        }
                        if (prev == 0xFF && b == 0xD9)
                        {
                            onFrame(frame.ToArray());
                            inFrame = false;
                        }
                    }
                    prev = b;
                }
            }
        }
        catch
        {
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
