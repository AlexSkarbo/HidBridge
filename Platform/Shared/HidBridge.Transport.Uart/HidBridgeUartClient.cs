using System.Collections.Concurrent;
using System.IO.Ports;
using System.Security.Cryptography;
using System.Text;
using HidBridge.Contracts;
using HidBridge.Hid;

namespace HidBridge.Transport.Uart;

/// <summary>
/// Sends HID reports over the legacy UART framing protocol understood by the bridge firmware.
/// </summary>
public sealed class HidBridgeUartClient : IAsyncDisposable
{
    private const byte Magic = 0xF1;
    private const byte Version = 0x01;
    private const byte FlagResponse = 0x01;
    private const byte FlagError = 0x02;
    private const byte FlagResponseLegacy = 0x80;
    private const byte FlagErrorLegacy = 0x40;
    private const int HeaderLen = 6;
    private const int CrcLen = 2;
    private const int HmacLen = 16;

    private readonly HidBridgeUartClientOptions _options;
    private readonly SerialPort _port;
    private readonly SemaphoreSlim _ioLock = new(1, 1);
    private readonly List<byte> _rxFrame = new(512);
    private readonly byte[] _bootstrapHmacKey;
    private byte[]? _derivedHmacKey;
    private bool _forceBootstrapKey;
    private bool _usingDerivedKey;
    private bool _rxEscaped;
    private int _seq;
    private byte _mouseButtons;
    private byte? _resolvedMouseInterface;
    private byte? _resolvedKeyboardInterface;
    private readonly ConcurrentDictionary<byte, MouseLayoutInfo?> _mouseLayouts = new();
    private readonly ConcurrentDictionary<byte, KeyboardLayoutInfo?> _keyboardLayouts = new();

    /// <summary>
    /// Initializes a UART client and opens the configured serial port immediately.
    /// </summary>
    /// <param name="options">The transport configuration to use.</param>
    public HidBridgeUartClient(HidBridgeUartClientOptions options)
    {
        _options = options;
        _bootstrapHmacKey = Encoding.UTF8.GetBytes(string.IsNullOrWhiteSpace(options.HmacKey) ? "changeme" : options.HmacKey);
        _port = new SerialPort(options.PortName, options.BaudRate, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None,
            WriteTimeout = 1000,
            ReadTimeout = 50,
            DtrEnable = true,
            RtsEnable = false,
        };
        _port.Open();
    }

