using System.Text.Json;
using HidControl.Application.Abstractions;
using HidControl.Contracts;

namespace HidControlServer.Services;

/// <summary>
/// JSON-backed state store for mutable runtime-managed data.
/// </summary>
public sealed class JsonHidStateStore : IHidStateStore
{
    private const int SchemaVersion = 1;
    private readonly string _path;
    private readonly object _lock = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    /// <summary>
    /// Creates a store for the provided file path.
    /// </summary>
    /// <param name="path">State file path.</param>
    public JsonHidStateStore(string path)
    {
        _path = path;
    }

    /// <inheritdoc />
    public HidStateSnapshot Load()
    {
        lock (_lock)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_path) || !File.Exists(_path))
                {
                    return EmptySnapshot();
                }

                string json = File.ReadAllText(_path);
                var file = JsonSerializer.Deserialize<StateFile>(json, JsonOptions);
                if (file is null)
                {
                    return EmptySnapshot();
                }

                var profiles = (file.VideoProfiles ?? new List<VideoProfileConfig>())
                    .Where(p => !string.IsNullOrWhiteSpace(p.Name))
                    .ToArray();
                var bindings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                foreach (var kvp in file.RoomProfileBindings ?? new Dictionary<string, string?>())
                {
                    string room = (kvp.Key ?? string.Empty).Trim();
                    if (room.Length == 0)
                    {
                        continue;
                    }
                    bindings[room] = string.IsNullOrWhiteSpace(kvp.Value) ? null : kvp.Value!.Trim();
                }

                return new HidStateSnapshot(profiles, NormalizeName(file.ActiveVideoProfile), bindings);
            }
            catch
            {
                return EmptySnapshot();
            }
        }
    }

    /// <inheritdoc />
    public void SaveVideoProfiles(IReadOnlyList<VideoProfileConfig> profiles, string? activeProfile)
    {
        lock (_lock)
        {
            StateFile file = LoadFileUnsafe();
            file.VideoProfiles = (profiles ?? Array.Empty<VideoProfileConfig>())
                .Where(p => !string.IsNullOrWhiteSpace(p.Name))
                .Select(p => new VideoProfileConfig(
                    p.Name.Trim(),
                    p.Args ?? string.Empty,
                    p.Note,
                    p.AudioEnabled,
                    string.IsNullOrWhiteSpace(p.AudioInput) ? null : p.AudioInput.Trim(),
                    p.AudioBitrateKbps,
                    p.IsReadonly))
                .ToList();
            file.ActiveVideoProfile = NormalizeName(activeProfile);
            SaveFileUnsafe(file);
        }
    }

    /// <inheritdoc />
    public void SaveRoomProfileBindings(IReadOnlyDictionary<string, string?> bindings)
    {
        lock (_lock)
        {
            StateFile file = LoadFileUnsafe();
            file.RoomProfileBindings = (bindings ?? new Dictionary<string, string?>())
                .Where(kvp => !string.IsNullOrWhiteSpace(kvp.Key))
                .ToDictionary(
                    kvp => kvp.Key.Trim(),
                    kvp => NormalizeName(kvp.Value),
                    StringComparer.OrdinalIgnoreCase);
            SaveFileUnsafe(file);
        }
    }

    private StateFile LoadFileUnsafe()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(_path) && File.Exists(_path))
            {
                string json = File.ReadAllText(_path);
                var file = JsonSerializer.Deserialize<StateFile>(json, JsonOptions);
                if (file is not null)
                {
                    file.Version = SchemaVersion;
                    return file;
                }
            }
        }
        catch
        {
            // fall back to empty schema
        }

        return new StateFile
        {
            Version = SchemaVersion,
            VideoProfiles = new List<VideoProfileConfig>(),
            RoomProfileBindings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        };
    }

    private void SaveFileUnsafe(StateFile file)
    {
        string? dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        file.Version = SchemaVersion;
        string json = JsonSerializer.Serialize(file, JsonOptions);
        File.WriteAllText(_path, json);
    }

    private static string? NormalizeName(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static HidStateSnapshot EmptySnapshot() =>
        new(Array.Empty<VideoProfileConfig>(), null, new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));

    private sealed class StateFile
    {
        public int Version { get; set; } = SchemaVersion;
        public List<VideoProfileConfig>? VideoProfiles { get; set; }
        public string? ActiveVideoProfile { get; set; }
        public Dictionary<string, string?>? RoomProfileBindings { get; set; }
    }
}
