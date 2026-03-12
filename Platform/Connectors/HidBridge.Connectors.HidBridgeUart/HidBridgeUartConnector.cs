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
    private readonly Lazy<HidBridgeUartClient> _client;

    /// <summary>
    /// Creates a UART-backed HID connector for one logical endpoint.
    /// </summary>
    /// <param name="agentId">The connector agent identifier.</param>
    /// <param name="endpointId">The endpoint identifier exposed to the control plane.</param>
    /// <param name="options">The UART transport configuration.</param>
    public HidBridgeUartConnector(string agentId, string endpointId, HidBridgeUartClientOptions options)
    {
        _options = options;
        _client = new Lazy<HidBridgeUartClient>(() => new HidBridgeUartClient(_options));
        Descriptor = new ConnectorDescriptor(
            agentId,
            endpointId,
            ConnectorType.HidBridge,
            new[]
            {
                new CapabilityDescriptor(CapabilityNames.HidMouseV1, "1.0"),
                new CapabilityDescriptor(CapabilityNames.HidKeyboardV1, "1.0"),
                new CapabilityDescriptor(CapabilityNames.DiagnosticsTelemetryV1, "1.0"),
            });
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
        var healthSnapshot = await Client.GetHealthAsync(cancellationToken);
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
        var health = await Client.GetHealthAsync(cancellationToken);
        return new AgentHeartbeatBody(
            health.IsConnected ? AgentStatus.Online : AgentStatus.Degraded,
            UptimeMs: Environment.TickCount64,
            Load: new Dictionary<string, object?>
            {
                ["interfaceCount"] = health.InterfaceCount,
                ["mouseInterface"] = health.ResolvedMouseInterface,
                ["keyboardInterface"] = health.ResolvedKeyboardInterface,
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
        try
        {
            await ExecuteCoreAsync(command, cancellationToken);
            sw.Stop();
            metrics["deviceAckMs"] = sw.Elapsed.TotalMilliseconds;
            return new ConnectorCommandResult(command.CommandId, CommandStatus.Applied, Metrics: metrics);
        }
        catch (HidBridgeUartDeviceException ex)
        {
            sw.Stop();
            metrics["deviceAckMs"] = sw.Elapsed.TotalMilliseconds;
            return new ConnectorCommandResult(command.CommandId, CommandStatus.Rejected, ex.ToErrorInfo(), metrics);
        }
        catch (TimeoutException ex)
        {
            sw.Stop();
            metrics["deviceAckMs"] = sw.Elapsed.TotalMilliseconds;
            return new ConnectorCommandResult(
                command.CommandId,
                CommandStatus.Timeout,
                new ErrorInfo(ErrorDomain.Transport, "E_UART_TIMEOUT", ex.Message, true),
                metrics);
        }
        catch (Exception ex)
        {
            sw.Stop();
            metrics["deviceAckMs"] = sw.Elapsed.TotalMilliseconds;
            return new ConnectorCommandResult(
                command.CommandId,
                CommandStatus.Rejected,
                new ErrorInfo(ErrorDomain.Command, "E_COMMAND_EXECUTION_FAILED", ex.Message, false),
                metrics);
        }
    }

    private HidBridgeUartClient Client => _client.Value;

    private async Task ExecuteCoreAsync(CommandRequestBody command, CancellationToken cancellationToken)
    {
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