    /// <summary>
    /// Reads a best-effort health snapshot including resolved interface selectors.
    /// </summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The current health snapshot of the transport and interface resolution state.</returns>
    public async Task<HidBridgeUartHealth> GetHealthAsync(CancellationToken cancellationToken)
    {
        HidInterfaceList? list = null;
        try
        {
            list = await RequestInterfaceListAsync(cancellationToken);
            _resolvedMouseInterface ??= ResolveInterfaceSelectorFromList(list, _options.MouseInterfaceSelector, true);
            _resolvedKeyboardInterface ??= ResolveInterfaceSelectorFromList(list, _options.KeyboardInterfaceSelector, false);
        }
        catch
        {
            // Keep health best-effort. Connector will surface execution failures separately.
        }

        return new HidBridgeUartHealth(
            _options.PortName,
            _options.BaudRate,
            _options.MouseInterfaceSelector,
            _options.KeyboardInterfaceSelector,
            _resolvedMouseInterface,
            _resolvedKeyboardInterface,
            list?.Interfaces.Count ?? 0,
            _port.IsOpen,
            DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Gets whether the UART client currently signs requests with a derived device key.
    /// </summary>
    public bool IsUsingDerivedKey => _usingDerivedKey;

    /// <summary>
    /// Resolves the device identifier and switches to a derived HMAC key when a master secret is provided.
    /// </summary>
    /// <param name="masterSecret">The master secret used to derive per-device key material.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns><c>true</c> when the derived key has been applied; otherwise <c>false</c>.</returns>
    public async Task<bool> EnsureDerivedKeyAsync(string? masterSecret, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(masterSecret) || _usingDerivedKey)
        {
            return _usingDerivedKey;
        }

        var deviceId = await RequestDeviceIdAsync(cancellationToken);
        if (deviceId is null || deviceId.Length == 0)
        {
            return false;
        }

        _derivedHmacKey = ComputeDerivedKey(masterSecret, deviceId);
        _forceBootstrapKey = false;
        _usingDerivedKey = true;
        return true;
    }

    /// <summary>
    /// Forces subsequent UART commands to use the bootstrap HMAC key.
    /// </summary>
    public void UseBootstrapHmacKey()
    {
        _forceBootstrapKey = true;
        _usingDerivedKey = false;
    }

    /// <summary>
    /// Sends one relative mouse movement report.
    /// </summary>
    /// <param name="dx">Relative X movement.</param>
    /// <param name="dy">Relative Y movement.</param>
    /// <param name="wheel">Relative wheel movement.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <param name="interfaceSelector">Overrides the configured logical or physical mouse interface selector.</param>
    public async Task SendMouseMoveAsync(int dx, int dy, int wheel, CancellationToken cancellationToken, byte? interfaceSelector = null)
    {
        var itf = await ResolveInterfaceAsync(interfaceSelector ?? _options.MouseInterfaceSelector, preferMouse: true, cancellationToken);
        if (!_mouseLayouts.TryGetValue(itf, out var layout))
        {
            layout = await TryRequestMouseLayoutAsync(itf, cancellationToken);
            _mouseLayouts[itf] = layout;
        }

        byte[] report = layout is not null
            ? HidReports.BuildMouseFromLayoutInfo(layout, _mouseButtons, dx, dy, wheel)
            : HidReports.BuildBootMouse(_mouseButtons, dx, dy, wheel, 4);

        await SendInjectReportAsync(itf, report, cancellationToken);
    }

    /// <summary>
    /// Applies a full mouse button bitmask and emits a synchronized mouse report.
    /// </summary>
    /// <param name="mask">The button mask to apply.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <param name="interfaceSelector">Overrides the configured mouse interface selector.</param>
    public async Task SetMouseButtonsMaskAsync(byte mask, CancellationToken cancellationToken, byte? interfaceSelector = null)
    {
        _mouseButtons = mask;
        await SendMouseMoveAsync(0, 0, 0, cancellationToken, interfaceSelector);
    }

    /// <summary>
    /// Changes the state of one named mouse button and emits the updated report.
    /// </summary>
    /// <param name="button">The logical mouse button name.</param>
    /// <param name="down">Whether the button should be pressed or released.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <param name="interfaceSelector">Overrides the configured mouse interface selector.</param>
    public async Task SetMouseButtonAsync(string button, bool down, CancellationToken cancellationToken, byte? interfaceSelector = null)
    {
        var mask = button.ToLowerInvariant() switch
        {
            "left" => (byte)0x01,
            "right" => (byte)0x02,
            "middle" => (byte)0x04,
            "back" => (byte)0x08,
            "forward" => (byte)0x10,
            _ => throw new ArgumentOutOfRangeException(nameof(button), $"Unsupported mouse button '{button}'."),
        };

        _mouseButtons = down ? (byte)(_mouseButtons | mask) : (byte)(_mouseButtons & ~mask);
        await SendMouseMoveAsync(0, 0, 0, cancellationToken, interfaceSelector);
    }

    /// <summary>
    /// Types a text string using the basic ASCII-to-HID mapping.
    /// </summary>
    /// <param name="text">The text to emit.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <param name="interfaceSelector">Overrides the configured keyboard interface selector.</param>
    public async Task SendKeyboardTextAsync(string text, CancellationToken cancellationToken, byte? interfaceSelector = null)
    {
        if (string.IsNullOrEmpty(text)) return;

        var itf = await ResolveInterfaceAsync(interfaceSelector ?? _options.KeyboardInterfaceSelector, preferMouse: false, cancellationToken);
        var layout = await EnsureKeyboardLayoutAsync(itf, cancellationToken);
        foreach (var ch in text)
        {
            if (!HidReports.TryMapAsciiToHidKey(ch, out var modifiers, out var usage)) continue;
            await SendKeyboardReportAsync(itf, layout, modifiers, usage, cancellationToken);
            await SendKeyboardResetAsync(itf, layout, cancellationToken);
        }
    }

    /// <summary>
    /// Sends one keypress composed of explicit HID usage and modifiers.
    /// </summary>
    /// <param name="usage">The HID usage to press.</param>
    /// <param name="modifiers">The HID modifier bitmask.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <param name="interfaceSelector">Overrides the configured keyboard interface selector.</param>
    public async Task SendKeyboardPressAsync(byte usage, byte modifiers, CancellationToken cancellationToken, byte? interfaceSelector = null)
    {
        var itf = await ResolveInterfaceAsync(interfaceSelector ?? _options.KeyboardInterfaceSelector, preferMouse: false, cancellationToken);
        var layout = await EnsureKeyboardLayoutAsync(itf, cancellationToken);
        await SendKeyboardReportAsync(itf, layout, modifiers, usage, cancellationToken);
        await SendKeyboardResetAsync(itf, layout, cancellationToken);
    }

    /// <summary>
    /// Sends a shortcut described with a token syntax such as <c>Ctrl+Shift+P</c>.
    /// </summary>
    /// <param name="shortcut">The shortcut expression to parse.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <param name="interfaceSelector">Overrides the configured keyboard interface selector.</param>
    public async Task SendKeyboardShortcutAsync(string shortcut, CancellationToken cancellationToken, byte? interfaceSelector = null)
    {
        var tokens = shortcut.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        byte modifiers = 0;
        byte keyUsage = 0;

        foreach (var token in tokens)
        {
            switch (token.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= 0x01;
                    break;
                case "shift":
                    modifiers |= 0x02;
                    break;
                case "alt":
                    modifiers |= 0x04;
                    break;
                case "win":
                case "meta":
                    modifiers |= 0x08;
                    break;
                default:
                    if (token.Length == 1 && HidReports.TryMapAsciiToHidKey(token[0], out var extraModifiers, out var usage))
                    {
                        modifiers |= extraModifiers;
                        keyUsage = usage;
                    }
                    else if (token.Equals("enter", StringComparison.OrdinalIgnoreCase))
                    {
                        keyUsage = 0x28;
                    }
                    else if (token.Equals("space", StringComparison.OrdinalIgnoreCase))
                    {
                        keyUsage = 0x2C;
                    }
                    break;
            }
        }

        if (keyUsage == 0) return;
        await SendKeyboardPressAsync(keyUsage, modifiers, cancellationToken, interfaceSelector);
    }

    /// <summary>
    /// Sends an empty keyboard report to release any pressed keys.
    /// </summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <param name="interfaceSelector">Overrides the configured keyboard interface selector.</param>
    public async Task SendKeyboardResetAsync(CancellationToken cancellationToken, byte? interfaceSelector = null)
    {
        var itf = await ResolveInterfaceAsync(interfaceSelector ?? _options.KeyboardInterfaceSelector, preferMouse: false, cancellationToken);
        var layout = await EnsureKeyboardLayoutAsync(itf, cancellationToken);
        await SendKeyboardResetAsync(itf, layout, cancellationToken);
    }

    /// <summary>
    /// Closes the serial port and releases transport resources.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await Task.Yield();
        try { _port.Close(); } catch { }
        try { _port.Dispose(); } catch { }
        _ioLock.Dispose();
    }

