using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Ports;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Channels;

namespace HidControlServer;

/// <summary>
/// Snapshot of UART transport statistics for the HID bridge.
/// </summary>
/// <param name="StartedAt">StartedAt.</param>
/// <param name="LastWriteAt">LastWriteAt.</param>
/// <param name="LastReadAt">LastReadAt.</param>
/// <param name="FramesSent">FramesSent.</param>
/// <param name="FramesReceived">FramesReceived.</param>
/// <param name="BytesSent">BytesSent.</param>
/// <param name="BytesReceived">BytesReceived.</param>
/// <param name="ReportsSent">ReportsSent.</param>
/// <param name="Errors">Errors.</param>
/// <param name="RxBadCrc">RxBadCrc.</param>
/// <param name="RxBadHmac">RxBadHmac.</param>
/// <param name="RxFallbackHmac">RxFallbackHmac.</param>
/// <param name="RxBadLength">RxBadLength.</param>
/// <param name="RxBadMagic">RxBadMagic.</param>
public sealed record HidUartClientStats(
    DateTimeOffset StartedAt,
    DateTimeOffset? LastWriteAt,
    DateTimeOffset? LastReadAt,
    long FramesSent,
    long FramesReceived,
    long BytesSent,
    long BytesReceived,
    long ReportsSent,
    long Errors,
    long RxBadCrc,
    long RxBadHmac,
    long RxFallbackHmac,
    long RxBadLength,
    long RxBadMagic);

/// <summary>
/// Trace entry for a single UART frame exchange.
/// </summary>
/// <param name="Ts">Ts.</param>
/// <param name="Dir">Dir.</param>
/// <param name="Info">Info.</param>
/// <param name="FrameHex">FrameHex.</param>
public sealed record UartTraceEntry(
    DateTimeOffset Ts,
    string Dir,
    string? Info,
    string FrameHex);

/// <summary>
/// Manages the UART link to the HID bridge (framing, HMAC, and report injection).
/// </summary>
public sealed class HidUartClient : IAsyncDisposable
{
    private const byte Magic = 0xF1;
    private const byte Version = 0x01;
    private const int HeaderLen = 6;
    private const int CrcLen = 2;
    private const int HmacLen = 16;

    private const byte FlagResponse = 0x01;
    private const byte FlagError = 0x02;

    private readonly SerialPort _serialPort;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private readonly CancellationTokenSource _rxCts = new();
    private readonly Task _rxTask;
    private readonly Channel<InjectItem> _injectChannel;
    private readonly Task _injectTask;
    private int _injectQueueDepth;
    private readonly object _listLock = new();
    private readonly byte[] _bootstrapKey;
    private byte[] _hmacKey;
    private bool _hasDerivedKey;
    private bool _forceBootstrap;
    private string? _deviceIdHex;
    private int _logLevel = 4;
    private int _seq;
    private long _framesSent;
    private long _framesReceived;
    private long _bytesSent;
    private long _bytesReceived;
    private long _reportsSent;
    private long _errors;
    private long _lastWriteUnixMs;
    private long _lastReadUnixMs;
    private long _rxBadCrc;
    private long _rxBadHmac;
    private long _rxFallbackHmac;
    private long _rxBadLength;
    private long _rxBadMagic;
    private readonly ConcurrentQueue<UartTraceEntry> _trace = new();
    private const int MaxTrace = 80;

    private readonly byte[] _rxBuf = new byte[1024];
    private int _rxLen;
    private bool _rxEsc;
    private HidInterfaceList? _lastInterfaceList;
    private TaskCompletionSource<HidInterfaceList>? _pendingInterfaceList;
    private readonly Dictionary<byte, ReportDescriptorSnapshot> _reportDesc = new();
    private readonly Dictionary<byte, TaskCompletionSource<ReportDescriptorSnapshot>> _pendingReportDesc = new();
    private readonly Dictionary<byte, ReportLayoutSnapshot> _reportLayout = new();
    private readonly Dictionary<byte, TaskCompletionSource<ReportLayoutSnapshot>> _pendingReportLayout = new();
    private readonly ConcurrentDictionary<byte, TaskCompletionSource<CtrlResponse>> _pending = new();
    private readonly ConcurrentDictionary<byte, bool> _pendingBootstrapKey = new();

    /// <summary>
    /// UART client for HID device communication.
    /// </summary>
    /// <param name="Seq">Seq.</param>
    /// <param name="Cmd">Cmd.</param>
    /// <param name="Flags">Flags.</param>
    /// <param name="Payload">Payload.</param>
    private sealed record CtrlResponse(byte Seq, byte Cmd, byte Flags, byte[] Payload);
    /// <summary>
    /// UART client for HID device communication.
    /// </summary>
    /// <param name="ItfSel">ItfSel.</param>
    /// <param name="Report">Report.</param>
    /// <param name="ReportLen">ReportLen.</param>
    /// <param name="TimeoutMs">TimeoutMs.</param>
    /// <param name="Retries">Retries.</param>
    /// <param name="Ct">Ct.</param>
    /// <param name="Tcs">Tcs.</param>
    private sealed record InjectItem(byte ItfSel, byte[] Report, int ReportLen, int TimeoutMs, int Retries, CancellationToken Ct, TaskCompletionSource<bool> Tcs);
    private const int InjectDropThreshold = 8;

    private readonly int _injectDropThreshold;

