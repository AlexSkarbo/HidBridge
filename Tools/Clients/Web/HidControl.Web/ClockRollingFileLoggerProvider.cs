using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace HidControl.Web;

internal sealed class ClockRollingFileLoggerProvider : ILoggerProvider
{
    private readonly ClockRollingTextFile _sink;
    private readonly ConcurrentDictionary<string, ClockRollingFileLogger> _loggers = new(StringComparer.OrdinalIgnoreCase);

    public ClockRollingFileLoggerProvider(string directoryPath, string filePrefix, int rotateMinutes, int retentionMinutes)
    {
        _sink = new ClockRollingTextFile(directoryPath, filePrefix, rotateMinutes, retentionMinutes);
    }

    public ILogger CreateLogger(string categoryName)
    {
        return _loggers.GetOrAdd(categoryName, static (name, sink) => new ClockRollingFileLogger(name, sink), _sink);
    }

    public void Dispose()
    {
        _sink.Dispose();
    }
}

internal sealed class ClockRollingFileLogger : ILogger
{
    private readonly string _category;
    private readonly ClockRollingTextFile _sink;

    public ClockRollingFileLogger(string category, ClockRollingTextFile sink)
    {
        _category = category;
        _sink = sink;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        string message = formatter(state, exception);
        string line = $"{DateTimeOffset.UtcNow:O} [{logLevel}] {_category}: {message}";
        _sink.WriteLine(line);
        if (exception is not null)
        {
            _sink.WriteLine(exception.ToString());
        }
    }
}