    private async Task<KeyboardLayoutInfo?> EnsureKeyboardLayoutAsync(byte itf, CancellationToken cancellationToken)
    {
        if (!_keyboardLayouts.TryGetValue(itf, out var layout))
        {
            layout = await TryRequestKeyboardLayoutAsync(itf, cancellationToken);
            _keyboardLayouts[itf] = layout;
        }
        return layout;
    }

    private async Task SendKeyboardReportAsync(byte itf, KeyboardLayoutInfo? layout, byte modifiers, byte usage, CancellationToken cancellationToken)
    {
        var report = HidReports.BuildKeyboardReport(layout, modifiers, usage);
        await SendInjectReportAsync(itf, report, cancellationToken);
    }

    private async Task SendKeyboardResetAsync(byte itf, KeyboardLayoutInfo? layout, CancellationToken cancellationToken)
    {
        var report = HidReports.BuildKeyboardReport(layout, 0, 0);
        await SendInjectReportAsync(itf, report, cancellationToken);
    }

    private async Task<byte> ResolveInterfaceAsync(byte selector, bool preferMouse, CancellationToken cancellationToken)
    {
        // 0xFF and 0xFE are logical selectors carried over from the legacy tooling:
        // they mean "resolve the best mouse" and "resolve the best keyboard" at runtime.
        if (selector is not 0xFF and not 0xFE)
        {
            return selector;
        }

        if (preferMouse && _resolvedMouseInterface.HasValue) return _resolvedMouseInterface.Value;
        if (!preferMouse && _resolvedKeyboardInterface.HasValue) return _resolvedKeyboardInterface.Value;

        var list = await RequestInterfaceListAsync(cancellationToken);
        var resolved = ResolveInterfaceSelectorFromList(list, selector, preferMouse);
        if (preferMouse) _resolvedMouseInterface = resolved;
        else _resolvedKeyboardInterface = resolved;
        return resolved;
    }

