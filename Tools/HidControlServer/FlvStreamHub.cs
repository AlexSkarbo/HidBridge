using System.Threading.Channels;
using HidControlServer.Services;

namespace HidControlServer;

/// <summary>
/// Tracks FLV header/tags and distributes stream data to subscribers.
/// </summary>
public sealed class FlvStreamHub
{
    private readonly object _lock = new();
    private readonly List<Subscriber> _subscribers = new();
    private TaskCompletionSource<bool> _headerReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private byte[]? _header;
    private byte[]? _lastMeta;
    private byte[]? _lastVideoConfig;
    private byte[]? _lastKeyframe;
    private bool _hasVideoConfig;
    private bool _hasKeyframe;
    private long _tagsIn;
    private long _tagsMeta;
    private long _tagsConfig;
    private long _tagsKeyframe;
    private long _tagsDroppedNoConfig;
    private long _tagsDroppedNoKeyframe;
    private long _tagsPublished;
    private DateTimeOffset _lastTagAtUtc;
    private int _lastTimestampMs;
    private byte _lastTagType;
    private int _lastTagLen;
    private int _lastVideoTimestampMs;
    private int _lastKeyframeTimestampMs;
    private int _lastConfigTimestampMs;
    private int _subscribersActive;
    private long _subscriberDrops;

    public bool HasHeader => _header is not null;
    public bool HasVideoConfig
    {
        get
        {
            lock (_lock)
            {
                return _hasVideoConfig;
            }
        }
    }

    /// <summary>
    /// Stores the initial FLV header for new subscribers.
    /// </summary>
    /// <param name="header">The header.</param>
    public void SetHeader(byte[] header)
    {
        lock (_lock)
        {
            _header ??= header;
        }
        _headerReady.TrySetResult(true);
    }

    /// <summary>
    /// Resets cached state for a new FLV stream while keeping subscribers.
    /// </summary>
    public void ResetForNewStream()
    {
        lock (_lock)
        {
            _header = null;
            _lastMeta = null;
            _lastVideoConfig = null;
            _lastKeyframe = null;
            _hasVideoConfig = false;
            _hasKeyframe = false;
            _lastTimestampMs = 0;
            _lastTagType = 0;
            _lastTagLen = 0;
            _lastVideoTimestampMs = 0;
            _lastKeyframeTimestampMs = 0;
            _lastConfigTimestampMs = 0;
            _lastTagAtUtc = default;
            _headerReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            foreach (var sub in _subscribers)
            {
                sub.NeedsKeyframe = true;
            }
        }
    }

