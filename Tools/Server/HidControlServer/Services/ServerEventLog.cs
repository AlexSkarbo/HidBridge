using System.Text.Json;

namespace HidControlServer.Services;

/// <summary>
/// Stores recent server events in memory and appends them to a log file.
/// </summary>
internal static class ServerEventLog
{
    private static readonly object Gate = new();
    private static ServerEvent[] _buffer = Array.Empty<ServerEvent>();
    private static int _count;
    private static int _next;
    private static string _logPath = string.Empty;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    /// <summary>
    /// Configures the event log buffer and log file location.
    /// </summary>
    /// <param name="maxEntries">Maximum number of events to keep in memory.</param>
    public static void Configure(int maxEntries)
    {
        if (maxEntries < 1)
        {
            maxEntries = 1;
        }
        string logDir = Path.Combine(AppContext.BaseDirectory, "logs");
        Directory.CreateDirectory(logDir);
        _logPath = Path.Combine(logDir, "server.log");

        lock (Gate)
        {
            _buffer = new ServerEvent[maxEntries];
            _count = 0;
            _next = 0;
        }
    }

    /// <summary>
    /// Appends an event to the in-memory log and file.
    /// </summary>
    /// <param name="category">Event category.</param>
    /// <param name="message">Event message.</param>
    /// <param name="data">Optional event data.</param>
    public static void Log(string category, string message, object? data = null)
    {
        var entry = new ServerEvent(DateTimeOffset.UtcNow, category, message, SerializeData(data));
        lock (Gate)
        {
            if (_buffer.Length == 0)
            {
                return;
            }
            _buffer[_next] = entry;
            _next = (_next + 1) % _buffer.Length;
            if (_count < _buffer.Length)
            {
                _count++;
            }
        }

        try
        {
            string line = JsonSerializer.Serialize(entry, JsonOptions);
            File.AppendAllText(_logPath, line + Environment.NewLine);
        }
        catch
        {
            // ignore logging failures
        }
    }

    /// <summary>
    /// Returns recent events, newest first.
    /// </summary>
    /// <param name="limit">Maximum number of events to return.</param>
    /// <returns>Recent events.</returns>
    public static IReadOnlyList<ServerEvent> GetRecent(int? limit = null)
    {
        lock (Gate)
        {
            if (_count == 0)
            {
                return Array.Empty<ServerEvent>();
            }
            int take = limit.HasValue ? Math.Min(limit.Value, _count) : _count;
            var list = new List<ServerEvent>(take);
            int idx = (_next - 1 + _buffer.Length) % _buffer.Length;
            for (int i = 0; i < take; i++)
            {
                list.Add(_buffer[idx]);
                idx = (idx - 1 + _buffer.Length) % _buffer.Length;
            }
            return list;
        }
    }

    private static string? SerializeData(object? data)
    {
        if (data is null) return null;
        try
        {
            return JsonSerializer.Serialize(data, JsonOptions);
        }
        catch
        {
            return data.ToString();
        }
    }

    /// <summary>
    /// Represents a log event entry.
    /// </summary>
    /// <param name="AtUtc">Event time in UTC.</param>
    /// <param name="Category">Event category.</param>
    /// <param name="Message">Event message.</param>
    /// <param name="DataJson">Optional event data JSON.</param>
    internal sealed record ServerEvent(DateTimeOffset AtUtc, string Category, string Message, string? DataJson);
}