    private static byte ResolveInterfaceSelectorFromList(HidInterfaceList? list, byte selector, bool preferMouse)
    {
        if (selector is not 0xFF and not 0xFE) return selector;
        if (list is null) return selector;

        byte? firstActiveMounted = null;
        foreach (var item in list.Interfaces)
        {
            if (!item.Active || !item.Mounted) continue;
            firstActiveMounted ??= item.Itf;
            if (preferMouse && (item.InferredType & 0x02) != 0) return item.Itf;
            if (!preferMouse && (item.InferredType & 0x01) != 0) return item.Itf;
        }

        if (firstActiveMounted.HasValue)
        {
            return firstActiveMounted.Value;
        }

        return selector;
    }

    private async Task<byte[]?> RequestDeviceIdAsync(CancellationToken cancellationToken)
    {
        var response = await SendCommandAsync(0x06, Array.Empty<byte>(), _options.CommandTimeoutMs, cancellationToken);
        var payload = response?.Payload;
        if (payload is null || payload.Length < 2)
        {
            return null;
        }

        var declaredLength = payload[0];
        var availableLength = payload.Length - 1;
        var actualLength = Math.Min(declaredLength, availableLength);
        if (actualLength <= 0)
        {
            return null;
        }

        var deviceId = new byte[actualLength];
        Buffer.BlockCopy(payload, 1, deviceId, 0, actualLength);
        return deviceId;
    }

