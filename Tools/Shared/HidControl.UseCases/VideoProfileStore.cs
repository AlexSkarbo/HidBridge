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
            List<VideoProfileConfig> incoming = (profiles ?? Array.Empty<VideoProfileConfig>())
                .Where(p => !string.IsNullOrWhiteSpace(p.Name))
                .Select(p => new VideoProfileConfig(
                    p.Name.Trim(),
                    p.Args ?? string.Empty,
                    p.Note,
                    p.AudioEnabled,
                    p.AudioInput,
                    p.AudioBitrateKbps,
                    IsReadonly: false))
                .ToList();
            bool activeInIncoming = incoming.Any(p => string.Equals(p.Name, ActiveProfile, StringComparison.OrdinalIgnoreCase));
            _profiles.Clear();
            // Client can only persist user profiles. Built-in presets are re-added below as readonly.
            _profiles.AddRange(incoming);
            EnsureDefaultProfiles(_profiles);
            if (incoming.Count > 0 && !activeInIncoming)
            {
                // If caller replaced user profiles and previous active is not among them,
                // switch to the first incoming profile instead of silently sticking to a base preset.
                ActiveProfile = incoming[0].Name;
            }
            else if (_profiles.Count > 0 && !_profiles.Any(p => string.Equals(p.Name, ActiveProfile, StringComparison.OrdinalIgnoreCase)))
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
    /// Creates or updates a user profile. Built-in readonly profiles cannot be modified.
    /// </summary>
    /// <param name="profile">Profile payload.</param>
    /// <param name="error">Validation error.</param>
    /// <returns>True on success.</returns>
    public bool Upsert(VideoProfileConfig profile, out string? error)
    {
        lock (_lock)
        {
            error = null;
            string name = profile.Name?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name))
            {
                error = "name_required";
                return false;
            }

            int index = _profiles.FindIndex(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            if (index >= 0 && _profiles[index].IsReadonly)
            {
                error = "readonly_profile";
                return false;
            }

            var next = new VideoProfileConfig(
                name,
                profile.Args ?? string.Empty,
                profile.Note,
                profile.AudioEnabled,
                string.IsNullOrWhiteSpace(profile.AudioInput) ? null : profile.AudioInput.Trim(),
                profile.AudioBitrateKbps,
                IsReadonly: false);

            if (index >= 0)
            {
                _profiles[index] = next;
            }
            else
            {
                _profiles.Add(next);
            }
            return true;
        }
    }

    /// <summary>
    /// Deletes a user profile. Built-in readonly profiles cannot be removed.
    /// </summary>
    /// <param name="name">Profile name.</param>
    /// <param name="error">Validation error.</param>
    /// <returns>True on success.</returns>
    public bool Delete(string name, out string? error)
    {
        lock (_lock)
        {
            error = null;
            string wanted = name?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(wanted))
            {
                error = "name_required";
                return false;
            }

            int index = _profiles.FindIndex(p => string.Equals(p.Name, wanted, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                error = "profile_not_found";
                return false;
            }

            if (_profiles[index].IsReadonly)
            {
                error = "readonly_profile";
                return false;
            }

            string removedName = _profiles[index].Name;
            _profiles.RemoveAt(index);

            if (string.Equals(ActiveProfile, removedName, StringComparison.OrdinalIgnoreCase))
            {
                ActiveProfile = PickAutoProfile(_profiles);
            }

            return true;
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

        // WebRTC-oriented base presets for source-specific audio behavior.
        EnsureProfile(
            profiles,
            "pc-hdmi-pcm48",
            "-f dshow -pixel_format yuyv422 -video_size 1920x1080 -framerate 30 -rtbufsize 64M -i video=USB3.0 Video -an",
            "PC HDMI (PCM 48kHz)",
            audioEnabled: true,
            audioInput: null,
            audioBitrateKbps: 64);
        EnsureProfile(
            profiles,
            "android-tvbox",
            "-f dshow -vcodec mjpeg -video_size 1920x1080 -framerate 30 -rtbufsize 64M -i video=USB3.0 Video -an",
            "Android/TV Box",
            audioEnabled: true,
            audioInput: null,
            audioBitrateKbps: 64);
        EnsureProfile(
            profiles,
            "low-bandwidth",
            "-f dshow -framerate 30 -rtbufsize 64M -i video=USB3.0 Video -an -c:v libvpx -deadline realtime -cpu-used 12 -lag-in-frames 0 -error-resilient 1 -auto-alt-ref 0 -vf format=yuv420p -g 30 -b:v 900k -maxrate 990k -bufsize 1800k",
            "Low bandwidth",
            audioEnabled: true,
            audioInput: null,
            audioBitrateKbps: 48);

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
    private static void EnsureProfile(
        List<VideoProfileConfig> profiles,
        string name,
        string args,
        string note,
        bool? audioEnabled = null,
        string? audioInput = null,
        int? audioBitrateKbps = null)
    {
        var existingIndex = profiles.FindIndex(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existingIndex >= 0)
        {
            var existing = profiles[existingIndex];
            if (existing.Args != args ||
                existing.Note != note ||
                existing.AudioEnabled != audioEnabled ||
                !string.Equals(existing.AudioInput ?? string.Empty, audioInput ?? string.Empty, StringComparison.Ordinal) ||
                existing.AudioBitrateKbps != audioBitrateKbps ||
                !existing.IsReadonly)
            {
                profiles[existingIndex] = new VideoProfileConfig(name, args, note, audioEnabled, audioInput, audioBitrateKbps, IsReadonly: true);
            }
            return;
        }
        profiles.Add(new VideoProfileConfig(name, args, note, audioEnabled, audioInput, audioBitrateKbps, IsReadonly: true));
    }
}
