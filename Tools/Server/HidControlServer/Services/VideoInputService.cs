using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using HidControl.Contracts;
using HidControl.UseCases;
using HidControl.UseCases.Video;

namespace HidControlServer.Services;

/// <summary>
/// Builds FFmpeg inputs and output arguments for video sources.
/// </summary>
internal static class VideoInputService
{
    /// <summary>
    /// Builds FFmpeg input arguments for a source.
    /// </summary>
    /// <param name="source">Video source.</param>
    /// <returns>Input arguments or null if incompatible.</returns>
    public static string? BuildFfmpegInput(VideoSourceConfig source)
    {
        if (!string.IsNullOrWhiteSpace(source.FfmpegInputOverride))
        {
            string overrideInput = source.FfmpegInputOverride;
            return IsInputCompatibleWithOs(overrideInput) ? overrideInput : null;
        }
        string kind = source.Kind?.Trim().ToLowerInvariant() ?? string.Empty;
        string url = source.Url ?? string.Empty;
        if (kind == "uvc")
        {
            if (url.StartsWith("/dev/video", StringComparison.OrdinalIgnoreCase))
            {
                string input = $"-f v4l2 -i {url}";
                return IsInputCompatibleWithOs(input) ? input : null;
            }
            if (url.StartsWith("win:", StringComparison.OrdinalIgnoreCase))
            {
                string? name = source.Name;
                if (!string.IsNullOrWhiteSpace(name))
                {
                    string input = $"-f dshow -i video=\"{name}\"";
                    return IsInputCompatibleWithOs(input) ? input : null;
                }
                string deviceId = url.Substring(4);
                string inputDevice = $"-f dshow -i video=\"{deviceId}\"";
                return IsInputCompatibleWithOs(inputDevice) ? inputDevice : null;
            }
            string fallback = $"-i {url}";
            return IsInputCompatibleWithOs(fallback) ? fallback : null;
        }
        if (kind == "lavfi" || kind == "test")
        {
            string spec = string.IsNullOrWhiteSpace(url) ? "testsrc2=size=1280x720:rate=30" : url;
            string input = $"-f lavfi -i {spec}";
            return IsInputCompatibleWithOs(input) ? input : null;
        }
        if (kind == "screen")
        {
            if (OperatingSystem.IsWindows())
            {
                string input = "-f gdigrab -i desktop";
                return IsInputCompatibleWithOs(input) ? input : null;
            }
            if (OperatingSystem.IsLinux())
            {
                string target = string.IsNullOrWhiteSpace(url) ? ":0.0" : url;
                string input = $"-f x11grab -i {target}";
                return IsInputCompatibleWithOs(input) ? input : null;
            }
            if (OperatingSystem.IsMacOS())
            {
                string target = string.IsNullOrWhiteSpace(url) ? "1" : url;
                string input = $"-f avfoundation -i {target}";
                return IsInputCompatibleWithOs(input) ? input : null;
            }
            return null;
        }
        if (kind == "rtsp")
        {
            string input = $"-rtsp_transport tcp -i {url}";
            return IsInputCompatibleWithOs(input) ? input : null;
        }
        if (kind == "http" || kind == "https" || kind == "hls")
        {
            string input = $"-i {url}";
            return IsInputCompatibleWithOs(input) ? input : null;
        }
        if (!string.IsNullOrWhiteSpace(url))
        {
            string input = $"-i {url}";
            return IsInputCompatibleWithOs(input) ? input : null;
        }
        return null;
    }

    /// <summary>
    /// Builds FFmpeg input for MJPEG capture.
    /// </summary>
    /// <param name="opt">Options.</param>
    /// <param name="source">Video source.</param>
    /// <param name="appState">Application state.</param>
    /// <returns>Input arguments or null.</returns>
    public static string? BuildMjpegInput(Options opt, VideoSourceConfig source, AppState appState)
    {
        return BuildMjpegInput(opt.ToVideoPipelineOptions(), source, appState);
    }

