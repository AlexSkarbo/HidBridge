using HidBridge.Abstractions;
using HidBridge.Contracts;
using HidBridge.Transport.Uart;
using System.Text.Json;

namespace HidBridge.Connectors.HidBridgeUart;

/// <summary>
/// Exposes mouse and keyboard control through the HidBridge UART transport.
/// </summary>
public sealed class HidBridgeUartConnector : IConnector
{
    private readonly HidBridgeUartClientOptions _options;
    private readonly object _clientSync = new();
    private Lazy<HidBridgeUartClient> _client;
    private readonly bool _passiveHealthMode;
    private readonly bool _releasePortAfterExecute;
    private readonly SemaphoreSlim _keyInitLock = new(1, 1);
    private bool _keyInitAttempted;
    private DateTimeOffset _nextKeyInitAttemptUtc = DateTimeOffset.MinValue;

    /// <summary>
    /// Creates a UART-backed HID connector for one logical endpoint.
    /// </summary>
    /// <param name="agentId">The connector agent identifier.</param>
    /// <param name="endpointId">The endpoint identifier exposed to the control plane.</param>
    /// <param name="options">The UART transport configuration.</param>
    /// <param name="extraCapabilities">Optional extra capability descriptors to advertise for routing tests.</param>
    /// <param name="passiveHealthMode">
    /// When enabled, registration and heartbeat avoid opening the serial port so WebRTC peer adapters can own UART access.
    /// </param>
    /// <param name="releasePortAfterExecute">
    /// When enabled, each command execution closes UART immediately after completion to avoid long-lived COM locks.
    /// </param>
    public HidBridgeUartConnector(
        string agentId,
        string endpointId,
        HidBridgeUartClientOptions options,
        IReadOnlyList<CapabilityDescriptor>? extraCapabilities = null,
        bool passiveHealthMode = false,
        bool releasePortAfterExecute = false)
    {
        _options = options;
        _passiveHealthMode = passiveHealthMode;
        _releasePortAfterExecute = releasePortAfterExecute;
        _client = CreateClientLazy();
        var capabilities = new List<CapabilityDescriptor>
        {
            new(CapabilityNames.HidMouseV1, "1.0"),
            new(CapabilityNames.HidKeyboardV1, "1.0"),
            new(CapabilityNames.DiagnosticsTelemetryV1, "1.0"),
        };

        if (extraCapabilities is not null)
        {
            foreach (var capability in extraCapabilities)
            {
                if (string.IsNullOrWhiteSpace(capability.Name))
                {
                    continue;
                }

                if (capabilities.Any(existing => string.Equals(existing.Name, capability.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                capabilities.Add(capability);
            }
        }

        Descriptor = new ConnectorDescriptor(
            agentId,
            endpointId,
            ConnectorType.HidBridge,
            capabilities);
        UartPort = options.PortName;
        BaudRate = options.BaudRate;
    }

    /// <summary>
    /// Gets the immutable connector descriptor exposed to the control plane.
    /// </summary>
    public ConnectorDescriptor Descriptor { get; }

    /// <summary>
    /// Gets the configured UART port name used by the connector.
    /// </summary>
    public string UartPort { get; }

    /// <summary>
    /// Gets the configured UART baud rate used by the connector.
    /// </summary>
    public int BaudRate { get; }

    /// <summary>
    /// Registers the connector and returns its initial health metadata.
    /// </summary>
    public async Task<AgentRegisterBody> RegisterAsync(CancellationToken cancellationToken)
    {
        HidBridgeUartHealth healthSnapshot;
        if (_passiveHealthMode)
        {
            healthSnapshot = BuildPassiveHealthSnapshot();
        }
        else
        {
            await EnsureDerivedKeyIfConfiguredAsync(cancellationToken);
            healthSnapshot = await Client.GetHealthAsync(cancellationToken);
        }

        var health = new Dictionary<string, object?>
        {
            ["transport"] = "uart-v2",
            ["port"] = UartPort,
            ["baudRate"] = BaudRate,
            ["mouseSelector"] = healthSnapshot.MouseInterfaceSelector,
            ["keyboardSelector"] = healthSnapshot.KeyboardInterfaceSelector,
            ["resolvedMouseInterface"] = healthSnapshot.ResolvedMouseInterface,
            ["resolvedKeyboardInterface"] = healthSnapshot.ResolvedKeyboardInterface,
            ["interfaceCount"] = healthSnapshot.InterfaceCount,
            ["isConnected"] = healthSnapshot.IsConnected,
            ["usingDerivedKey"] = IsClientUsingDerivedKey,
            ["passiveHealthMode"] = _passiveHealthMode,
            ["releasePortAfterExecute"] = _releasePortAfterExecute,
        };

        return new AgentRegisterBody(
            ConnectorType.HidBridge,
            AgentVersion: "0.2.0-p0",
            Capabilities: Descriptor.Capabilities,
            Health: health);
    }

    /// <summary>
    /// Reads the current heartbeat snapshot from the underlying UART client.
    /// </summary>
    public async Task<AgentHeartbeatBody> HeartbeatAsync(CancellationToken cancellationToken)
    {
        HidBridgeUartHealth health;
        if (_passiveHealthMode)
        {
            health = BuildPassiveHealthSnapshot();
        }
        else
        {
            await EnsureDerivedKeyIfConfiguredAsync(cancellationToken);
            health = await Client.GetHealthAsync(cancellationToken);
        }

        return new AgentHeartbeatBody(
            health.IsConnected ? AgentStatus.Online : AgentStatus.Degraded,
            UptimeMs: Environment.TickCount64,
            Load: new Dictionary<string, object?>
            {
                ["interfaceCount"] = health.InterfaceCount,
                ["mouseInterface"] = health.ResolvedMouseInterface,
                ["keyboardInterface"] = health.ResolvedKeyboardInterface,
                ["passiveHealthMode"] = _passiveHealthMode,
                ["releasePortAfterExecute"] = _releasePortAfterExecute,
            });
    }

    /// <summary>
    /// Executes a HID command and normalizes UART transport failures into contract results.
    /// </summary>
    public async Task<ConnectorCommandResult> ExecuteAsync(CommandRequestBody command, CancellationToken cancellationToken)
    {
        var metrics = new Dictionary<string, double>
        {
            ["agentQueueMs"] = 0,
        };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        ConnectorCommandResult result;
        try
        {
            await ExecuteCoreAsync(command, cancellationToken);
            sw.Stop();
            metrics["deviceAckMs"] = sw.Elapsed.TotalMilliseconds;
            result = new ConnectorCommandResult(command.CommandId, CommandStatus.Applied, Metrics: metrics);
        }
        catch (HidBridgeUartDeviceException ex)
        {
            sw.Stop();
            metrics["deviceAckMs"] = sw.Elapsed.TotalMilliseconds;
            result = new ConnectorCommandResult(command.CommandId, CommandStatus.Rejected, ex.ToErrorInfo(), metrics);
        }
        catch (TimeoutException ex)
        {
            sw.Stop();
            metrics["deviceAckMs"] = sw.Elapsed.TotalMilliseconds;
            result = new ConnectorCommandResult(
                command.CommandId,
                CommandStatus.Timeout,
                new ErrorInfo(ErrorDomain.Transport, "E_UART_TIMEOUT", ex.Message, true),
                metrics);
        }
        catch (Exception ex)
        {
            sw.Stop();
            metrics["deviceAckMs"] = sw.Elapsed.TotalMilliseconds;
            result = new ConnectorCommandResult(
                command.CommandId,
                CommandStatus.Rejected,
                new ErrorInfo(ErrorDomain.Command, "E_COMMAND_EXECUTION_FAILED", ex.Message, false),
                metrics);
        }
        finally
        {
            if (_releasePortAfterExecute)
            {
                await ReleaseClientAfterExecuteAsync();
            }
        }

        return result;
    }

    private Lazy<HidBridgeUartClient> CreateClientLazy()
        => new(() => new HidBridgeUartClient(_options), LazyThreadSafetyMode.ExecutionAndPublication);

    private HidBridgeUartClient Client
    {
        get
        {
            lock (_clientSync)
            {
                return _client.Value;
            }
        }
    }

    private bool IsClientUsingDerivedKey
    {
        get
        {
            lock (_clientSync)
            {
                if (!_client.IsValueCreated)
                {
                    return false;
                }

                return _client.Value.IsUsingDerivedKey;
            }
        }
    }

    private async Task ReleaseClientAfterExecuteAsync()
    {
        HidBridgeUartClient? instanceToDispose = null;
        lock (_clientSync)
        {
            if (!_client.IsValueCreated)
            {
                return;
            }

            instanceToDispose = _client.Value;
            _client = CreateClientLazy();
            _keyInitAttempted = false;
            _nextKeyInitAttemptUtc = DateTimeOffset.MinValue;
        }

        if (instanceToDispose is not null)
        {
            await instanceToDispose.DisposeAsync();
        }
    }

    private HidBridgeUartHealth BuildPassiveHealthSnapshot()
        => new(
            _options.PortName,
            _options.BaudRate,
            _options.MouseInterfaceSelector,
            _options.KeyboardInterfaceSelector,
            ResolvedMouseInterface: null,
            ResolvedKeyboardInterface: null,
            InterfaceCount: 0,
            IsConnected: false,
            SampledAt: DateTimeOffset.UtcNow);

    private async Task ExecuteCoreAsync(CommandRequestBody command, CancellationToken cancellationToken)
    {
        await EnsureDerivedKeyIfConfiguredAsync(cancellationToken);
        var action = NormalizeAction(command.Action);
        switch (action)
        {
            case "mouse.move":
                await Client.SendMouseMoveAsync(
                    GetInt(command.Args, "dx"),
                    GetInt(command.Args, "dy"),
                    GetInt(command.Args, "wheel", 0),
                    cancellationToken,
                    GetByteOrNull(command.Args, "itfSel"));
                return;

            case "mouse.wheel":
                await Client.SendMouseMoveAsync(
                    0,
                    0,
                    GetInt(command.Args, "delta"),
                    cancellationToken,
                    GetByteOrNull(command.Args, "itfSel"));
                return;

            case "mouse.button":
                await Client.SetMouseButtonAsync(
                    GetString(command.Args, "button"),
                    GetBool(command.Args, "down"),
                    cancellationToken,
                    GetByteOrNull(command.Args, "itfSel"));
                return;

            case "mouse.buttons":
                await Client.SetMouseButtonsMaskAsync(
                    GetByte(command.Args, "mask"),
                    cancellationToken,
                    GetByteOrNull(command.Args, "itfSel"));
                return;

            case "keyboard.text":
                await Client.SendKeyboardTextAsync(
                    GetString(command.Args, "text"),
                    cancellationToken,
                    GetByteOrNull(command.Args, "itfSel"));
                return;

            case "keyboard.press":
                await Client.SendKeyboardPressAsync(
                    GetByte(command.Args, "usage"),
                    GetByte(command.Args, "modifiers", 0),
                    cancellationToken,
                    GetByteOrNull(command.Args, "itfSel"));
                return;

            case "keyboard.shortcut":
                await Client.SendKeyboardShortcutAsync(
                    GetString(command.Args, "shortcut"),
                    cancellationToken,
                    GetByteOrNull(command.Args, "itfSel"));
                return;

            case "keyboard.reset":
                await Client.SendKeyboardResetAsync(
                    cancellationToken,
                    GetByteOrNull(command.Args, "itfSel"));
                return;

            default:
                throw new InvalidOperationException($"Unsupported HID action '{command.Action}'.");
        }
    }

    private async Task EnsureDerivedKeyIfConfiguredAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.MasterSecret) || _keyInitAttempted)
        {
            return;
        }

        if (DateTimeOffset.UtcNow < _nextKeyInitAttemptUtc)
        {
            return;
        }

        await _keyInitLock.WaitAsync(cancellationToken);
        try
        {
            if (_keyInitAttempted)
            {
                return;
            }

            try
            {
                var applied = await Client.EnsureDerivedKeyAsync(_options.MasterSecret, cancellationToken);
                _keyInitAttempted = applied;
                if (!applied)
                {
                    _nextKeyInitAttemptUtc = DateTimeOffset.UtcNow.AddSeconds(30);
                }
            }
            catch
            {
                // Keep command path alive on key-derivation issues and retry later.
                _nextKeyInitAttemptUtc = DateTimeOffset.UtcNow.AddSeconds(30);
            }
        }
        finally
        {
            _keyInitLock.Release();
        }
    }

