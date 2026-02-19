using System.Globalization;

namespace HidControl.Web;

internal sealed class ClockRollingTextFile : IDisposable
{
    private readonly object _lock = new();
    private readonly string _directoryPath;
    private readonly string _filePrefix;
    private readonly int _rotateMinutes;
    private readonly int _retentionMinutes;
    private readonly string _sessionId = DateTimeOffset.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
    private DateTimeOffset _currentBucketStartUtc = DateTimeOffset.MinValue;
    private StreamWriter? _writer;
    private DateTimeOffset _lastCleanupUtc = DateTimeOffset.MinValue;

    public ClockRollingTextFile(string directoryPath, string filePrefix, int rotateMinutes, int retentionMinutes)
    {
        _directoryPath = string.IsNullOrWhiteSpace(directoryPath) ? Path.Combine(AppContext.BaseDirectory, "logs") : directoryPath.Trim();
        _filePrefix = string.IsNullOrWhiteSpace(filePrefix) ? "hidcontrol.web" : filePrefix.Trim();
        _rotateMinutes = Math.Max(1, rotateMinutes);
        _retentionMinutes = Math.Max(_rotateMinutes, retentionMinutes);
        Directory.CreateDirectory(_directoryPath);
        OpenWriterIfNeeded(DateTimeOffset.UtcNow);
    }

    public void WriteLine(string message)
    {
        lock (_lock)
        {
            OpenWriterIfNeeded(DateTimeOffset.UtcNow);
            if (_writer is null)
            {
                return;
            }

            _writer.WriteLine(message);
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
