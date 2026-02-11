using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using HidControl.Contracts;

namespace HidControl.Infrastructure.Services;

/// <summary>
/// Resolves video profiles and encoder availability.
/// </summary>
/// <summary>
/// Resolves video profiles and encoder availability.
/// </summary>
public static class VideoProfileService
{
    private sealed record EncoderOption(string Id, string Label, string FfmpegEncoder, string[] ExtraArgs);

    private static readonly ConcurrentDictionary<string, HashSet<string>> EncoderCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly string[] KnownEncoders = new[] { "h264_nvenc", "h264_amf", "h264_v4l2m2m", "libx264" };
    private static readonly ConcurrentDictionary<string, (DateTime AtUtc, IReadOnlyList<(string Id, string Label)> Encoders)> WebRtcEncoderProbeCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Ensures a supported encoder is used in profile arguments using pipeline options.
    /// </summary>
    /// <param name="opt">Video pipeline options.</param>
    /// <param name="profiles">Profiles list.</param>
    /// <param name="profileArgs">Profile arguments.</param>
    /// <returns>Adjusted arguments.</returns>
    public static string EnsureSupportedEncoder(VideoPipelineOptions opt, IReadOnlyList<VideoProfileConfig> profiles, string profileArgs)
    {
        return EnsureSupportedEncoderInternal(opt.FfmpegPath, profiles, profileArgs);
    }

    /// <summary>
    /// Lists available WebRTC encoder modes for the current host by probing ffmpeg.
    /// </summary>
    /// <param name="ffmpegPath">Ffmpeg executable path.</param>
    /// <returns>Available encoder mode ids and labels.</returns>
    public static IReadOnlyList<(string Id, string Label)> ListAvailableWebRtcEncoders(string ffmpegPath)
    {
        string key = $"{ffmpegPath}|{Environment.OSVersion.Platform}";
        if (WebRtcEncoderProbeCache.TryGetValue(key, out var cached))
        {
            if ((DateTime.UtcNow - cached.AtUtc) < TimeSpan.FromSeconds(30))
            {
                return cached.Encoders;
            }
        }

        var result = ProbeAvailableWebRtcEncoders(ffmpegPath);
        WebRtcEncoderProbeCache[key] = (DateTime.UtcNow, result);
        return result;
    }

