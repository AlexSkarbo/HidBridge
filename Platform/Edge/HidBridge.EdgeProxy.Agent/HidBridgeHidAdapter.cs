using HidBridge.Edge.Abstractions;
using HidBridge.Contracts;

namespace HidBridge.EdgeProxy.Agent;

/// <summary>
/// Defines a device-control adapter boundary used by edge proxy worker orchestration.
/// </summary>
public interface IHidBridgeHidAdapter
{
    /// <summary>
    /// Executes one typed transport command through the underlying device-control path.
    /// </summary>
    Task<EdgeCommandExecutionResult> ExecuteAsync(TransportCommandMessageBody command, CancellationToken cancellationToken);
}

/// <summary>
/// Adapts typed transport command messages to <see cref="IEdgeCommandExecutor"/> requests.
/// </summary>
public sealed class HidBridgeHidAdapter : IHidBridgeHidAdapter
{
    private readonly IEdgeCommandExecutor _commandExecutor;

    /// <summary>
    /// Initializes a new adapter instance.
    /// </summary>
    public HidBridgeHidAdapter(IEdgeCommandExecutor commandExecutor)
    {
        _commandExecutor = commandExecutor;
    }

    /// <summary>
    /// Executes one command by projecting typed args into normalized executor arguments.
    /// </summary>
    public Task<EdgeCommandExecutionResult> ExecuteAsync(TransportCommandMessageBody command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        return _commandExecutor.ExecuteAsync(
            new EdgeCommandRequest(
                CommandId: command.CommandId,
                Action: command.Action,
                Args: BuildExecutorArgs(command.Args),
                TimeoutMs: command.TimeoutMs),
            cancellationToken);
    }

    private static IReadOnlyDictionary<string, object?> BuildExecutorArgs(TransportHidCommandArgsBody args)
    {
        var map = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        void Put(string key, object? value)
        {
            if (value is not null)
            {
                map[key] = value;
            }
        }

        Put("text", args.Text);
        Put("shortcut", args.Shortcut);
        Put("usage", args.Usage);
        Put("modifiers", args.Modifiers);
        Put("dx", args.Dx);
        Put("dy", args.Dy);
        Put("wheel", args.Wheel);
        Put("delta", args.Delta);
        Put("button", args.Button);
        Put("down", args.Down);
        Put("holdMs", args.HoldMs);
        Put("itfSel", args.InterfaceSelector);
        return map;
    }
}