    /// <summary>
    /// Creates a UART client and starts background RX/inject workers.
    /// </summary>
    /// <param name="portName">The portName.</param>
    /// <param name="baudRate">The baudRate.</param>
    /// <param name="hmacKey">The hmacKey.</param>
    /// <param name="injectQueueCapacity">The injectQueueCapacity.</param>
    /// <param name="injectDropThreshold">The injectDropThreshold.</param>
    /// <returns>Result.</returns>
    public HidUartClient(string portName, int baudRate, string hmacKey, int injectQueueCapacity, int injectDropThreshold)
    {
        if (string.IsNullOrWhiteSpace(portName)) throw new ArgumentException("Port name is required.", nameof(portName));
        if (baudRate <= 0) throw new ArgumentOutOfRangeException(nameof(baudRate));

        string bootstrap = string.IsNullOrWhiteSpace(hmacKey) ? "changeme" : hmacKey;
        _bootstrapKey = System.Text.Encoding.UTF8.GetBytes(bootstrap);
        _hmacKey = _bootstrapKey;
        _hasDerivedKey = false;
        _injectDropThreshold = injectDropThreshold;

        _serialPort = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None,
            WriteTimeout = 1000,
            ReadTimeout = SerialPort.InfiniteTimeout,
            DtrEnable = true,
            RtsEnable = false
        };