    private static string NormalizeAction(string action)
    {
        return action.Trim().Replace('_', '.').ToLowerInvariant();
    }

    private static string GetString(IReadOnlyDictionary<string, object?> args, string name)
    {
        if (!args.TryGetValue(name, out var value) || value is null)
        {
            throw new InvalidOperationException($"Missing args.{name}");
        }

        return value switch
        {
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } json => json.GetString() ?? string.Empty,
            _ => value.ToString() ?? string.Empty,
        };
    }

    private static bool GetBool(IReadOnlyDictionary<string, object?> args, string name, bool defaultValue = false)
    {
        if (!args.TryGetValue(name, out var value) || value is null)
        {
            return defaultValue;
        }

        return value switch
        {
            bool flag => flag,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement { ValueKind: JsonValueKind.False } => false,
            JsonElement json when json.ValueKind == JsonValueKind.Number && json.TryGetInt32(out var number) => number != 0,
            _ when bool.TryParse(value.ToString(), out var parsed) => parsed,
            _ => defaultValue,
        };
    }

    private static int GetInt(IReadOnlyDictionary<string, object?> args, string name, int defaultValue = 0)
    {
        if (!args.TryGetValue(name, out var value) || value is null)
        {
            return defaultValue;
        }

        return value switch
        {
            int number => number,
            long number => checked((int)number),
            byte number => number,
            JsonElement json when json.ValueKind == JsonValueKind.Number && json.TryGetInt32(out var number) => number,
            _ when int.TryParse(value.ToString(), out var parsed) => parsed,
            _ => defaultValue,
        };
    }

    private static byte GetByte(IReadOnlyDictionary<string, object?> args, string name, byte defaultValue = 0)
    {
        return (byte)Math.Clamp(GetInt(args, name, defaultValue), byte.MinValue, byte.MaxValue);
    }

    private static byte? GetByteOrNull(IReadOnlyDictionary<string, object?> args, string name)
    {
        if (!args.ContainsKey(name))
        {
            return null;
        }

        return GetByte(args, name);
    }
}
