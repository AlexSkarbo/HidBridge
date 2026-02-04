using HidControl.Contracts;

namespace HidControl.UseCases;

// In-memory profile registry with active selection logic.
/// <summary>
/// Stores video profile data.
/// </summary>
public sealed class VideoProfileStore
{
    private readonly object _lock = new();
    private readonly List<VideoProfileConfig> _profiles = new();

    public string ActiveProfile { get; private set; }

    /// <summary>
    /// Executes VideoProfileStore.
    /// </summary>
    /// <param name="initial">The initial.</param>
    /// <param name="active">The active.</param>
    public VideoProfileStore(IEnumerable<VideoProfileConfig>? initial, string active)
    {
        if (initial is not null)
        {
            _profiles.AddRange(initial);
        }
        EnsureDefaultProfiles(_profiles);
        ActiveProfile = string.IsNullOrWhiteSpace(active) || IsAutoName(active)
            ? PickAutoProfile(_profiles)
            : active;
        if (_profiles.Count > 0 && !_profiles.Any(p => string.Equals(p.Name, ActiveProfile, StringComparison.OrdinalIgnoreCase)))
        {
            ActiveProfile = PickAutoProfile(_profiles);
        }
    }

    /// <summary>
    /// Gets all.
    /// </summary>
    /// <returns>Result.</returns>
    public IReadOnlyList<VideoProfileConfig> GetAll()
    {
        lock (_lock)
        {
            return _profiles.ToArray();
        }
    }

    /// <summary>
    /// Executes ReplaceAll.
    /// </summary>
    /// <param name="profiles">The profiles.</param>
    public void ReplaceAll(IEnumerable<VideoProfileConfig> profiles)
    {
        lock (_lock)
        {
            _profiles.Clear();
            _profiles.AddRange(profiles);
            if (_profiles.Count > 0 && !_profiles.Any(p => string.Equals(p.Name, ActiveProfile, StringComparison.OrdinalIgnoreCase)))
            {
                ActiveProfile = PickAutoProfile(_profiles);
            }
        }
    }