    /// <summary>
    /// Publishes an FLV tag and updates cached metadata/configuration.
    /// </summary>
    /// <param name="tag">The tag.</param>
    /// <param name="isMeta">The isMeta.</param>
    /// <param name="isKeyframe">The isKeyframe.</param>
    /// <param name="isVideoConfig">The isVideoConfig.</param>
    /// <param name="timestampMs">The timestampMs.</param>
    /// <param name="tagType">The tagType.</param>
    public void PublishTag(byte[] tag, bool isMeta, bool isKeyframe, bool isVideoConfig, int timestampMs, byte tagType)
    {
        Subscriber[] subscribers;
        byte[]? meta;
        byte[]? config;
        byte[]? injectedConfig = null;
        bool keyframe = isKeyframe;
        bool videoConfig = isVideoConfig;
        lock (_lock)
        {
            _tagsIn++;
            _lastTagAtUtc = DateTimeOffset.UtcNow;
            _lastTimestampMs = timestampMs;
            _lastTagType = tagType;
            _lastTagLen = tag.Length;
            if (isMeta)
            {
                _lastMeta = tag;
                _tagsMeta++;
            }
            if (!_hasVideoConfig && !videoConfig && keyframe && tagType == 9)
            {
                if (FlvParserService.TryBuildAvcConfigTagFromKeyframe(tag, out var configTag))
                {
                    injectedConfig = configTag;
                    _lastVideoConfig = configTag;
                    _hasVideoConfig = true;
                    _hasKeyframe = false;
                    _tagsConfig++;
                    _lastConfigTimestampMs = timestampMs;
                }
            }
            if (videoConfig)
            {
                _lastVideoConfig = tag;
                _hasVideoConfig = true;
                _hasKeyframe = false;
                _tagsConfig++;
                _lastConfigTimestampMs = timestampMs;
            }
            if (keyframe)
            {
                _lastKeyframe = tag;
                _hasKeyframe = true;
                _tagsKeyframe++;
                _lastKeyframeTimestampMs = timestampMs;
            }
            if (tagType == 9)
            {
                _lastVideoTimestampMs = timestampMs;
            }
            // If we have no AVC config yet, still allow keyframes through.
            // Some encoders may delay or omit config on first packets.
            if (!_hasVideoConfig && !videoConfig && tag.Length > 0 && tag[0] == 9 && !keyframe)
            {
                _tagsDroppedNoConfig++;
                return;
            }
            if (_hasVideoConfig && !_hasKeyframe && !keyframe && !videoConfig && tag.Length > 0 && tag[0] == 9)
            {
                _tagsDroppedNoKeyframe++;
                return;
            }
            subscribers = _subscribers.ToArray();
            meta = _lastMeta;
            config = injectedConfig ?? _lastVideoConfig;
            _tagsPublished++;
        }
        foreach (var sub in subscribers)
        {
            bool injectOnKeyframe = tagType == 9 && keyframe;

            // New subscribers should not start with a potentially stale keyframe.
            // Always wait for the next keyframe seen after subscribe, then start streaming.
            if (sub.NeedsKeyframe)
            {
                if (!injectOnKeyframe)
                {
                    continue;
                }
                if (meta is not null)
                {
                    TryWrite(sub, meta);
                }
                if (config is not null)
                {
                    TryWrite(sub, config);
                }
                sub.NeedsKeyframe = false;
                TryWrite(sub, tag);
                continue;
            }

            if (meta is not null && injectOnKeyframe)
            {
                TryWrite(sub, meta);
            }
            if (config is not null && injectOnKeyframe)
            {
                TryWrite(sub, config);
            }
            TryWrite(sub, tag);
        }
    }

    /// <summary>
    /// Tries to write.
    /// </summary>
    /// <param name="sub">The sub.</param>
    /// <param name="tag">The tag.</param>
    /// <returns>Result.</returns>
    private bool TryWrite(Subscriber sub, byte[] tag)
    {
        if (sub.Channel.Writer.TryWrite(tag))
        {
            return true;
        }
        sub.Drops++;
        lock (_lock)
        {
            _subscriberDrops++;
        }
        return false;
    }

    /// <summary>
    /// Executes WaitForHeaderAsync.
    /// </summary>
    /// <param name="timeout">The timeout.</param>
    /// <param name="ct">The ct.</param>
    /// <returns>Result.</returns>
    public async Task<byte[]?> WaitForHeaderAsync(TimeSpan timeout, CancellationToken ct)
    {
        if (_header is not null)
        {
            return _header;
        }
        Task finished = await Task.WhenAny(_headerReady.Task, Task.Delay(timeout, ct));
        if (finished == _headerReady.Task && _header is not null)
        {
            return _header;
        }
        return _header;
    }

    /// <summary>
    /// Gets initial tags.
    /// </summary>
    /// <returns>Result.</returns>
    public List<byte[]> GetInitialTags()
    {
        lock (_lock)
        {
            var list = new List<byte[]>();
            if (_header is not null)
            {
                list.Add(_header);
            }
            if (_lastMeta is not null)
            {
                list.Add(_lastMeta);
            }
            if (_lastVideoConfig is not null)
            {
                list.Add(_lastVideoConfig);
            }
            return list;
        }
    }

