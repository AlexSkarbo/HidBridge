using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace HidControlServer.Services;

/// <summary>
/// Simple clock-aligned rolling file logger provider.
/// </summary>
public sealed class ClockRollingFileLoggerProvider : ILoggerProvider
{
    private readonly ClockRollingFileSink _sink;
    private readonly ConcurrentDictionary<string, ClockRollingFileLogger> _loggers = new(StringComparer.OrdinalIgnoreCase);

    public ClockRollingFileLoggerProvider(string directoryPath, string filePrefix, int rotateMinutes, int retentionMinutes)
    {
        _sink = new ClockRollingFileSink(directoryPath, filePrefix, rotateMinutes, retentionMinutes);
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
    private readonly ClockRollingFileSink _sink;

    public ClockRollingFileLogger(string category, ClockRollingFileSink sink)
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
        _sink.WriteLine(DateTimeOffset.UtcNow, logLevel, _category, message, exception);
    }
}

internal sealed class ClockRollingFileSink : IDisposable
{
    private readonly object _lock = new();
    private readonly string _directoryPath;
    private readonly string _filePrefix;
    private readonly int _rotateMinutes;
    private readonly int _retentionMinutes;
    private readonly string _sessionId = DateTimeOffset.UtcNow.ToString("yyyyMMdd_HHmmss");
    private DateTimeOffset _currentBucketStartUtc = DateTimeOffset.MinValue;
    private StreamWriter? _writer;
    private DateTimeOffset _lastCleanupUtc = DateTimeOffset.MinValue;

    public ClockRollingFileSink(string directoryPath, string filePrefix, int rotateMinutes, int retentionMinutes)
    {
        _directoryPath = string.IsNullOrWhiteSpace(directoryPath) ? Path.Combine(AppContext.BaseDirectory, "logs") : directoryPath.Trim();
        _filePrefix = string.IsNullOrWhiteSpace(filePrefix) ? "app" : filePrefix.Trim();
        _rotateMinutes = Math.Max(1, rotateMinutes);
        _retentionMinutes = Math.Max(_rotateMinutes, retentionMinutes);
        Directory.CreateDirectory(_directoryPath);
        OpenWriterIfNeeded(DateTimeOffset.UtcNow);
    }

    public void WriteLine(DateTimeOffset nowUtc, LogLevel level, string category, string message, Exception? exception)
    {
        lock (_lock)
        {
            OpenWriterIfNeeded(nowUtc);
            if (_writer is null)
            {
                return;
            }

            _writer.Write(nowUtc.ToString("O"));
            _writer.Write(" [");
            _writer.Write(level);
            _writer.Write("] ");
            _writer.Write(category);
            _writer.Write(": ");
            _writer.WriteLine(message);
            if (exception is not null)
            {
                _writer.WriteLine(exception);
            }
            _writer.Flush();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    private void OpenWriterIfNeeded(DateTimeOffset nowUtc)
    {
        DateTimeOffset bucketStart = AlignToBucketStartUtc(nowUtc, _rotateMinutes);
        if (_writer is not null && bucketStart == _currentBucketStartUtc)
        {
            CleanupOldFilesIfNeeded(nowUtc);
            return;
        }

        _writer?.Dispose();
        _currentBucketStartUtc = bucketStart;

        string fileName = $"{_filePrefix}_{bucketStart:yyyyMMdd_HHmm}_s{_sessionId}.log";
        string filePath = Path.Combine(_directoryPath, fileName);
        _writer = new StreamWriter(new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
        {
            AutoFlush = true
        };
        _writer.WriteLine($"# session={_sessionId} bucketStartUtc={bucketStart:O} rotateMinutes={_rotateMinutes} retentionMinutes={_retentionMinutes}");
        CleanupOldFilesIfNeeded(nowUtc);
    }

    private void CleanupOldFilesIfNeeded(DateTimeOffset nowUtc)
    {
        if (_lastCleanupUtc != DateTimeOffset.MinValue && (nowUtc - _lastCleanupUtc) < TimeSpan.FromMinutes(1))
        {
            return;
        }

        _lastCleanupUtc = nowUtc;
        DateTimeOffset threshold = nowUtc.AddMinutes(-_retentionMinutes);
        string pattern = $"{_filePrefix}_*_s*.log";
        foreach (string filePath in Directory.EnumerateFiles(_directoryPath, pattern, SearchOption.TopDirectoryOnly))
        {
            try
            {
                var info = new FileInfo(filePath);
                DateTimeOffset lastWrite = new(info.LastWriteTimeUtc, TimeSpan.Zero);
                if (lastWrite < threshold)
                {
                    File.Delete(filePath);
                }
            }
            catch
            {
                // Best effort cleanup.
            }
        }
    }

    private static DateTimeOffset AlignToBucketStartUtc(DateTimeOffset valueUtc, int minutes)
    {
        long bucketTicks = TimeSpan.FromMinutes(minutes).Ticks;
        long ticks = valueUtc.UtcDateTime.Ticks;
        long alignedTicks = ticks - (ticks % bucketTicks);
        return new DateTimeOffset(new DateTime(alignedTicks, DateTimeKind.Utc));
    }
}
