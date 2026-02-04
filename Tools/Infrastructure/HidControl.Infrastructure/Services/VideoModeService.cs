using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using HidControl.Contracts;

namespace HidControl.Infrastructure.Services;

/// <summary>
/// Discovers DirectShow devices and capture modes.
/// </summary>
/// <summary>
/// Discovers DirectShow devices and capture modes.
/// </summary>
public static class VideoModeService
{
    /// <summary>
    /// Lists DirectShow video devices.
    /// </summary>
    /// <param name="ffmpegPath">FFmpeg path.</param>
    /// <returns>Device list.</returns>
    public static IReadOnlyList<VideoDshowDevice> ListDshowDevices(string ffmpegPath)
    {
        var devices = new List<VideoDshowDevice>();
        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = "-list_devices true -f dshow -i dummy",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var proc = Process.Start(psi);
        if (proc is null)
        {
            throw new InvalidOperationException("ffmpeg_start_failed");
        }

        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(5000);

        bool inVideo = false;
        int lastIndex = -1;
        foreach (string rawLine in stderr.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            string line = rawLine.Trim();
            if (line.Contains("DirectShow video devices", StringComparison.OrdinalIgnoreCase))
            {
                inVideo = true;
                continue;
            }
            if (line.Contains("DirectShow audio devices", StringComparison.OrdinalIgnoreCase))
            {
                inVideo = false;
                continue;
            }
            if (line.Contains("Alternative name", StringComparison.OrdinalIgnoreCase))
            {
                int altFirstQuote = line.IndexOf('"');
                int altLastQuote = line.LastIndexOf('"');
                if (altFirstQuote < 0 || altLastQuote <= altFirstQuote)
                {
                    continue;
                }
                string value = line.Substring(altFirstQuote + 1, altLastQuote - altFirstQuote - 1);
                if (lastIndex >= 0)
                {
                    VideoDshowDevice current = devices[lastIndex];
                    devices[lastIndex] = current with { AlternativeName = value };
                }
                continue;
            }
            bool isVideoLine = line.Contains("(video)", StringComparison.OrdinalIgnoreCase) || inVideo;
            if (!isVideoLine)
            {
                continue;
            }
            int firstQuote = line.IndexOf('"');
            int lastQuote = line.LastIndexOf('"');
            if (firstQuote < 0 || lastQuote <= firstQuote)
            {
                continue;
            }
            string name = line.Substring(firstQuote + 1, lastQuote - firstQuote - 1);
            if (!line.Contains("(video)", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            devices.Add(new VideoDshowDevice(name, null));
            lastIndex = devices.Count - 1;
        }

        return devices;
    }

    /// <summary>
    /// Lists DirectShow capture modes for a device.
    /// </summary>
    /// <param name="ffmpegPath">FFmpeg path.</param>
    /// <param name="deviceName">Device name.</param>
    /// <returns>Modes and MJPEG support.</returns>
    public static VideoModesResult ListDshowModes(string ffmpegPath, string deviceName)
    {
        var modes = new Dictionary<(int w, int h), double>();
        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = $"-list_options true -f dshow -i video=\"{deviceName}\"",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var proc = Process.Start(psi);
        if (proc is null)
        {
            throw new InvalidOperationException("ffmpeg_start_failed");
        }

        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(5000);

        var sizeRegex = new Regex(@"s=(\d+)x(\d+)", RegexOptions.IgnoreCase);
        var fpsRegex = new Regex(@"fps=([0-9]+(?:\.[0-9]+)?)", RegexOptions.IgnoreCase);
        var mjpegRegex = new Regex(@"(pixel_format|vcodec)=mjpeg", RegexOptions.IgnoreCase);
        bool supportsMjpeg = mjpegRegex.IsMatch(stderr);
        foreach (string rawLine in stderr.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            string line = rawLine.Trim();
            if (!line.Contains("s=", StringComparison.OrdinalIgnoreCase) || !line.Contains("fps=", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            MatchCollection sizes = sizeRegex.Matches(line);
            MatchCollection fpsMatches = fpsRegex.Matches(line);
            if (sizes.Count == 0 || fpsMatches.Count == 0) continue;

            List<double> fpsValues = new();
            foreach (Match m in fpsMatches)
            {
                if (double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double f))
                {
                    fpsValues.Add(f);
                }
            }
            if (fpsValues.Count == 0) continue;

            for (int i = 0; i < sizes.Count; i++)
            {
                Match s = sizes[i];
                if (!int.TryParse(s.Groups[1].Value, out int w)) continue;
                if (!int.TryParse(s.Groups[2].Value, out int h)) continue;
                double fps = fpsValues.Count == sizes.Count ? fpsValues[i] : fpsValues.Max();
                var key = (w, h);
                if (modes.TryGetValue(key, out double existing))
                {
                    if (fps > existing) modes[key] = fps;
                }
                else
                {
                    modes[key] = fps;
                }
            }
        }

        var list = modes
            .Select(kv => new VideoMode(kv.Key.w, kv.Key.h, kv.Value))
            .OrderBy(m => m.Width * m.Height)
            .ToList();
        return new VideoModesResult(list, supportsMjpeg);
    }
}