    private static byte[] ComputeDerivedKey(string masterSecret, byte[] deviceId)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(masterSecret));
        return hmac.ComputeHash(deviceId);
    }

    private async Task<HidInterfaceList?> RequestInterfaceListAsync(CancellationToken cancellationToken)
    {
        var response = await SendCommandAsync(0x02, Array.Empty<byte>(), _options.CommandTimeoutMs, cancellationToken);
        var payload = response is null ? null : response.Payload;
        if (payload is null || payload.Length < 1) return null;

        var count = payload[0];
        var offset = 1;
        var items = new List<HidInterfaceInfo>(count);
        for (var i = 0; i < count; i++)
        {
            if (offset + 7 > payload.Length) break;
            var devAddr = payload[offset + 0];
            var itf = payload[offset + 1];
            var itfProtocol = payload[offset + 2];
            var protocol = payload[offset + 3];
            var inferredType = payload[offset + 4];
            var active = payload[offset + 5] != 0;
            var mounted = payload[offset + 6] != 0;
            items.Add(new HidInterfaceInfo(devAddr, itf, itfProtocol, protocol, inferredType, active, mounted));
            offset += 7;
        }

        return new HidInterfaceList(DateTimeOffset.UtcNow, items);
    }

    private async Task<MouseLayoutInfo?> TryRequestMouseLayoutAsync(byte itfSel, CancellationToken cancellationToken)
    {
        foreach (byte reportId in new byte[] { 0, 1, 2, 3, 4, 5, 6, 7 })
        {
            try
            {
                var response = await SendCommandAsync(0x05, new byte[] { itfSel, reportId }, _options.CommandTimeoutMs, cancellationToken);
                var payload = response is null ? null : response.Payload;
                if (payload is null || payload.Length < 18) continue;
                if ((payload[2] & 0x01) == 0) continue;

                return new MouseLayoutInfo(
                    payload[1],
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
                    payload[3]);
            }
            catch (HidBridgeUartDeviceException ex) when (ex.DeviceErrorCode == 0x04)
            {
                continue;
            }
        }

        return null;
    }

    private async Task<KeyboardLayoutInfo?> TryRequestKeyboardLayoutAsync(byte itfSel, CancellationToken cancellationToken)
    {
        foreach (byte reportId in new byte[] { 0, 1, 2, 3, 4, 5, 6, 7 })
        {
            try
            {
                var response = await SendCommandAsync(0x05, new byte[] { itfSel, reportId }, _options.CommandTimeoutMs, cancellationToken);
                var payload = response is null ? null : response.Payload;
                if (payload is null || payload.Length < 18) continue;
                if ((payload[2] & 0x02) == 0) continue;

                return new KeyboardLayoutInfo(
                    payload[1],
                    payload[16] == 0 ? (byte)8 : payload[16],
                    payload[17] != 0);
            }
            catch (HidBridgeUartDeviceException ex) when (ex.DeviceErrorCode == 0x04)
            {
                continue;
            }
        }

        return null;
    }

    private async Task SendInjectReportAsync(byte itfSel, byte[] report, CancellationToken cancellationToken)
    {
        if (itfSel is 0xFF or 0xFE)
        {
            throw new InvalidOperationException(
                $"UART interface selector could not be resolved (itf={itfSel}). " +
                "Set HIDBRIDGE_UART_MOUSE_SELECTOR/HIDBRIDGE_UART_KEYBOARD_SELECTOR to a concrete interface id.");
        }

        if (report.Length > 240)
        {
            throw new InvalidOperationException("HID report is too large.");
        }

        var payload = new byte[2 + report.Length];
        payload[0] = itfSel;
        payload[1] = (byte)report.Length;
        Buffer.BlockCopy(report, 0, payload, 2, report.Length);

        var response = await SendCommandAsync(0x01, payload, _options.InjectTimeoutMs, cancellationToken, _options.InjectRetries);
        if (response is null)
        {
            throw new TimeoutException(
                $"No UART ACK for inject report on {_options.PortName} " +
                $"(baud={_options.BaudRate}, itf={itfSel}, timeoutMs={_options.InjectTimeoutMs}, retries={_options.InjectRetries}).");
        }
    }

    private async Task<UartResponse?> SendCommandAsync(
        byte cmd,
        byte[] payload,
        int timeoutMs,
        CancellationToken cancellationToken,
        int retries = 0,
        bool forceBootstrapKey = false,
        bool allowBootstrapFallback = true)
    {
        for (var attempt = 0; attempt <= retries; attempt++)
        {
            var seq = unchecked((byte)Interlocked.Increment(ref _seq));
            var requestKey = SelectRequestHmacKey(cmd, forceBootstrapKey);
            var alternateResponseKey = SelectAlternateResponseHmacKey(requestKey);
            var frame = BuildFrame(seq, cmd, payload, requestKey);
            var slip = Slip.Encode(frame);

            await _ioLock.WaitAsync(cancellationToken);
            try
            {
                await _port.BaseStream.WriteAsync(slip.AsMemory(0, slip.Length), cancellationToken);
                await _port.BaseStream.FlushAsync(cancellationToken);
                var response = ReadMatchingResponse(seq, cmd, timeoutMs, cancellationToken, requestKey, alternateResponseKey);
                if (response is not null)
                {
                    if (response.UsedAlternateHmacKey && ReferenceEquals(alternateResponseKey, _bootstrapHmacKey))
                    {
                        UseBootstrapHmacKey();
                    }

                    return response;
                }
            }
            finally
            {
                _ioLock.Release();
            }
        }

        if (!forceBootstrapKey
            && allowBootstrapFallback
            && _derivedHmacKey is not null
            && !_forceBootstrapKey
            && cmd != 0x06)
        {
            var fallback = await SendCommandAsync(
                cmd,
                payload,
                timeoutMs,
                cancellationToken,
                retries: 0,
                forceBootstrapKey: true,
                allowBootstrapFallback: false);

            if (fallback is not null)
            {
                UseBootstrapHmacKey();
            }

            return fallback;
        }

        return null;
    }

    private UartResponse? ReadMatchingResponse(
        byte seq,
        byte cmd,
        int timeoutMs,
        CancellationToken cancellationToken,
        byte[] expectedHmacKey,
        byte[]? alternateHmacKey)
    {
        var start = Environment.TickCount64;
        while ((Environment.TickCount64 - start) < timeoutMs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frame = TryReadSlipFrame();
            if (frame is null) continue;
            if (!TryParseFrame(frame, expectedHmacKey, alternateHmacKey, out var response, out var usedAlternateHmacKey))
            {
                continue;
            }

            var isResponse = (response.Flags & FlagResponse) != 0 || (response.Flags & FlagResponseLegacy) != 0;
            if (!isResponse) continue;
            if (response.Seq != seq || response.Cmd != cmd) continue;

            var isError = (response.Flags & FlagError) != 0 || (response.Flags & FlagErrorLegacy) != 0;
            if (isError)
            {
                var errorCode = response.Payload.Length > 0 ? response.Payload[0] : (byte?)null;
                throw new HidBridgeUartDeviceException(errorCode);
            }

            response.UsedAlternateHmacKey = usedAlternateHmacKey;
            return response;
        }

        return null;
    }

    private byte[]? TryReadSlipFrame()
    {
        const byte end = 0xC0;
        const byte esc = 0xDB;
        const byte escEnd = 0xDC;
        const byte escEsc = 0xDD;

        while (true)
        {
            int raw;
            try
            {
                raw = _port.ReadByte();
            }
            catch (TimeoutException)
            {
                return null;
            }

            var value = (byte)raw;
            if (value == end)
            {
                if (_rxFrame.Count == 0)
                {
                    _rxEscaped = false;
                    continue;
                }

                var frame = _rxFrame.ToArray();
                _rxFrame.Clear();
                _rxEscaped = false;
                return frame;
            }

            if (_rxEscaped)
            {
                _rxEscaped = false;
                if (value == escEnd) _rxFrame.Add(end);
                else if (value == escEsc) _rxFrame.Add(esc);
                else
                {
                    _rxFrame.Clear();
                    return null;
                }
                continue;
            }

            if (value == esc)
            {
                _rxEscaped = true;
                continue;
            }

            _rxFrame.Add(value);
            if (_rxFrame.Count > 2048)
            {
                _rxFrame.Clear();
                _rxEscaped = false;
                return null;
            }
        }
    }

    private bool TryParseFrame(
        ReadOnlySpan<byte> frame,
        byte[] expectedHmacKey,
        byte[]? alternateHmacKey,
        out UartResponse response,
        out bool usedAlternateHmacKey)
    {
        response = null!;
        usedAlternateHmacKey = false;
        if (frame.Length < HeaderLen + CrcLen + HmacLen) return false;
        if (frame[0] != Magic || frame[1] != Version) return false;

        var payloadLen = frame[5];
        if (frame.Length != HeaderLen + payloadLen + CrcLen + HmacLen) return false;

        var crc = Crc16Ccitt(frame[..(HeaderLen + payloadLen)]);
        var actualCrc = (ushort)(frame[6 + payloadLen] | (frame[7 + payloadLen] << 8));
        if (crc != actualCrc) return false;

        if (!TryValidateHmac(frame, payloadLen, expectedHmacKey))
        {
            if (alternateHmacKey is null || !TryValidateHmac(frame, payloadLen, alternateHmacKey))
            {
                return false;
            }

            usedAlternateHmacKey = true;
        }

        response = new UartResponse(frame[3], frame[4], frame[2], frame.Slice(6, payloadLen).ToArray());
        return true;
    }

    private byte[] BuildFrame(byte seq, byte cmd, ReadOnlySpan<byte> payload, byte[] hmacKey)
    {
        var payloadLen = payload.Length;
        var frame = new byte[HeaderLen + payloadLen + CrcLen + HmacLen];
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

        var crc = Crc16Ccitt(frame.AsSpan(0, HeaderLen + payloadLen));
        frame[6 + payloadLen] = (byte)(crc & 0xFF);
        frame[7 + payloadLen] = (byte)(crc >> 8);

        using var hmac = new HMACSHA256(hmacKey);
        var hash = hmac.ComputeHash(frame.AsSpan(0, HeaderLen + payloadLen + CrcLen).ToArray());
        hash.AsSpan(0, HmacLen).CopyTo(frame.AsSpan(8 + payloadLen));
        return frame;
    }

    private byte[] SelectRequestHmacKey(byte cmd, bool forceBootstrapKey)
    {
        if (forceBootstrapKey || cmd == 0x06 || _forceBootstrapKey || _derivedHmacKey is null)
        {
            return _bootstrapHmacKey;
        }

        _usingDerivedKey = true;
        return _derivedHmacKey;
    }

    private byte[]? SelectAlternateResponseHmacKey(byte[] requestKey)
    {
        if (ReferenceEquals(requestKey, _bootstrapHmacKey))
        {
            return _derivedHmacKey;
        }

        return _bootstrapHmacKey;
    }

    private static bool TryValidateHmac(ReadOnlySpan<byte> frame, int payloadLen, byte[] key)
    {
        using var hmac = new HMACSHA256(key);
        var hash = hmac.ComputeHash(frame[..(HeaderLen + payloadLen + CrcLen)].ToArray());
        return hash.AsSpan(0, HmacLen).SequenceEqual(frame.Slice(8 + payloadLen, HmacLen));
    }

    private static ushort Crc16Ccitt(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (var value in data)
        {
            crc ^= (ushort)(value << 8);
            for (var i = 0; i < 8; i++)
            {
                crc = (ushort)((crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : (crc << 1));
            }
        }
        return crc;
    }

    /// <summary>
    /// Represents one parsed UART response frame.
    /// </summary>
    private sealed class UartResponse
    {
        /// <summary>
        /// Creates a parsed UART response frame.
        /// </summary>
        /// <param name="seq">The protocol sequence number.</param>
        /// <param name="cmd">The echoed command identifier.</param>
        /// <param name="flags">The raw protocol flags.</param>
        /// <param name="payload">The decoded payload bytes.</param>
        public UartResponse(byte seq, byte cmd, byte flags, byte[] payload)
        {
            Seq = seq;
            Cmd = cmd;
            Flags = flags;
            Payload = payload;
        }

        /// <summary>
        /// Gets the protocol sequence number.
        /// </summary>
        public byte Seq { get; }

        /// <summary>
        /// Gets the echoed command identifier.
        /// </summary>
        public byte Cmd { get; }

        /// <summary>
        /// Gets the raw protocol flags.
        /// </summary>
        public byte Flags { get; }

        /// <summary>
        /// Gets the decoded payload bytes.
        /// </summary>
        public byte[] Payload { get; }

        /// <summary>
        /// Gets or sets whether the frame was validated with the alternate HMAC key.
        /// </summary>
        public bool UsedAlternateHmacKey { get; set; }
    }
}