    /// <summary>
    /// Gets stats.
    /// </summary>
    /// <returns>Result.</returns>
    public FlvHubStats GetStats()
    {
        lock (_lock)
        {
            return new FlvHubStats(
                hasHeader: _header is not null,
                hasVideoConfig: _hasVideoConfig,
                hasKeyframe: _hasKeyframe,
                tagsIn: _tagsIn,
                tagsMeta: _tagsMeta,
                tagsConfig: _tagsConfig,
                tagsKeyframe: _tagsKeyframe,
                tagsDroppedNoConfig: _tagsDroppedNoConfig,
                tagsDroppedNoKeyframe: _tagsDroppedNoKeyframe,
                tagsPublished: _tagsPublished,
                lastTagAtUtc: _lastTagAtUtc,
                lastTimestampMs: _lastTimestampMs,
                lastTagType: _lastTagType,
                lastTagLen: _lastTagLen,
                lastVideoTimestampMs: _lastVideoTimestampMs,
                lastKeyframeTimestampMs: _lastKeyframeTimestampMs,
                lastConfigTimestampMs: _lastConfigTimestampMs,
                subscribersActive: _subscribersActive,
                subscriberDrops: _subscriberDrops
            );
        }
    }

    /// <summary>
    /// Executes Subscribe.
    /// </summary>
    /// <param name="ct">The ct.</param>
    /// <returns>Result.</returns>
    public ChannelReader<byte[]> Subscribe(CancellationToken ct)
    {
        var channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(128)
        {
            SingleWriter = true,
            SingleReader = false,
            FullMode = BoundedChannelFullMode.DropWrite
        });
        var sub = new Subscriber(channel)
        {
            NeedsKeyframe = true
        };
        lock (_lock)
        {
            _subscribers.Add(sub);
            _subscribersActive = _subscribers.Count;
        }
        ct.Register(() =>
        {
            lock (_lock)
            {
                _subscribers.Remove(sub);
                _subscribersActive = _subscribers.Count;
                channel.Writer.TryComplete();
            }
        });
        return channel.Reader;
    }

    /// <summary>
    /// Manages FLV stream hub state.
    /// </summary>
    /// <param name="hasHeader">hasHeader.</param>
    /// <param name="hasVideoConfig">hasVideoConfig.</param>
    /// <param name="hasKeyframe">hasKeyframe.</param>
    /// <param name="tagsIn">tagsIn.</param>
    /// <param name="tagsMeta">tagsMeta.</param>
    /// <param name="tagsConfig">tagsConfig.</param>
    /// <param name="tagsKeyframe">tagsKeyframe.</param>
    /// <param name="tagsDroppedNoConfig">tagsDroppedNoConfig.</param>
    /// <param name="tagsDroppedNoKeyframe">tagsDroppedNoKeyframe.</param>
    /// <param name="tagsPublished">tagsPublished.</param>
    /// <param name="lastTagAtUtc">lastTagAtUtc.</param>
    /// <param name="lastTimestampMs">lastTimestampMs.</param>
    /// <param name="lastTagType">lastTagType.</param>
    /// <param name="lastTagLen">lastTagLen.</param>
    /// <param name="lastVideoTimestampMs">lastVideoTimestampMs.</param>
    /// <param name="lastKeyframeTimestampMs">lastKeyframeTimestampMs.</param>
    /// <param name="lastConfigTimestampMs">lastConfigTimestampMs.</param>
    /// <param name="subscribersActive">subscribersActive.</param>
    /// <param name="subscriberDrops">subscriberDrops.</param>
    public readonly record struct FlvHubStats(
        bool hasHeader,
        bool hasVideoConfig,
        bool hasKeyframe,
        long tagsIn,
        long tagsMeta,
        long tagsConfig,
        long tagsKeyframe,
        long tagsDroppedNoConfig,
        long tagsDroppedNoKeyframe,
        long tagsPublished,
        DateTimeOffset lastTagAtUtc,
        int lastTimestampMs,
        byte lastTagType,
        int lastTagLen,
        int lastVideoTimestampMs,
        int lastKeyframeTimestampMs,
        int lastConfigTimestampMs,
        int subscribersActive,
        long subscriberDrops
    );

    /// <summary>
    /// Manages FLV stream hub state.
    /// </summary>
    private sealed class Subscriber
    {
        /// <summary>
        /// Executes Subscriber.
        /// </summary>
        /// <param name="channel">The channel.</param>
        public Subscriber(Channel<byte[]> channel)
        {
            Channel = channel;
        }

        public Channel<byte[]> Channel { get; }
        public bool NeedsKeyframe { get; set; }
        public long Drops { get; set; }
    }
}
