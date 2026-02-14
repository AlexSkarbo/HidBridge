using System.Collections.Concurrent;
using System.Buffers;
using System.Diagnostics;
using System.Text;
using HidControl.UseCases;
using HidControl.UseCases.Video;

namespace HidControlServer.Services;

/// <summary>
/// Orchestrates FFmpeg processes and stream pumping.
/// </summary>
internal static class FfmpegPipelineService
{
    /// <summary>
    /// Starts FFmpeg processes for all enabled sources.
    /// </summary>
    /// <param name="opt">Options.</param>
    /// <param name="profiles">Profile store.</param>
    /// <param name="sources">Video sources.</param>
    /// <param name="appState">Application state.</param>
    /// <param name="outputState">Output state.</param>
    /// <param name="ffmpegProcesses">Tracked processes.</param>
    /// <param name="ffmpegStates">Tracked states.</param>
    /// <param name="restart">Restart running processes.</param>
    /// <param name="started">Started results.</param>
    /// <param name="errors">Error list.</param>
    /// <param name="force">Skip restarts if already running.</param>
    public static void StartFfmpegForSources(Options opt,
        VideoProfileStore profiles,
        IReadOnlyList<VideoSourceConfig> sources,
        AppState appState,
        VideoOutputState outputState,
        ConcurrentDictionary<string, Process> ffmpegProcesses,
        ConcurrentDictionary<string, FfmpegProcState> ffmpegStates,
        bool restart,
        List<object> started,
        List<string> errors,
        bool force)
    {
        if (!ExecutableService.TryValidateExecutablePath(opt.FfmpegPath, out string ffmpegError))
        {
            errors.Add($"ffmpeg: {ffmpegError}");
            return;
        }
        foreach (VideoSourceConfig src in sources)
        {
            if (!src.Enabled) continue;
            if (appState.VideoCaptureWorkers.TryGetValue(src.Id, out var worker) &&
                worker is not null &&
                worker.IsRunning &&
                !worker.CurrentInputIsRtsp)
            {
                errors.Add($"capture_worker_running {src.Id}");
                continue;
            }
            if (ffmpegProcesses.TryGetValue(src.Id, out var existing) && !existing.HasExited)
            {
                if (force) continue;
                if (!restart) continue;
                try { existing.Kill(true); } catch { }
            }
            string? input = VideoInputService.BuildFfmpegInput(src);
            if (string.IsNullOrWhiteSpace(input))
            {
                errors.Add($"source_os_mismatch {src.Id}");
                continue;
            }
            string args = VideoInputService.BuildFfmpegStreamArgs(opt, profiles, sources, src, appState, outputState);
            if (string.IsNullOrWhiteSpace(args))
            {
                errors.Add($"no ffmpeg outputs for {src.Id}");
                continue;
            }
            Directory.CreateDirectory(opt.VideoLogDir);
            string logPath = Path.Combine(opt.VideoLogDir, $"ffmpeg_{src.Id}.log");
            bool captureLockHeld = false;
            if (NeedsCaptureLock(src))
            {
                int lockTimeout = Math.Clamp(opt.VideoCaptureLockTimeoutMs, 100, 30_000);
                if (!appState.VideoCaptureLock.Wait(lockTimeout))
                {
                    errors.Add($"capture_busy {src.Id}");
                    continue;
                }
                captureLockHeld = true;
            }

            var psi = new ProcessStartInfo
            {
                FileName = opt.FfmpegPath,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            if (!string.IsNullOrWhiteSpace(opt.VideoHlsDir) &&
                args.Contains("-f hls", StringComparison.OrdinalIgnoreCase))
            {
                psi.WorkingDirectory = VideoUrlService.BuildHlsDir(opt, src.Id);
            }
            try
            {
                VideoFrameHub? hub = null;
                FlvStreamHub? flvHub = null;
                if (!opt.VideoSecondaryCaptureEnabled)
                {
                    if (outputState.Flv)
                    {
                        var existingHub = appState.FlvStreamHubs.GetOrAdd(src.Id, _ => new FlvStreamHub());
                        existingHub.ResetForNewStream();
                        flvHub = existingHub;
                    }
                    else if (outputState.Mjpeg)
                    {
                        hub = appState.VideoFrameHubs.GetOrAdd(src.Id, _ => new VideoFrameHub());
                    }
                }
                var proc = Process.Start(psi);
                if (proc is null)
                {
                    errors.Add($"failed to start ffmpeg for {src.Id}");
                    if (captureLockHeld)
                    {
                        try { appState.VideoCaptureLock.Release(); } catch { }
                    }
                    continue;
                }
                ffmpegProcesses[src.Id] = proc;
                var state = ffmpegStates.GetOrAdd(src.Id, _ => new FfmpegProcState());
                state.LastStartAt = DateTimeOffset.UtcNow;
                state.LogPath = logPath;
                state.Args = args;
                state.CaptureLockHeld = captureLockHeld;
                state.CaptureLockAt = captureLockHeld ? DateTimeOffset.UtcNow : null;
                _ = Task.Run(() =>
                {
                    try
                    {
                        proc.WaitForExit();
                        ffmpegProcesses.TryRemove(src.Id, out _);
                        if (ffmpegStates.TryGetValue(src.Id, out var st))
                        {
                            st.LastExitAt = DateTimeOffset.UtcNow;
                            st.LastExitCode = proc.ExitCode;
                            st.CaptureLockHeld = false;
                        }
                        ServerEventLog.Log("ffmpeg", "exit", new { id = src.Id, code = proc.ExitCode });
                        if (proc.ExitCode != 0 &&
                            ffmpegStates.TryGetValue(src.Id, out var exitState) &&
                            !string.IsNullOrWhiteSpace(exitState.LogPath))
                        {
                            var tail = ReadTailLines(exitState.LogPath, maxLines: 10, maxBytes: 64 * 1024);
                            if (tail.Length > 0)
                            {
                                ServerEventLog.Log("ffmpeg", "exit_tail", new { id = src.Id, tail });
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        ServerEventLog.Log("ffmpeg", "exit_error", new { id = src.Id, error = ex.Message });
                    }
                });
                _ = Task.Run(async () =>
                {
                    var writeLock = new object();
                    try
                    {
                        await using var fs = new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
                        await using var writer = new StreamWriter(fs, Encoding.ASCII, 1024, leaveOpen: true);
                        await writer.WriteLineAsync($"# ffmpeg pid={proc.Id} args={args}");
                        if (flvHub is not null)
                        {
                            await writer.WriteLineAsync("# stdout=flv");
                        }
                        else if (hub is not null)
                        {
                            await writer.WriteLineAsync("# stdout=mjpeg");
                        }
                        await writer.FlushAsync();
                        Task stdout = flvHub is not null
                            ? PumpFlvTagsAsync(opt, proc.StandardOutput.BaseStream, flvHub, CancellationToken.None)
                            : hub is null
                                ? PumpProcessStreamAsync("stdout", proc.StandardOutput, writer, writeLock)
                                : PumpMjpegFramesAsync(opt, proc.StandardOutput.BaseStream, hub, CancellationToken.None);
                        Task stderr = PumpProcessStreamAsync("stderr", proc.StandardError, writer, writeLock);
                        await Task.WhenAll(stdout, stderr);
                    }
                    catch { }
                    finally
                    {
                        if (captureLockHeld)
                        {
                            try { appState.VideoCaptureLock.Release(); } catch { }
                            if (ffmpegStates.TryGetValue(src.Id, out var st))
                            {
                                st.CaptureLockHeld = false;
                            }
                        }
                    }
                });
                started.Add(new { id = src.Id, status = "started", pid = proc.Id, logPath });
            }
            catch (Exception ex)
            {
                errors.Add($"{src.Id}: {ex.Message}");
                if (captureLockHeld)
                {
                    try { appState.VideoCaptureLock.Release(); } catch { }
                }
            }
        }
    }

    private static string[] ReadTailLines(string path, int maxLines, int maxBytes)
    {
        if (maxLines <= 0 || maxBytes <= 0) return Array.Empty<string>();
        if (!File.Exists(path)) return Array.Empty<string>();
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length == 0) return Array.Empty<string>();
            int toRead = (int)Math.Min(fs.Length, maxBytes);
            fs.Seek(-toRead, SeekOrigin.End);
            byte[] buffer = ArrayPool<byte>.Shared.Rent(toRead);
            try
            {
                int read = fs.Read(buffer, 0, toRead);
                if (read <= 0) return Array.Empty<string>();
                string text = Encoding.UTF8.GetString(buffer, 0, read);
                string[] lines = text.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length <= maxLines) return lines;
                return lines[^maxLines..];
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static async Task PumpMjpegFramesAsync(Options opt, Stream stream, VideoFrameHub hub, CancellationToken ct)
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
                int read = await stream.ReadAsync(buffer.AsMemory(0, bufferSize), ct);
                if (read <= 0)
                {
                    break;
                }
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
                            hub.Publish(frame.ToArray());
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

    private static async Task PumpFlvTagsAsync(Options opt, Stream stream, FlvStreamHub hub, CancellationToken ct)
    {
        int bufferSize = Math.Clamp(opt.VideoFlvBufferBytes, 32 * 1024, 4 * 1024 * 1024);
        int readBufferSize = Math.Clamp(opt.VideoFlvReadBufferBytes, 8 * 1024, 1024 * 1024);
        var buffer = new FlvParserService.ByteBuffer(bufferSize, bufferSize);
        byte[] readBuf = ArrayPool<byte>.Shared.Rent(readBufferSize);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int read = await stream.ReadAsync(readBuf.AsMemory(0, readBufferSize), ct);
                if (read <= 0)
                {
                    ServerEventLog.Log("flv", "pump_end", new { reason = "eof" });
                    break;
                }
                buffer.Append(readBuf.AsSpan(0, read));

                if (!hub.HasHeader && buffer.TryReadHeader(out var header))
                {
                    hub.SetHeader(header);
                }

                while (true)
                {
                    var result = buffer.TryReadTag(
                        out var tag,
                        out bool isMeta,
                        out bool isKeyframe,
                        out bool isVideoConfig,
                        out int timestampMs,
                        out byte tagType);
                    if (result == FlvParserService.TagReadResult.NeedMore)
                    {
                        break;
                    }
                    if (result == FlvParserService.TagReadResult.Read)
                    {
                        // Do not synthesize keyframes here; it can break FLV playback.
                        hub.PublishTag(tag, isMeta, isKeyframe, isVideoConfig, timestampMs, tagType);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            ServerEventLog.Log("flv", "pump_end", new { reason = "canceled" });
        }
        catch (Exception ex)
        {
            ServerEventLog.Log("flv", "pump_error", new { error = ex.Message });
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(readBuf);
        }
    }

    private static async Task PumpProcessStreamAsync(string name, StreamReader reader, StreamWriter writer, object writeLock)
    {
        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync()) is not null)
            {
                lock (writeLock)
                {
                    writer.WriteLine($"{DateTimeOffset.UtcNow:O} {name}: {line}");
                    writer.Flush();
                }
            }
        }
        catch
        {
        }
    }

    private static bool NeedsCaptureLock(VideoSourceConfig source)
    {
        string? input = VideoInputService.BuildFfmpegInput(source);
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }
        if (input.Contains("rtsp://", StringComparison.OrdinalIgnoreCase) ||
            input.Contains("http://", StringComparison.OrdinalIgnoreCase) ||
            input.Contains("https://", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        return input.Contains("dshow", StringComparison.OrdinalIgnoreCase) ||
            input.Contains("v4l2", StringComparison.OrdinalIgnoreCase) ||
            input.Contains("avfoundation", StringComparison.OrdinalIgnoreCase) ||
            input.Contains("gdigrab", StringComparison.OrdinalIgnoreCase) ||
            input.Contains("x11grab", StringComparison.OrdinalIgnoreCase);
    }
}