/// <summary>
/// Represents a firmware-level UART device failure surfaced to the connector layer.
/// </summary>
public sealed class HidBridgeUartDeviceException : Exception
{
    /// <summary>
    /// Creates a new device exception from a raw firmware status byte.
    /// </summary>
    /// <param name="deviceErrorCode">The raw device status byte, when available.</param>
    public HidBridgeUartDeviceException(byte? deviceErrorCode)
        : base(deviceErrorCode.HasValue ? $"UART device returned error 0x{deviceErrorCode.Value:X2}" : "UART device returned error")
    {
        DeviceErrorCode = deviceErrorCode;
    }

    /// <summary>
    /// Gets the raw firmware error code, when one was returned by the bridge.
    /// </summary>
    public byte? DeviceErrorCode { get; }

    /// <summary>
    /// Converts the device exception into a contract-level error payload.
    /// </summary>
    /// <returns>A normalized error descriptor for API and orchestration layers.</returns>
    public ErrorInfo ToErrorInfo()
    {
        if (DeviceErrorCode.HasValue)
        {
            return UartErrorCatalog.FromFirmwareCode(DeviceErrorCode.Value);
        }

        return new ErrorInfo(ErrorDomain.Uart, "E_UART_DEVICE_ERROR", Message, false);
    }
}