    private static string EnsureSupportedEncoderInternal(string ffmpegPath, IReadOnlyList<VideoProfileConfig> profiles, string profileArgs)
    {
        if (string.IsNullOrWhiteSpace(profileArgs))
        {
            return profileArgs;
        }
        string lower = profileArgs.ToLowerInvariant();
        string? required = null;
        if (lower.Contains("h264_nvenc"))
        {
            required = "h264_nvenc";
        }
        else if (lower.Contains("h264_amf"))
        {
            required = "h264_amf";
        }
        else if (lower.Contains("h264_v4l2m2m"))
        {
            required = "h264_v4l2m2m";
        }
        else if (lower.Contains("libx264"))
        {
            required = "libx264";
        }

        if (string.IsNullOrWhiteSpace(required) || EncoderAvailable(ffmpegPath, required))
        {
            return profileArgs;
        }

        foreach (string enc in KnownEncoders)
        {
            if (!EncoderAvailable(ffmpegPath, enc))
            {
                continue;
            }
            VideoProfileConfig? match = profiles
                .FirstOrDefault(p => p.Args.Contains(enc, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match.Args;
            }
        }

        (string? w, string? h) = ExtractScale(profileArgs);
        string? fps = ExtractFps(profileArgs);
        return BuildCpuFallbackArgs(w, h, fps);
    }

    private static string BuildCpuFallbackArgs(string? w, string? h, string? fps)
    {
        string scale = !string.IsNullOrWhiteSpace(w) && !string.IsNullOrWhiteSpace(h)
            ? $" -vf scale={w}:{h}"
            : string.Empty;
        string rate = !string.IsNullOrWhiteSpace(fps) ? $" -r {fps}" : string.Empty;
        return "-c:v libx264 -preset veryfast -tune zerolatency -g 30 -keyint_min 30 -sc_threshold 0 -bf 0 -pix_fmt yuv420p -b:v 3500k -maxrate 3500k -bufsize 1750k" + scale + rate;
    }

    private static string? ExtractFps(string args)
    {
        if (string.IsNullOrWhiteSpace(args))
        {
            return null;
        }
        var match = Regex.Match(args, "-r\\s+(\\S+)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static bool EncoderAvailable(string ffmpegPath, string encoder)
    {
        try
        {
            HashSet<string> encoders = EncoderCache.GetOrAdd(ffmpegPath, static path => ProbeEncoders(path));
            return encoders.Contains(encoder);
        }
        catch
        {
            return true;
        }
    }

    private static HashSet<string> ProbeEncoders(string ffmpegPath)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = "-hide_banner -encoders",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var proc = Process.Start(psi);
        if (proc is null)
        {
            return set;
        }
        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        try { proc.WaitForExit(2000); } catch { }
        string combined = stdout + "\n" + stderr;
        foreach (string enc in KnownEncoders)
        {
            if (combined.Contains(enc, StringComparison.OrdinalIgnoreCase))
            {
                set.Add(enc);
            }
        }
        return set;
    }

    private static IReadOnlyList<(string Id, string Label)> ProbeAvailableWebRtcEncoders(string ffmpegPath)
    {
        // Keep CPU always first; hardware modes are appended when they pass probe.
        var list = new List<(string Id, string Label)> { ("cpu", "CPU (software)") };
        foreach (var option in GetWebRtcEncoderCandidates())
        {
            if (TryProbeEncoder(ffmpegPath, option))
            {
                list.Add((option.Id, option.Label));
            }
        }
        return list;
    }

    private static IEnumerable<EncoderOption> GetWebRtcEncoderCandidates()
    {
        if (OperatingSystem.IsWindows())
        {
            yield return new EncoderOption("nvenc", "NVIDIA NVENC", "h264_nvenc", Array.Empty<string>());
            yield return new EncoderOption("amf", "AMD AMF", "h264_amf", Array.Empty<string>());
            yield return new EncoderOption("qsv", "Intel QSV", "h264_qsv", new[] { "-look_ahead", "0" });
            yield break;
        }

        if (OperatingSystem.IsLinux())
        {
            yield return new EncoderOption("v4l2m2m", "V4L2 M2M", "h264_v4l2m2m", Array.Empty<string>());
            yield return new EncoderOption("vaapi", "VAAPI", "h264_vaapi", Array.Empty<string>());
        }
    }

    private static bool TryProbeEncoder(string ffmpegPath, EncoderOption option)
    {
        try
        {
            // Quick synthetic encode probe to validate actual runtime availability.
            var args = new List<string>
            {
                "-hide_banner",
                "-loglevel", "error",
                "-f", "lavfi",
                "-i", "testsrc=size=320x180:rate=5",
                "-frames:v", "1",
                "-an",
                "-c:v", option.FfmpegEncoder
            };
            args.AddRange(option.ExtraArgs);
            args.AddRange(new[] { "-f", "null", "-" });

            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = string.Join(' ', args.Select(static a => a.Contains(' ') ? $"\"{a}\"" : a)),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var proc = Process.Start(psi);
            if (proc is null)
            {
                return false;
            }
            if (!proc.WaitForExit(4000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return false;
            }
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static (string? w, string? h) ExtractScale(string args)
    {
        if (string.IsNullOrWhiteSpace(args)) return (null, null);
        var match = Regex.Match(args, "scale=(\\d+):(\\d+)", RegexOptions.IgnoreCase);
        if (!match.Success) return (null, null);
        return (match.Groups[1].Value, match.Groups[2].Value);
    }
}