    /// <summary>
    /// Sets active.
    /// </summary>
    /// <param name="name">The name.</param>
    /// <returns>Result.</returns>
    public bool SetActive(string name)
    {
        lock (_lock)
        {
            if (_profiles.Count == 0)
            {
                ActiveProfile = name;
                return true;
            }
            if (IsAutoName(name))
            {
                ActiveProfile = PickAutoProfile(_profiles);
                return true;
            }
            if (_profiles.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                ActiveProfile = name;
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Gets active args.
    /// </summary>
    /// <returns>Result.</returns>
    public string GetActiveArgs()
    {
        lock (_lock)
        {
            VideoProfileConfig? profile = _profiles.FirstOrDefault(p => string.Equals(p.Name, ActiveProfile, StringComparison.OrdinalIgnoreCase));
            return profile?.Args ?? "-c:v libx264 -preset ultrafast -tune zerolatency";
        }
    }

    public string ActiveArgs => GetActiveArgs();

    /// <summary>
    /// Checks whether auto name.
    /// </summary>
    /// <param name="name">The name.</param>
    /// <returns>Result.</returns>
    private static bool IsAutoName(string name)
    {
        return string.Equals(name, "auto", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Executes PickAutoProfile.
    /// </summary>
    /// <param name="profiles">The profiles.</param>
    /// <returns>Result.</returns>
    private static string PickAutoProfile(IReadOnlyList<VideoProfileConfig> profiles)
    {
        string? selected = null;
        if (OperatingSystem.IsWindows())
        {
            selected = FindProfileName(profiles, "win-1080-low", "win-2k-low", "win-4k-low");
        }
        else if (OperatingSystem.IsLinux())
        {
            selected = FindProfileName(profiles, "pi-1080-low", "pi-2k-low");
        }
        else if (OperatingSystem.IsMacOS())
        {
            selected = FindProfileName(profiles, "mac-1080-low");
        }
        selected ??= FindProfileName(profiles, "low-latency");
        selected ??= profiles.Count > 0 ? profiles[0].Name : "low-latency";
        return selected;
    }

    /// <summary>
    /// Executes FindProfileName.
    /// </summary>
    /// <param name="profiles">The profiles.</param>
    /// <param name="names">The names.</param>
    /// <returns>Result.</returns>
    private static string? FindProfileName(IReadOnlyList<VideoProfileConfig> profiles, params string[] names)
    {
        foreach (string name in names)
        {
            VideoProfileConfig? match = profiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match.Name;
            }
        }
        return null;
    }

    /// <summary>
    /// Ensures default profiles.
    /// </summary>
    /// <param name="profiles">The profiles.</param>
    private static void EnsureDefaultProfiles(List<VideoProfileConfig> profiles)
    {
        // Always ensure a safe CPU fallback profile exists first.
        EnsureProfile(
            profiles,
            "low-latency",
            "-c:v libx264 -preset veryfast -tune zerolatency -g 30 -keyint_min 30 -sc_threshold 0 -bf 0 -pix_fmt yuv420p -b:v 3500k -maxrate 3500k -bufsize 1750k -vf scale=1920:1080 -r 30",
            "default");

        // Legacy defaults (kept for compatibility with existing configs/UI labels).
        EnsureProfile(
            profiles,
            "win-1080-low",
            "-rtbufsize 64M -c:v libx264 -preset ultrafast -tune zerolatency -g 30 -bf 0 -pix_fmt yuv420p -b:v 6000k -maxrate 6000k -bufsize 12000k -vf scale=1920:1080 -r 30",
            "windows");
        EnsureProfile(
            profiles,
            "win-2k-low",
            "-rtbufsize 64M -c:v libx264 -preset ultrafast -tune zerolatency -g 30 -bf 0 -pix_fmt yuv420p -b:v 12000k -maxrate 12000k -bufsize 24000k -vf scale=2560:1440 -r 30",
            "windows");
        EnsureProfile(
            profiles,
            "win-4k-low",
            "-rtbufsize 64M -c:v libx264 -preset ultrafast -tune zerolatency -g 30 -bf 0 -pix_fmt yuv420p -b:v 24000k -maxrate 24000k -bufsize 48000k -vf scale=3840:2160 -r 30",
            "windows");
        EnsureProfile(
            profiles,
            "pi-1080-low",
            "-c:v h264_v4l2m2m -g 30 -bf 0 -pix_fmt yuv420p -b:v 6000k -maxrate 6000k -bufsize 12000k -vf scale=1920:1080 -r 30",
            "linux");
        EnsureProfile(
            profiles,
            "pi-2k-low",
            "-c:v h264_v4l2m2m -g 30 -bf 0 -pix_fmt yuv420p -b:v 12000k -maxrate 12000k -bufsize 24000k -vf scale=2560:1440 -r 30",
            "linux");
        EnsureProfile(
            profiles,
            "mac-1080-low",
            "-c:v libx264 -preset ultrafast -tune zerolatency -g 30 -bf 0 -pix_fmt yuv420p -b:v 6000k -maxrate 6000k -bufsize 12000k -vf scale=1920:1080 -r 30",
            "macos");

        // Low-latency FLV/WS-FLV oriented presets (H.264, 1080p30).
        EnsureProfile(
            profiles,
            "win-1080-flv-nvenc",
            "-c:v h264_nvenc -preset p3 -tune ll -rc cbr -b:v 6000k -maxrate 6000k -bufsize 600k -g 4 -bf 0 -rc-lookahead 0 -no-scenecut 1 -forced-idr 1 -profile:v high -pix_fmt yuv420p -vf scale=1920:1080 -r 30",
            "windows nvenc flv");
        EnsureProfile(
            profiles,
            "win-1080-flv-amf",
            "-c:v h264_amf -usage lowlatency -rc cbr -b:v 6000k -maxrate 6000k -bufsize 600k -g 4 -bf 0 -profile:v high -pix_fmt yuv420p -vf scale=1920:1080 -r 30",
            "windows amf flv");
        EnsureProfile(
            profiles,
            "linux-1080-flv-v4l2",
            "-c:v h264_v4l2m2m -b:v 6000k -maxrate 6000k -bufsize 600k -g 4 -bf 0 -pix_fmt yuv420p -vf scale=1920:1080 -r 30",
            "linux v4l2m2m flv");
        EnsureProfile(
            profiles,
            "cpu-1080-flv-x264",
            "-c:v libx264 -preset superfast -tune zerolatency -g 4 -keyint_min 4 -sc_threshold 0 -bf 0 -pix_fmt yuv420p -b:v 6000k -maxrate 6000k -bufsize 600k -vf scale=1920:1080 -r 30",
            "cpu flv");
    }

    /// <summary>
    /// Ensures profile.
    /// </summary>
    /// <param name="profiles">The profiles.</param>
    /// <param name="name">The name.</param>
    /// <param name="args">The args.</param>
    /// <param name="note">The note.</param>
    private static void EnsureProfile(List<VideoProfileConfig> profiles, string name, string args, string note)
    {
        var existingIndex = profiles.FindIndex(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existingIndex >= 0)
        {
            var existing = profiles[existingIndex];
            if (existing.Args != args || existing.Note != note)
            {
                profiles[existingIndex] = new VideoProfileConfig(name, args, note);
            }
            return;
        }
        profiles.Add(new VideoProfileConfig(name, args, note));
    }
}
