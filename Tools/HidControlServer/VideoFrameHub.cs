using System.Threading.Channels;

namespace HidControlServer;

/// <summary>
/// Distributes latest JPEG frames to subscribers.
/// </summary>
public sealed class VideoFrameHub
{
    private readonly object _lock = new();
    private readonly List<Channel<byte[]>> _subscribers = new();

    public byte[]? LatestFrame { get; private set; }
    public DateTimeOffset? LatestFrameAt { get; private set; }

    /// <summary>
    /// Publishes a new JPEG frame to all subscribers.
    /// </summary>
    /// <param name="jpeg">The jpeg.</param>
    public void Publish(byte[] jpeg)
    {
        Channel<byte[]>[] subscribers;
        lock (_lock)
        {
            LatestFrame = jpeg;
            LatestFrameAt = DateTimeOffset.UtcNow;
            subscribers = _subscribers.ToArray();
        }
        foreach (Channel<byte[]> sub in subscribers)
        {
            sub.Writer.TryWrite(jpeg);
        }
    }

    /// <summary>
    /// Subscribes to JPEG frame updates (drop-oldest channel).
    /// </summary>
    /// <param name="ct">The ct.</param>
    /// <returns>Result.</returns>
    public ChannelReader<byte[]> Subscribe(CancellationToken ct)
    {
        var channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(1)
        {
            SingleWriter = true,
            SingleReader = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });
        lock (_lock)
        {
            _subscribers.Add(channel);
            if (LatestFrame is not null)
            {
                channel.Writer.TryWrite(LatestFrame);
            }
        }
        ct.Register(() =>
        {
            lock (_lock)
            {
                _subscribers.Remove(channel);
                channel.Writer.TryComplete();
            }
        });
        return channel.Reader;
    }

    /// <summary>
    /// Waits for a frame or returns the latest one on timeout.
    /// </summary>
    /// <param name="timeout">The timeout.</param>
    /// <param name="ct">The ct.</param>
    /// <returns>Result.</returns>
    public async Task<byte[]?> WaitForFrameAsync(TimeSpan timeout, CancellationToken ct)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        while (!ct.IsCancellationRequested && DateTimeOffset.UtcNow < deadline)
        {
            byte[]? frame = LatestFrame;
            if (frame is not null)
            {
                return frame;
            }
            await Task.Delay(50, ct);
        }
        return LatestFrame;
    }
}