    /// <summary>
    /// Builds FFmpeg input for MJPEG capture using pipeline options.
    /// </summary>
    /// <param name="opt">Video pipeline options.</param>
    /// <param name="source">Video source.</param>
    /// <param name="appState">Application state.</param>
    /// <returns>Input arguments or null.</returns>
    public static string? BuildMjpegInput(VideoPipelineOptions opt, VideoSourceConfig source, AppState appState)
    {
        if (opt.VideoRtspPort > 0 && HasRtspStreamProcess(opt, source, appState))
        {
            string rtsp = VideoUrlService.BuildRtspClientUrl(opt, source.Id);
            return $"-rtsp_transport tcp -i {QuoteArg(rtsp)}";
        }
        return BuildFfmpegInput(source);
    }

    /// <summary>
    /// Builds full FFmpeg stream arguments for a source and outputs.
    /// </summary>
    /// <param name="opt">Options.</param>
    /// <param name="profiles">Profile store.</param>
    /// <param name="sources">Video sources.</param>
    /// <param name="src">Source.</param>
    /// <param name="appState">Application state.</param>
    /// <param name="outputState">Output state.</param>
    /// <returns>Argument string or empty.</returns>
    public static string BuildFfmpegStreamArgs(Options opt, VideoProfileStore profiles, IReadOnlyList<VideoSourceConfig> sources, VideoSourceConfig src, AppState appState, VideoOutputState outputState)
    {
        return BuildFfmpegStreamArgs(opt.ToVideoPipelineOptions(), profiles, sources, src, appState, outputState);
    }

    /// <summary>
    /// Builds full FFmpeg stream arguments for a source and outputs using pipeline options.
    /// </summary>
    /// <param name="opt">Video pipeline options.</param>
    /// <param name="profiles">Profile store.</param>
    /// <param name="sources">Video sources.</param>
    /// <param name="src">Source.</param>
    /// <param name="appState">Application state.</param>
    /// <param name="outputState">Output state.</param>
    /// <returns>Argument string or empty.</returns>
    public static string BuildFfmpegStreamArgs(VideoPipelineOptions opt, VideoProfileStore profiles, IReadOnlyList<VideoSourceConfig> sources, VideoSourceConfig src, AppState appState, VideoOutputState outputState)
    {
        string? input = BuildFfmpegInput(src);
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }
        int index = VideoConfigService.GetVideoSourceIndex(sources, src);
        string profileArgs = profiles.GetActiveArgs().Trim();
        profileArgs = VideoProfileService.EnsureSupportedEncoder(opt, profiles, profileArgs);
        if (!string.IsNullOrWhiteSpace(profileArgs))
        {
            var lower = profileArgs.ToLowerInvariant();
            if (lower.Contains("h264_nvenc"))
            {
                if (!lower.Contains("rc-lookahead"))
                {
                    profileArgs += " -rc-lookahead 0";
                }
                if (!lower.Contains("-g "))
                {
                    profileArgs += " -g 30";
                }
                if (!lower.Contains("bf "))
                {
                    profileArgs += " -bf 0";
                }
                if (!lower.Contains("no-scenecut"))
                {
                    profileArgs += " -no-scenecut 1";
                }
            }
            else if (lower.Contains("h264_amf"))
            {
                if (!lower.Contains("usage "))
                {
                    profileArgs += " -usage lowlatency";
                }
                if (!lower.Contains("-g "))
                {
                    profileArgs += " -g 30";
                }
                if (!lower.Contains("bf "))
                {
                    profileArgs += " -bf 0";
                }
            }
            else if (lower.Contains("h264_v4l2m2m"))
            {
                if (!lower.Contains("bf "))
                {
                    profileArgs += " -bf 0";
                }
                if (!lower.Contains("-g "))
                {
                    profileArgs += " -g 30";
                }
            }
            else if (lower.Contains("libx264"))
            {
                if (!lower.Contains("tune zerolatency"))
                {
                    profileArgs += " -tune zerolatency";
                }
                if (!lower.Contains("sc_threshold"))
                {
                    profileArgs += " -sc_threshold 0";
                }
                if (!lower.Contains("keyint_min"))
                {
                    profileArgs += " -keyint_min 30";
                }
                if (!lower.Contains("bf "))
                {
                    profileArgs += " -bf 0";
                }
            }
        }
        bool enableHls = outputState.Hls;
        bool enableMjpeg = outputState.Mjpeg;
        bool enableFlv = outputState.Flv;
        bool enableMjpegPassthrough = outputState.MjpegPassthrough && enableMjpeg;
        int mjpegFps = Math.Clamp(outputState.MjpegFps, 1, 60);
        IReadOnlyList<VideoMode>? cachedModes = null;
        bool supportsMjpeg = true;
        int cacheTtlSeconds = opt.VideoModesCacheTtlSeconds;
        if (cacheTtlSeconds > 0 && appState.VideoModesCache.TryGetValue(src.Id, out var cached))
        {
            if ((DateTimeOffset.UtcNow - cached.CachedAt) <= TimeSpan.FromSeconds(cacheTtlSeconds))
            {
                cachedModes = cached.Modes;
                supportsMjpeg = cached.SupportsMjpeg;
                if (!supportsMjpeg)
                {
                    enableMjpegPassthrough = false;
                }
            }
        }
        if (OperatingSystem.IsWindows() && input.StartsWith("-f dshow", StringComparison.OrdinalIgnoreCase))
        {
            input = AugmentDshowInput(
                input,
                ref profileArgs,
                enableMjpegPassthrough,
                mjpegFps,
                outputState.MjpegSize,
                cachedModes,
                supportsMjpeg);
        }
        string WithProfile(string output)
        {
            return string.IsNullOrWhiteSpace(profileArgs) ? output : $"{profileArgs} {output}";
        }
        var outputs = new List<string>();