        _serialPort.Open();
        _rxTask = Task.Run(ReadLoopAsync);
        if (injectQueueCapacity < 1) injectQueueCapacity = 1;
        _injectChannel = Channel.CreateBounded<InjectItem>(new BoundedChannelOptions(injectQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
        _injectTask = Task.Run(InjectLoopAsync);
    }

    /// <summary>
    /// Returns current UART transport statistics.
    /// </summary>
    /// <returns>Result.</returns>
    public HidUartClientStats GetStats()
    {
        long lastMs = Interlocked.Read(ref _lastWriteUnixMs);
        DateTimeOffset? lastWriteAt = lastMs == 0 ? null : DateTimeOffset.FromUnixTimeMilliseconds(lastMs);
        long lastReadMs = Interlocked.Read(ref _lastReadUnixMs);
        DateTimeOffset? lastReadAt = lastReadMs == 0 ? null : DateTimeOffset.FromUnixTimeMilliseconds(lastReadMs);

        return new HidUartClientStats(
            _startedAt,
            lastWriteAt,
            lastReadAt,
            Interlocked.Read(ref _framesSent),
            Interlocked.Read(ref _framesReceived),
            Interlocked.Read(ref _bytesSent),
            Interlocked.Read(ref _bytesReceived),
            Interlocked.Read(ref _reportsSent),
            Interlocked.Read(ref _errors),
            Interlocked.Read(ref _rxBadCrc),
            Interlocked.Read(ref _rxBadHmac),
            Interlocked.Read(ref _rxFallbackHmac),
            Interlocked.Read(ref _rxBadLength),
            Interlocked.Read(ref _rxBadMagic));
    }

    /// <summary>
    /// Returns a recent snapshot of UART trace entries.
    /// </summary>
    /// <param name="limit">The limit.</param>
    /// <returns>Result.</returns>
    public IReadOnlyList<UartTraceEntry> GetTraceSnapshot(int limit)
    {
        if (limit <= 0) limit = 20;
        if (limit > MaxTrace) limit = MaxTrace;
        UartTraceEntry[] items = _trace.ToArray();
        if (items.Length <= limit) return items;
        return items.Skip(items.Length - limit).ToArray();
    }

    /// <summary>
    /// Queues an input report for injection to the device.
    /// </summary>
    /// <param name="itfSel">The itfSel.</param>
    /// <param name="report">The report.</param>
    /// <param name="cancellationToken">The cancellationToken.</param>
    /// <returns>Result.</returns>
    public Task SendInjectReportAsync(byte itfSel, ReadOnlyMemory<byte> report, CancellationToken cancellationToken)
    {
        return SendInjectReportAsync(itfSel, report, 200, 2, false, cancellationToken);
    }

    /// <summary>
    /// Gets the cached device identifier (hex string).
    /// </summary>
    /// <returns>Result.</returns>
    public string? GetDeviceIdHex()
    {
        return _deviceIdHex;
    }

    /// <summary>
    /// Checks whether a derived HMAC key is active.
    /// </summary>
    /// <returns>Result.</returns>
    public bool HasDerivedKey()
    {
        return _hasDerivedKey && !_forceBootstrap;
    }

    /// <summary>
    /// Forces use of the bootstrap HMAC key for subsequent requests.
    /// </summary>
    public void UseBootstrapHmacKey()
    {
        _hmacKey = _bootstrapKey;
        _hasDerivedKey = false;
        _forceBootstrap = true;
    }

    /// <summary>
    /// Sets the derived HMAC key for authenticated frames.
    /// </summary>
    /// <param name="key">The key.</param>
    public void SetDerivedHmacKey(byte[] key)
    {
        if (key is null || key.Length == 0) return;
        _hmacKey = key;
        _hasDerivedKey = true;
        _forceBootstrap = false;
    }

    /// <summary>
    /// Checks whether bootstrap key usage is forced.
    /// </summary>
    /// <returns>Result.</returns>
    public bool IsBootstrapForced()
    {
        return _forceBootstrap;
    }

    /// <summary>
    /// Clears forced bootstrap key usage.
    /// </summary>
    public void ClearBootstrapForce()
    {
        _forceBootstrap = false;
        if (!_hasDerivedKey)
        {
            _hmacKey = _bootstrapKey;
        }
    }

    /// <summary>
    /// Requests the device identifier from firmware.
    /// </summary>
    /// <param name="timeoutMs">The timeoutMs.</param>
    /// <param name="cancellationToken">The cancellationToken.</param>
    /// <returns>Result.</returns>
    public async Task<byte[]?> RequestDeviceIdAsync(int timeoutMs, CancellationToken cancellationToken)
    {
        if (timeoutMs <= 0) timeoutMs = 500;
        CtrlResponse? resp = await SendCommandAsync(0x06, ReadOnlyMemory<byte>.Empty, 0, timeoutMs, 1, cancellationToken, true);
        if (resp is null || resp.Payload.Length < 1) return null;
        int len = resp.Payload[0];
        if (len <= 0) return null;
        if (len > resp.Payload.Length - 1) len = resp.Payload.Length - 1;
        if (len <= 0) return null;
        byte[] id = resp.Payload.AsSpan(1, len).ToArray();
        _deviceIdHex = Convert.ToHexString(id);
        return id;
    }

    /// <summary>
    /// Sends an inject report with retry and timeout settings.
    /// </summary>
    /// <param name="itfSel">The itfSel.</param>
    /// <param name="report">The report.</param>
    /// <param name="timeoutMs">The timeoutMs.</param>
    /// <param name="retries">The retries.</param>
    /// <param name="dropIfBusy">The dropIfBusy.</param>
    /// <param name="cancellationToken">The cancellationToken.</param>
    /// <returns>Result.</returns>
    public async Task SendInjectReportAsync(byte itfSel, ReadOnlyMemory<byte> report, int timeoutMs, int retries, bool dropIfBusy, CancellationToken cancellationToken)
    {
        if (report.Length == 0) return;
        if (report.Length > 255) throw new ArgumentOutOfRangeException(nameof(report), "Report must fit into 255 bytes.");
        if (dropIfBusy && _injectDropThreshold > 0 && Volatile.Read(ref _injectQueueDepth) >= _injectDropThreshold) return;

        byte[] reportCopy = ArrayPool<byte>.Shared.Rent(report.Length);
        report.Span.CopyTo(reportCopy);
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var item = new InjectItem(itfSel, reportCopy, report.Length, timeoutMs, retries, cancellationToken, tcs);
        if (dropIfBusy)
        {
            if (!_injectChannel.Writer.TryWrite(item))
            {
                ArrayPool<byte>.Shared.Return(reportCopy);
                return;
            }
        }
        else
        {
            await _injectChannel.Writer.WriteAsync(item, cancellationToken);
        }
        Interlocked.Increment(ref _injectQueueDepth);
        await tcs.Task.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Requests the HID interface list from the device.
    /// </summary>
    /// <param name="timeoutMs">The timeoutMs.</param>
    /// <param name="cancellationToken">The cancellationToken.</param>
    /// <returns>Result.</returns>
    public async Task<HidInterfaceList?> RequestInterfaceListAsync(int timeoutMs, CancellationToken cancellationToken)
    {
        return await RequestInterfaceListAsync(timeoutMs, false, cancellationToken);
    }

    /// <summary>
    /// Requests the HID interface list with explicit HMAC key selection.
    /// </summary>
    /// <param name="timeoutMs">The timeoutMs.</param>
    /// <param name="useBootstrapKey">The useBootstrapKey.</param>
    /// <param name="cancellationToken">The cancellationToken.</param>
    /// <returns>Result.</returns>
    public async Task<HidInterfaceList?> RequestInterfaceListAsync(int timeoutMs, bool useBootstrapKey, CancellationToken cancellationToken)
    {
        if (timeoutMs <= 0) timeoutMs = 500;

        TaskCompletionSource<HidInterfaceList> tcs;
        bool owner = false;
        lock (_listLock)
        {
            if (_pendingInterfaceList != null)
            {
                tcs = _pendingInterfaceList;
            }
            else
            {
                tcs = new TaskCompletionSource<HidInterfaceList>(TaskCreationOptions.RunContinuationsAsynchronously);
                _pendingInterfaceList = tcs;
                owner = true;
            }
        }

        try
        {
            if (owner)
            {
                await SendCommandAsync(0x02, ReadOnlyMemory<byte>.Empty, 0, timeoutMs, 1, cancellationToken, useBootstrapKey);
            }
        }
        catch
        {
            if (owner)
            {
                lock (_listLock)
                {
                    if (ReferenceEquals(_pendingInterfaceList, tcs))
                    {
                        _pendingInterfaceList = null;
                    }
                }
            }
            throw;
        }

        try
        {
            return await WaitWithTimeoutAsync(tcs.Task, timeoutMs, cancellationToken);
        }
        finally
        {
            if (owner)
            {
                lock (_listLock)
                {
                    if (ReferenceEquals(_pendingInterfaceList, tcs))
                    {
                        _pendingInterfaceList = null;
                    }
                }
            }
            else if (!tcs.Task.IsCompleted)
            {
                lock (_listLock)
                {
                    if (ReferenceEquals(_pendingInterfaceList, tcs))
                    {
                        _pendingInterfaceList = null;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Sets the device log level.
    /// </summary>
    /// <param name="level">The level.</param>
    /// <param name="cancellationToken">The cancellationToken.</param>
    /// <returns>Result.</returns>
    public Task SetLogLevelAsync(byte level, CancellationToken cancellationToken)
    {
        byte[] payload = new byte[] { level };
        _logLevel = level;
        return SendCommandAsync(0x03, payload, 0, 500, 1, cancellationToken, false);
    }

    /// <summary>
    /// Gets the cached device log level.
    /// </summary>
    /// <returns>Result.</returns>
    public byte GetLogLevel()
    {
        int level = Volatile.Read(ref _logLevel);
        if (level < 0) return 0;
        if (level > 4) return 4;
        return (byte)level;
    }

    /// <summary>
    /// Gets the most recently received interface list.
    /// </summary>
    /// <returns>Result.</returns>
    public HidInterfaceList? GetLastInterfaceList()
    {
        lock (_listLock)
        {
            return _lastInterfaceList;
        }
    }

    /// <summary>
    /// Requests a report descriptor for the specified interface.
    /// </summary>
    /// <param name="itf">The itf.</param>
    /// <param name="timeoutMs">The timeoutMs.</param>
    /// <param name="cancellationToken">The cancellationToken.</param>
    /// <returns>Result.</returns>
    public async Task<ReportDescriptorSnapshot?> RequestReportDescriptorAsync(byte itf, int timeoutMs, CancellationToken cancellationToken)
    {
        return await RequestReportDescriptorAsync(itf, timeoutMs, cancellationToken, false);
    }

    /// <summary>
    /// Requests a report descriptor with explicit HMAC key selection.
    /// </summary>
    /// <param name="itf">The itf.</param>
    /// <param name="timeoutMs">The timeoutMs.</param>
    /// <param name="cancellationToken">The cancellationToken.</param>
    /// <param name="useBootstrapKey">The useBootstrapKey.</param>
    /// <returns>Result.</returns>
    public async Task<ReportDescriptorSnapshot?> RequestReportDescriptorAsync(byte itf, int timeoutMs, CancellationToken cancellationToken, bool useBootstrapKey)
    {
        if (timeoutMs <= 0) timeoutMs = 500;

        TaskCompletionSource<ReportDescriptorSnapshot> tcs;
        lock (_listLock)
        {
            if (_pendingReportDesc.ContainsKey(itf))
            {
                throw new InvalidOperationException($"Report descriptor request already pending for itf={itf}.");
            }
            tcs = new TaskCompletionSource<ReportDescriptorSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingReportDesc[itf] = tcs;
        }

        byte[] payload = new byte[] { itf };
        try
        {
            await SendCommandAsync(0x04, payload, 0, timeoutMs, 1, cancellationToken, useBootstrapKey);
        }
        catch
        {
            lock (_listLock)
            {
                _pendingReportDesc.Remove(itf);
            }
            throw;
        }

        try
        {
            return await WaitWithTimeoutAsync(tcs.Task, timeoutMs, cancellationToken);
        }
        finally
        {
            lock (_listLock)
            {
                _pendingReportDesc.Remove(itf);
            }
        }
    }

    /// <summary>
    /// Requests a report layout for the given interface and report id.
    /// </summary>
    /// <param name="itf">The itf.</param>
    /// <param name="reportId">The reportId.</param>
    /// <param name="timeoutMs">The timeoutMs.</param>
    /// <param name="cancellationToken">The cancellationToken.</param>
    /// <returns>Result.</returns>
    public async Task<ReportLayoutSnapshot?> RequestReportLayoutAsync(byte itf, byte reportId, int timeoutMs, CancellationToken cancellationToken)
    {
        return await RequestReportLayoutAsync(itf, reportId, timeoutMs, cancellationToken, false);
    }

    /// <summary>
    /// Requests a report layout with explicit HMAC key selection.
    /// </summary>
    /// <param name="itf">The itf.</param>
    /// <param name="reportId">The reportId.</param>
    /// <param name="timeoutMs">The timeoutMs.</param>
    /// <param name="cancellationToken">The cancellationToken.</param>
    /// <param name="useBootstrapKey">The useBootstrapKey.</param>
    /// <returns>Result.</returns>
    public async Task<ReportLayoutSnapshot?> RequestReportLayoutAsync(byte itf, byte reportId, int timeoutMs, CancellationToken cancellationToken, bool useBootstrapKey)
    {
        if (timeoutMs <= 0) timeoutMs = 500;

        TaskCompletionSource<ReportLayoutSnapshot> tcs;
        lock (_listLock)
        {
            if (_pendingReportLayout.ContainsKey(itf))
            {
                throw new InvalidOperationException($"Report layout request already pending for itf={itf}.");
            }
            tcs = new TaskCompletionSource<ReportLayoutSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingReportLayout[itf] = tcs;
        }

        byte[] payload = new byte[] { itf, reportId };
        try
        {
            await SendCommandAsync(0x05, payload, 0, timeoutMs, 1, cancellationToken, useBootstrapKey);
        }
        catch
        {
            lock (_listLock)
            {
                _pendingReportLayout.Remove(itf);
            }
            throw;
        }

        try
        {
            return await WaitWithTimeoutAsync(tcs.Task, timeoutMs, cancellationToken);
        }
        finally
        {
            lock (_listLock)
            {
                _pendingReportLayout.Remove(itf);
            }
        }
    }

    /// <summary>
    /// Gets the cached report descriptor for the interface.
    /// </summary>
    /// <param name="itf">The itf.</param>
    /// <returns>Result.</returns>
    public ReportDescriptorSnapshot? GetReportDescriptor(byte itf)
    {
        lock (_listLock)
        {
            _reportDesc.TryGetValue(itf, out var snap);
            return snap;
        }
    }

    /// <summary>
    /// Gets a copy of the cached report descriptor.
    /// </summary>
    /// <param name="itf">The itf.</param>
    /// <returns>Result.</returns>
    public ReportDescriptorSnapshot? GetReportDescriptorCopy(byte itf)
    {
        lock (_listLock)
        {
            if (_reportDesc.TryGetValue(itf, out var snap))
            {
                return snap with { Data = snap.Data.ToArray() };
            }
            return null;
        }
    }

    /// <summary>
    /// Gets a copy of the cached report layout.
    /// </summary>
    /// <param name="itf">The itf.</param>
    /// <returns>Result.</returns>
    public ReportLayoutSnapshot? GetReportLayoutCopy(byte itf)
    {
        lock (_listLock)
        {
            if (_reportLayout.TryGetValue(itf, out var snap))
            {
                return snap with
                {
                    Mouse = snap.Mouse is null ? null : snap.Mouse with { },
                    Keyboard = snap.Keyboard is null ? null : snap.Keyboard with { }
                };
            }
            return null;
        }
    }

    /// <summary>
    /// Executes NextSeq.
    /// </summary>
    /// <returns>Result.</returns>
    private byte NextSeq()
    {
        int seq = Interlocked.Increment(ref _seq);
        return (byte)(seq & 0xFF);
    }

    /// <summary>
    /// Sends command.
    /// </summary>
    /// <param name="cmd">The cmd.</param>
    /// <param name="payload">The payload.</param>
    /// <param name="reportBytes">The reportBytes.</param>
    /// <param name="timeoutMs">The timeoutMs.</param>
    /// <param name="retries">The retries.</param>
    /// <param name="cancellationToken">The cancellationToken.</param>
    /// <returns>Result.</returns>
    private async Task<CtrlResponse?> SendCommandAsync(byte cmd, ReadOnlyMemory<byte> payload, int reportBytes, int timeoutMs, int retries, CancellationToken cancellationToken)
    {
        return await SendCommandAsync(cmd, payload, reportBytes, timeoutMs, retries, cancellationToken, false);
    }

    /// <summary>
    /// Sends command.
    /// </summary>
    /// <param name="cmd">The cmd.</param>
    /// <param name="payload">The payload.</param>
    /// <param name="reportBytes">The reportBytes.</param>
    /// <param name="timeoutMs">The timeoutMs.</param>
    /// <param name="retries">The retries.</param>
    /// <param name="cancellationToken">The cancellationToken.</param>
    /// <param name="useBootstrapKey">The useBootstrapKey.</param>
    /// <param name="allowBootstrapFallback">The allowBootstrapFallback.</param>
    /// <returns>Result.</returns>
    private async Task<CtrlResponse?> SendCommandAsync(byte cmd, ReadOnlyMemory<byte> payload, int reportBytes, int timeoutMs, int retries, CancellationToken cancellationToken, bool useBootstrapKey, bool allowBootstrapFallback = true)
    {
        if (timeoutMs <= 0) timeoutMs = 500;
        if (retries < 0) retries = 0;

        for (int attempt = 0; attempt <= retries; attempt++)
        {
            byte seq = NextSeq();
            var tcs = new TaskCompletionSource<CtrlResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_pending.TryAdd(seq, tcs))
            {
                Interlocked.Increment(ref _errors);
                continue;
            }
            if (useBootstrapKey)
            {
                _pendingBootstrapKey[seq] = true;
            }

            try
            {
                byte[]? keyOverride = useBootstrapKey ? _bootstrapKey : null;
                byte[] frame = BuildFrame(seq, cmd, payload.Span, keyOverride);
                await SendFrameAsync(frame, reportBytes, cancellationToken);

                CtrlResponse? resp = await WaitWithTimeoutAsync(tcs.Task, timeoutMs, cancellationToken);
                if (resp is null)
                {
                    if (cancellationToken.IsCancellationRequested) return null;
                    if (attempt >= retries) return null;
                    continue;
                }
                if ((resp.Flags & FlagError) != 0)
                {
                    string message = resp.Payload.Length > 0 ? $"cmd=0x{cmd:X2} error=0x{resp.Payload[0]:X2}" : $"cmd=0x{cmd:X2} error";
                    throw new InvalidOperationException(message);
                }
                return resp;
            }
            finally
            {
                _pendingBootstrapKey.TryRemove(seq, out _);
                _pending.TryRemove(seq, out _);
            }
        }

        if (!useBootstrapKey && allowBootstrapFallback && _hasDerivedKey)
        {
            CtrlResponse? fallback = await SendCommandAsync(cmd, payload, reportBytes, timeoutMs, retries, cancellationToken, true, false);
            if (fallback is not null)
            {
                UseBootstrapHmacKey();
            }
            return fallback;
        }

        return null;
    }

    /// <summary>
    /// Builds frame.
    /// </summary>
    /// <param name="seq">The seq.</param>
    /// <param name="cmd">The cmd.</param>
    /// <param name="payload">The payload.</param>
    /// <param name="keyOverride">The keyOverride.</param>
    /// <returns>Result.</returns>
    private byte[] BuildFrame(byte seq, byte cmd, ReadOnlySpan<byte> payload, byte[]? keyOverride)
    {
        if (payload.Length > 240) throw new ArgumentOutOfRangeException(nameof(payload), "Payload must fit into 240 bytes.");
        int payloadLen = payload.Length;
        int totalLen = HeaderLen + payloadLen + CrcLen + HmacLen;
        byte[] frame = new byte[totalLen];
        frame[0] = Magic;
        frame[1] = Version;
        frame[2] = 0;
        frame[3] = seq;
        frame[4] = cmd;
        frame[5] = (byte)payloadLen;
        if (payloadLen > 0)
        {
            payload.CopyTo(frame.AsSpan(6));
        }

        ushort crc = Crc16Ccitt(frame.AsSpan(0, HeaderLen + payloadLen));
        frame[6 + payloadLen] = (byte)(crc & 0xFF);
        frame[7 + payloadLen] = (byte)(crc >> 8);

        Span<byte> mac = stackalloc byte[32];
        ReadOnlySpan<byte> key = keyOverride is null ? GetHmacKeyForCmd(cmd) : keyOverride;
        ComputeHmac(key, frame.AsSpan(0, HeaderLen + payloadLen + CrcLen), mac);
        mac.Slice(0, HmacLen).CopyTo(frame.AsSpan(8 + payloadLen, HmacLen));

        return frame;
    }

    /// <summary>
    /// Sends frame.
    /// </summary>
    /// <param name="frame">The frame.</param>
    /// <param name="reportBytes">The reportBytes.</param>
    /// <param name="cancellationToken">The cancellationToken.</param>
    /// <returns>Result.</returns>
    private async Task SendFrameAsync(byte[] frame, int reportBytes, CancellationToken cancellationToken)
    {
        byte[] slip = Slip.Encode(frame.AsSpan());

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            _serialPort.Write(slip, 0, slip.Length);
        }
        catch
        {
            Interlocked.Increment(ref _errors);
            throw;
        }
        finally
        {
            _writeLock.Release();
        }

        Interlocked.Increment(ref _framesSent);
        Interlocked.Add(ref _bytesSent, slip.Length);
        if (reportBytes > 0)
        {
            Interlocked.Add(ref _reportsSent, reportBytes);
        }
        Interlocked.Exchange(ref _lastWriteUnixMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        EnqueueTrace("tx", frame, null);
    }

    /// <summary>
    /// Reads loop.
    /// </summary>
    /// <returns>Result.</returns>
    private async Task ReadLoopAsync()
    {
        byte[] buf = new byte[256];
        while (!_rxCts.IsCancellationRequested)
        {
            int read;
            try
            {
                read = await _serialPort.BaseStream.ReadAsync(buf.AsMemory(0, buf.Length), _rxCts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch
            {
                Interlocked.Increment(ref _errors);
                await Task.Delay(50);
                continue;
            }

            if (read <= 0) continue;
            for (int i = 0; i < read; i++)
            {
                FeedRxByte(buf[i]);
            }
        }
    }

    private static async Task<T?> WaitWithTimeoutAsync<T>(Task<T> task, int timeoutMs, CancellationToken cancellationToken)
    {
        if (timeoutMs <= 0) timeoutMs = 500;
        Task delay = Task.Delay(timeoutMs);
        Task completed = await Task.WhenAny(task, delay).ConfigureAwait(false);
        if (completed == task)
        {
            return await task.ConfigureAwait(false);
        }
        if (cancellationToken.IsCancellationRequested)
        {
            return default;
        }
        return default;
    }

    /// <summary>
    /// Executes FeedRxByte.
    /// </summary>
    /// <param name="b">The b.</param>
    private void FeedRxByte(byte b)
    {
        const byte End = 0xC0;
        const byte Esc = 0xDB;
        const byte EscEnd = 0xDC;
        const byte EscEsc = 0xDD;

        if (b == End)
        {
            if (_rxLen > 0)
            {
                HandleFrame(new ReadOnlySpan<byte>(_rxBuf, 0, _rxLen));
            }
            _rxLen = 0;
            _rxEsc = false;
            return;
        }

        if (b == Esc)
        {
            _rxEsc = true;
            return;
        }

        if (_rxEsc)
        {
            if (b == EscEnd) b = End;
            else if (b == EscEsc) b = Esc;
            _rxEsc = false;
        }

        if (_rxLen < _rxBuf.Length)
        {
            _rxBuf[_rxLen++] = b;
        }
        else
        {
            _rxLen = 0;
            _rxEsc = false;
        }
    }

    /// <summary>
    /// Handles frame.
    /// </summary>
    /// <param name="frame">The frame.</param>
    private void HandleFrame(ReadOnlySpan<byte> frame)
    {
        if (!TryParseFrame(frame, out CtrlResponse response, out FrameParseError error, out bool usedFallback))
        {
            EnqueueTrace("rx", frame.ToArray(), error.ToString());
            switch (error)
            {
                case FrameParseError.BadCrc:
                    Interlocked.Increment(ref _rxBadCrc);
                    break;
                case FrameParseError.BadHmac:
                    Interlocked.Increment(ref _rxBadHmac);
                    break;
                case FrameParseError.BadLength:
                    Interlocked.Increment(ref _rxBadLength);
                    break;
                case FrameParseError.BadMagic:
                    Interlocked.Increment(ref _rxBadMagic);
                    break;
            }
            return;
        }
        if (usedFallback)
        {
            Interlocked.Increment(ref _rxFallbackHmac);
            if (_hasDerivedKey)
            {
                _forceBootstrap = true;
            }
        }
        Interlocked.Increment(ref _framesReceived);
        Interlocked.Add(ref _bytesReceived, frame.Length);
        Interlocked.Exchange(ref _lastReadUnixMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        EnqueueTrace("rx", frame.ToArray(), null);
        ProcessResponse(response);
    }

    /// <summary>
    /// Executes EnqueueTrace.
    /// </summary>
    /// <param name="dir">The dir.</param>
    /// <param name="frame">The frame.</param>
    /// <param name="info">The info.</param>
    private void EnqueueTrace(string dir, byte[] frame, string? info)
    {
        string hex = Convert.ToHexString(frame);
        _trace.Enqueue(new UartTraceEntry(DateTimeOffset.UtcNow, dir, info, hex));
        while (_trace.Count > MaxTrace && _trace.TryDequeue(out _)) { }
    }

    /// <summary>
    /// UART client for HID device communication.
    /// </summary>
    private enum FrameParseError
    {
        None,
        BadLength,
        BadMagic,
        BadCrc,
        BadHmac
    }

    /// <summary>
    /// Tries to parse frame.
    /// </summary>
    /// <param name="frame">The frame.</param>
    /// <param name="response">The response.</param>
    /// <param name="error">The error.</param>
    /// <param name="usedFallback">The usedFallback.</param>
    /// <returns>Result.</returns>
    private bool TryParseFrame(ReadOnlySpan<byte> frame, out CtrlResponse response, out FrameParseError error, out bool usedFallback)
    {
        response = default!;
        error = FrameParseError.None;
        usedFallback = false;
        if (frame.Length < HeaderLen + CrcLen + HmacLen)
        {
            error = FrameParseError.BadLength;
            return false;
        }
        if (frame[0] != Magic || frame[1] != Version)
        {
            error = FrameParseError.BadMagic;
            return false;
        }

        byte payloadLen = frame[5];
        int totalLen = HeaderLen + payloadLen + CrcLen + HmacLen;
        if (frame.Length != totalLen)
        {
            error = FrameParseError.BadLength;
            return false;
        }

        byte seq = frame[3];
        byte cmd = frame[4];
        ReadOnlySpan<byte> key = GetHmacKeyForCmd(cmd);
        if (_pendingBootstrapKey.TryGetValue(seq, out bool useBootstrap) && useBootstrap)
        {
            key = _bootstrapKey;
        }

        ushort crc = Crc16Ccitt(frame.Slice(0, HeaderLen + payloadLen));
        ushort msgCrc = (ushort)(frame[6 + payloadLen] | (frame[7 + payloadLen] << 8));
        if (crc != msgCrc)
        {
            error = FrameParseError.BadCrc;
            return false;
        }

        Span<byte> mac = stackalloc byte[32];
        ComputeHmac(key, frame.Slice(0, HeaderLen + payloadLen + CrcLen), mac);
        if (!ConstantTimeEquals(mac.Slice(0, HmacLen), frame.Slice(8 + payloadLen, HmacLen)))
        {
            ReadOnlySpan<byte> altKey = key.SequenceEqual(_bootstrapKey) ? (_hasDerivedKey ? _hmacKey : ReadOnlySpan<byte>.Empty) : _bootstrapKey;
            if (altKey.Length == 0)
            {
                error = FrameParseError.BadHmac;
                return false;
            }
            ComputeHmac(altKey, frame.Slice(0, HeaderLen + payloadLen + CrcLen), mac);
            if (!ConstantTimeEquals(mac.Slice(0, HmacLen), frame.Slice(8 + payloadLen, HmacLen)))
            {
                error = FrameParseError.BadHmac;
                return false;
            }
            usedFallback = true;
        }

        byte flags = frame[2];
        byte[] payload = frame.Slice(6, payloadLen).ToArray();
        response = new CtrlResponse(seq, cmd, flags, payload);
        return true;
    }

    /// <summary>
    /// Executes ProcessResponse.
    /// </summary>
    /// <param name="response">The response.</param>
    private void ProcessResponse(CtrlResponse response)
    {
        if ((response.Flags & FlagResponse) == 0) return;

        if ((response.Flags & FlagError) == 0)
        {
            if (response.Cmd == 0x02)
            {
                HidInterfaceList? list = ParseInterfaceList(response.Payload);
                if (list is not null)
                {
                    lock (_listLock)
                    {
                        _lastInterfaceList = list;
                        _pendingInterfaceList?.TrySetResult(list);
                    }
                }
            }
            else if (response.Cmd == 0x04)
            {
                ReportDescriptorSnapshot? snap = ParseReportDesc(response.Payload);
                if (snap is not null)
                {
                    lock (_listLock)
                    {
                        _reportDesc[snap.Itf] = snap;
                        if (_pendingReportDesc.TryGetValue(snap.Itf, out var tcs))
                        {
                            tcs.TrySetResult(snap);
                        }
                    }
                }
            }
            else if (response.Cmd == 0x05)
            {
                ReportLayoutSnapshot? snap = ParseReportLayout(response.Payload);
                if (snap is not null)
                {
                    lock (_listLock)
                    {
                        _reportLayout[snap.Itf] = snap;
                        if (_pendingReportLayout.TryGetValue(snap.Itf, out var tcs))
                        {
                            tcs.TrySetResult(snap);
                        }
                    }
                }
            }
        }

        if (_pending.TryRemove(response.Seq, out var pending))
        {
            pending.TrySetResult(response);
        }
        _pendingBootstrapKey.TryRemove(response.Seq, out _);
    }

    /// <summary>
    /// Parses interface list.
    /// </summary>
    /// <param name="payload">The payload.</param>
    /// <returns>Result.</returns>
    private HidInterfaceList? ParseInterfaceList(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 1) return null;
        int count = payload[0];
        int available = payload.Length - 1;
        int entrySize = 7;
        int maxCount = entrySize > 0 ? available / entrySize : 0;
        if (count > maxCount) count = maxCount;
        if (count < 0) count = 0;

        var list = new List<HidInterfaceInfo>(count);
        int offset = 1;
        for (int i = 0; i < count; i++)
        {
            if (offset + entrySize > payload.Length) break;
            byte devAddr = payload[offset++];
            byte itf = payload[offset++];
            byte itfProtocol = payload[offset++];
            byte protocol = payload[offset++];
            byte inferredType = payload[offset++];
            byte active = payload[offset++];
            byte mounted = payload[offset++];
            list.Add(new HidInterfaceInfo(devAddr, itf, itfProtocol, protocol, inferredType, active != 0, mounted != 0));
        }

        return new HidInterfaceList(DateTimeOffset.UtcNow, list);
    }

    /// <summary>
    /// Parses report desc.
    /// </summary>
    /// <param name="payload">The payload.</param>
    /// <returns>Result.</returns>
    private ReportDescriptorSnapshot? ParseReportDesc(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 4) return null;
        byte itf = payload[0];
        int totalLen = payload[1] | (payload[2] << 8);
        byte flags = payload[3];
        bool truncated = (flags & 0x01) != 0;
        byte[] data = payload.Slice(4).ToArray();
        return new ReportDescriptorSnapshot(DateTimeOffset.UtcNow, itf, totalLen, truncated, data);
    }

    /// <summary>
    /// Parses report layout.
    /// </summary>
    /// <param name="payload">The payload.</param>
    /// <returns>Result.</returns>
    private ReportLayoutSnapshot? ParseReportLayout(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 18) return null;
        byte itf = payload[0];
        byte reportId = payload[1];
        byte layoutKind = payload[2];
        byte flags = payload[3];

        MouseLayoutInfo? mouse = null;
        if ((layoutKind & 0x01) != 0)
        {
            mouse = new MouseLayoutInfo(
                reportId,
                payload[4],
                payload[5],
                payload[6],
                payload[7],
                payload[8],
                payload[9] != 0,
                payload[10],
                payload[11],
                payload[12] != 0,
                payload[13],
                payload[14],
                payload[15] != 0,
                flags);
        }

        KeyboardLayoutInfo? keyboard = null;
        if ((layoutKind & 0x02) != 0)
        {
            keyboard = new KeyboardLayoutInfo(reportId, payload[16], payload[17] != 0);
        }

        return new ReportLayoutSnapshot(DateTimeOffset.UtcNow, itf, reportId, layoutKind, flags, mouse, keyboard);
    }

    /// <summary>
    /// Executes Crc16Ccitt.
    /// </summary>
    /// <param name="data">The data.</param>
    /// <returns>Result.</returns>
    private static ushort Crc16Ccitt(ReadOnlySpan<byte> data)
    {
        const ushort seed = 0xFFFF;
        ushort crc = seed;
        foreach (byte b in data)
        {
            crc ^= (ushort)(b << 8);
            for (int i = 0; i < 8; i++)
            {
                crc = (ushort)((crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : (crc << 1));
            }
        }
        return crc;
    }

    /// <summary>
    /// Gets hmac key for cmd.
    /// </summary>
    /// <param name="cmd">The cmd.</param>
    /// <returns>Result.</returns>
    private ReadOnlySpan<byte> GetHmacKeyForCmd(byte cmd)
    {
        if (cmd == 0x06) return _bootstrapKey;
        if (_forceBootstrap) return _bootstrapKey;
        return _hasDerivedKey ? _hmacKey : _bootstrapKey;
    }

    /// <summary>
    /// Executes ComputeHmac.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <param name="data">The data.</param>
    /// <param name="output">The output.</param>
    private void ComputeHmac(ReadOnlySpan<byte> key, ReadOnlySpan<byte> data, Span<byte> output)
    {
        using var hmac = new HMACSHA256(key.ToArray());
        byte[] hash = hmac.ComputeHash(data.ToArray());
        hash.CopyTo(output);
    }

    /// <summary>
    /// Executes ConstantTimeEquals.
    /// </summary>
    /// <param name="a">The a.</param>
    /// <param name="b">The b.</param>
    /// <returns>Result.</returns>
    private static bool ConstantTimeEquals(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        if (a.Length != b.Length) return false;
        int diff = 0;
        for (int i = 0; i < a.Length; i++)
        {
            diff |= a[i] ^ b[i];
        }
        return diff == 0;
    }

    /// <summary>
    /// Executes DisposeAsync.
    /// </summary>
    /// <returns>Result.</returns>
    public ValueTask DisposeAsync()
    {
        try
        {
            _rxCts.Cancel();
            _injectChannel.Writer.TryComplete();
            try { _rxTask.Wait(250); } catch { }
            try { _injectTask.Wait(250); } catch { }
            _serialPort.Close();
            _serialPort.Dispose();
        }
        catch
        {
            // ignore
        }
        _writeLock.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Executes InjectLoopAsync.
    /// </summary>
    /// <returns>Result.</returns>
    private async Task InjectLoopAsync()
    {
        var reader = _injectChannel.Reader;
        while (await reader.WaitToReadAsync(_rxCts.Token))
        {
            while (reader.TryRead(out InjectItem? item))
            {
                if (item is null)
                {
                    continue;
                }
                if (item.Ct.IsCancellationRequested)
                {
                    item.Tcs.TrySetCanceled(item.Ct);
                    ArrayPool<byte>.Shared.Return(item.Report);
                    Interlocked.Decrement(ref _injectQueueDepth);
                    continue;
                }

                try
                {
                    int payloadLen = 2 + item.ReportLen;
                    byte[] payload = ArrayPool<byte>.Shared.Rent(payloadLen);
                    try
                    {
                        payload[0] = item.ItfSel;
                        payload[1] = (byte)item.ReportLen;
                        item.Report.AsSpan(0, item.ReportLen).CopyTo(payload.AsSpan(2));
                        CtrlResponse? resp = await SendCommandAsync(0x01, payload.AsMemory(0, payloadLen), item.ReportLen, item.TimeoutMs, item.Retries, _rxCts.Token, false);
                        if (resp is null)
                        {
                            Interlocked.Increment(ref _errors);
                            if (_hasDerivedKey && !_forceBootstrap)
                            {
                                CtrlResponse? fallback = await SendCommandAsync(0x01, payload.AsMemory(0, payloadLen), item.ReportLen, item.TimeoutMs, 0, _rxCts.Token, true);
                                if (fallback is not null)
                                {
                                    UseBootstrapHmacKey();
                                }
                            }
                        }
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(payload);
                    }

                    item.Tcs.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    item.Tcs.TrySetException(ex);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(item.Report);
                    Interlocked.Decrement(ref _injectQueueDepth);
                }
            }
        }
    }
}
