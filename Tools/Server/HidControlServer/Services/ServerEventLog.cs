using System.Text.Json;
using Microsoft.Extensions.Logging;

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
    private static ILogger? _logger;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    /// <summary>
    /// Configures the event log buffer.
    /// </summary>
    /// <param name="maxEntries">Maximum number of events to keep in memory.</param>
    public static void Configure(int maxEntries)
    {
        if (maxEntries < 1)
        {
            maxEntries = 1;
        }
        try
        {
            string[] legacyPaths =
            {
                Path.Combine(AppContext.BaseDirectory, "logs", "server.log"),
                Path.Combine(Directory.GetCurrentDirectory(), "logs", "server.log")
            };
            foreach (string legacyPath in legacyPaths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (File.Exists(legacyPath))
                {
                    File.Delete(legacyPath);
                }
            }
        }
        catch
        {
            // ignore cleanup failures
        }
        lock (Gate)
        {
            _buffer = new ServerEvent[maxEntries];
            _count = 0;
            _next = 0;
        }
    }

    /// <summary>
    /// Binds a structured logger used for persisted server-event output.
    /// </summary>
    /// <param name="logger">Target logger.</param>
    public static void BindLogger(ILogger logger)
    {
        _logger = logger;
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
            _logger?.LogInformation("server_event category={Category} message={Message} data={DataJson}", entry.Category, entry.Message, entry.DataJson);
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