        if (enableHls && opt.VideoRtspPort > 0)
        {
            string rtsp = VideoUrlService.BuildRtspListenUrl(opt, src.Id);
            outputs.Add(WithProfile($"-map 0:v:0 -an -f rtsp -rtsp_flags listen -rtsp_transport tcp {QuoteArg(rtsp)}"));
        }
        if (enableHls && opt.VideoSrtBasePort > 0)
        {
            string srt = VideoUrlService.BuildSrtListenUrl(opt, index);
            outputs.Add(WithProfile($"-map 0:v:0 -an -f mpegts {QuoteArg(srt)}"));
        }
        if (enableHls && opt.VideoRtmpPort > 0)
        {
            string rtmp = VideoUrlService.BuildRtmpListenUrl(opt, src.Id);
            outputs.Add(WithProfile($"-map 0:v:0 -an -f flv -listen 1 {QuoteArg(rtmp)}"));
        }
        if (enableFlv)
        {
            string flvSafeBsf = "";
            // Avoid nested quoting on Windows; keep syntax consistent with HLS builder.
            string flvForceKey = $" -force_key_frames {QuoteArg("expr:gte(t,n_forced*1)")}";
            if (!string.IsNullOrWhiteSpace(profileArgs))
            {
                var lower = profileArgs.ToLowerInvariant();
                bool isH264 = lower.Contains("h264_nvenc") || lower.Contains("h264_amf") || lower.Contains("h264_v4l2m2m") || lower.Contains("libx264");
                bool hasDumpExtra = lower.Contains("dump_extra");
                if (isH264 && !hasDumpExtra)
                {
                    flvSafeBsf = " -bsf:v dump_extra";
                }
            }
            outputs.Add(WithProfile($"-map 0:v:0 -an -fflags nobuffer -flags low_delay -flush_packets 1 -max_delay 0 -muxdelay 0 -muxpreload 0{flvForceKey}{flvSafeBsf} -f flv pipe:1"));
        }
        if (enableHls && !string.IsNullOrWhiteSpace(opt.VideoHlsDir))
        {
            string outDir = VideoUrlService.BuildHlsDir(opt, src.Id);
            Directory.CreateDirectory(outDir);
            string outPath = "index.m3u8";
            string segExt = opt.VideoLlHls ? "m4s" : "ts";
            string segName = $"seg_%06d.{segExt}";
            string initName = "init.mp4";
            string flags = "append_list";
            if (opt.VideoHlsDeleteSegments)
            {
                flags += "+delete_segments";
            }
            if (opt.VideoLlHls)
            {
                flags += "+independent_segments";
            }
            string segType = opt.VideoLlHls ? "fmp4" : "mpegts";
            var hls = new StringBuilder();
            hls.Append("-map 0:v:0 -an ");
            int segSeconds = Math.Clamp(opt.VideoHlsSegmentSeconds, 1, 10);
            hls.Append($"-force_key_frames {QuoteArg($"expr:gte(t,n_forced*{segSeconds})")} ");
            hls.Append("-f hls ");
            hls.Append($"-hls_time {segSeconds} ");
            hls.Append($"-hls_list_size {Math.Clamp(opt.VideoHlsListSize, 3, 20)} ");
            hls.Append($"-hls_segment_type {segType} ");
            hls.Append($"-hls_flags {flags} ");
            hls.Append($"-hls_segment_filename {QuoteArg(segName)} ");
            if (opt.VideoLlHls)
            {
                hls.Append($"-hls_fmp4_init_filename {QuoteArg(initName)} ");
            }
            hls.Append(QuoteArg(outPath));
            outputs.Add(WithProfile(hls.ToString()));
        }
        if (enableMjpeg && !opt.VideoSecondaryCaptureEnabled)
        {
            if (enableMjpegPassthrough)
            {
                outputs.Add("-map 0:v:0 -an -c:v copy -f mjpeg pipe:1");
            }
            else
            {
                outputs.Add($"-map 0:v:0 -an -vf fps={mjpegFps} -q:v 5 -f mjpeg pipe:1");
            }
        }

        if (outputs.Count == 0)
        {
            return string.Empty;
        }

        return $"{input} {string.Join(" ", outputs)}";
    }

    private static bool IsInputCompatibleWithOs(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return true;
        if (OperatingSystem.IsWindows())
        {
            return !input.Contains("v4l2", StringComparison.OrdinalIgnoreCase) &&
                !input.Contains("/dev/video", StringComparison.OrdinalIgnoreCase) &&
                !input.Contains("avfoundation", StringComparison.OrdinalIgnoreCase) &&
                !input.Contains("x11grab", StringComparison.OrdinalIgnoreCase);
        }
        if (OperatingSystem.IsLinux())
        {
            return !input.Contains("dshow", StringComparison.OrdinalIgnoreCase) &&
                !input.Contains("gdigrab", StringComparison.OrdinalIgnoreCase) &&
                !input.Contains("avfoundation", StringComparison.OrdinalIgnoreCase);
        }
        if (OperatingSystem.IsMacOS())
        {
            return !input.Contains("dshow", StringComparison.OrdinalIgnoreCase) &&
                !input.Contains("gdigrab", StringComparison.OrdinalIgnoreCase) &&
                !input.Contains("x11grab", StringComparison.OrdinalIgnoreCase) &&
                !input.Contains("v4l2", StringComparison.OrdinalIgnoreCase) &&
                !input.Contains("/dev/video", StringComparison.OrdinalIgnoreCase);
        }
        return true;
    }

    private static bool HasRtspStreamProcess(VideoPipelineOptions opt, VideoSourceConfig source, AppState appState)
    {
        if (appState.FfmpegProcesses.TryGetValue(source.Id, out var proc) &&
            proc is not null &&
            !proc.HasExited)
        {
            return true;
        }

        if (!opt.VideoKillOrphanFfmpeg)
        {
            return false;
        }

        string listenUrl = VideoUrlService.BuildRtspListenUrl(opt, source.Id);
        foreach (var info in FfmpegProcessManager.ListFfmpegProcessInfos())
        {
            if (!FfmpegProcessManager.CmdMatchesSource(info.CommandLine, source))
            {
                continue;
            }
            if (info.CommandLine.Contains(listenUrl, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string QuoteArg(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "\"\"";
        if (value.Contains(' ') || value.Contains('"'))
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }
        return value;
    }

    private static string AugmentDshowInput(
        string input,
        ref string profileArgs,
        bool mjpegPassthrough,
        int? mjpegFps,
        string? mjpegSize,
        IReadOnlyList<VideoMode>? modes,
        bool supportsMjpeg)
    {
        string rtbuf = ExtractArgValue(ref profileArgs, "-rtbufsize");
        string fps = ExtractArgValue(ref profileArgs, "-r");
        (string? w, string? h) = ExtractScale(profileArgs);
        if (mjpegPassthrough && supportsMjpeg && modes is { Count: > 0 })
        {
            (int? reqW, int? reqH) = ParseMjpegSize(mjpegSize);
            if (!reqW.HasValue && !reqH.HasValue)
            {
                if (int.TryParse(w, out int fallbackW) && int.TryParse(h, out int fallbackH))
                {
                    reqW = fallbackW;
                    reqH = fallbackH;
                }
            }
            VideoMode? bestMode = SelectClosestMode(modes, reqW, reqH);
            if (bestMode is not null)
            {
                if (!input.Contains("-video_size", StringComparison.OrdinalIgnoreCase))
                {
                    input = InsertBeforeInput(input, $"-video_size {bestMode.Width}x{bestMode.Height}");
                }
                if (!input.Contains("-vcodec", StringComparison.OrdinalIgnoreCase))
                {
                    input = InsertBeforeInput(input, "-vcodec mjpeg");
                }
            }
        }
        if (!string.IsNullOrWhiteSpace(rtbuf))
        {
            input = InsertBeforeInput(input, $"-rtbufsize {rtbuf}");
        }
        if (!string.IsNullOrWhiteSpace(fps))
        {
            input = InsertBeforeInput(input, $"-framerate {fps}");
        }
        else if (mjpegPassthrough && mjpegFps.HasValue)
        {
            input = InsertBeforeInput(input, $"-framerate {mjpegFps.Value}");
        }
        return input;
    }

    private static string InsertBeforeInput(string input, string value)
    {
        int idx = input.IndexOf(" -i ", StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return $"{input} {value}";
        }
        return input.Insert(idx, " " + value);
    }

    private static string ExtractArgValue(ref string args, string key)
    {
        if (string.IsNullOrWhiteSpace(args)) return string.Empty;
        var match = Regex.Match(args, $"{Regex.Escape(key)}\\s+(\\S+)", RegexOptions.IgnoreCase);
        if (!match.Success) return string.Empty;
        string value = match.Groups[1].Value;
        args = Regex.Replace(args, $"{Regex.Escape(key)}\\s+\\S+", "", RegexOptions.IgnoreCase).Trim();
        return value;
    }

    private static (string? w, string? h) ExtractScale(string args)
    {
        if (string.IsNullOrWhiteSpace(args)) return (null, null);
        var match = Regex.Match(args, @"scale=(\d+)[:x](\d+)", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            return (match.Groups[1].Value, match.Groups[2].Value);
        }
        match = Regex.Match(args, @"-s\s+(\d+)x(\d+)", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            return (match.Groups[1].Value, match.Groups[2].Value);
        }
        return (null, null);
    }

    private static (int? w, int? h) ParseMjpegSize(string? size)
    {
        if (string.IsNullOrWhiteSpace(size)) return (null, null);
        var match = Regex.Match(size.Trim(), @"(\d+)[x:](\d+)", RegexOptions.IgnoreCase);
        if (!match.Success) return (null, null);
        if (int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int w) &&
            int.TryParse(match.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int h))
        {
            return (w, h);
        }
        return (null, null);
    }

    private static VideoMode? SelectClosestMode(IReadOnlyList<VideoMode> modes, int? reqW, int? reqH)
    {
        if (modes.Count == 0) return null;
        if (!reqW.HasValue && !reqH.HasValue)
        {
            return modes
                .OrderByDescending(m => m.MaxFps)
                .ThenByDescending(m => m.Width * m.Height)
                .FirstOrDefault();
        }
        int targetW = reqW ?? 0;
        int targetH = reqH ?? 0;
        return modes
            .OrderBy(m => Math.Abs(m.Width - targetW) + Math.Abs(m.Height - targetH))
            .ThenByDescending(m => m.MaxFps)
            .FirstOrDefault();
    }
}
