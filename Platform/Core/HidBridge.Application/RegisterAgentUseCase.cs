using HidBridge.Abstractions;
using HidBridge.Contracts;

namespace HidBridge.Application;

/// <summary>
/// Registers a connector in the control plane and updates endpoint inventory.
/// </summary>
public sealed class RegisterAgentUseCase
{
    private readonly IConnectorRegistry _connectorRegistry;
    private readonly IEndpointInventory _inventory;
    private readonly IEventWriter _eventWriter;

    /// <summary>
    /// Creates the agent registration use case.
    /// </summary>
    public RegisterAgentUseCase(IConnectorRegistry connectorRegistry, IEndpointInventory inventory, IEventWriter eventWriter)
    {
        _connectorRegistry = connectorRegistry;
        _inventory = inventory;
        _eventWriter = eventWriter;
    }

    /// <summary>
    /// Registers the connector, upserts endpoint capabilities, and emits an audit event.
    /// </summary>
    /// <param name="connector">The connector to register.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The registration payload returned by the connector.</returns>
    public async Task<AgentRegisterBody> ExecuteAsync(IConnector connector, CancellationToken cancellationToken)
    {
        var registration = await connector.RegisterAsync(cancellationToken);
        await _connectorRegistry.RegisterAsync(connector, cancellationToken);
        await _inventory.UpsertEndpointAsync(connector.Descriptor.EndpointId, registration.Capabilities, cancellationToken);
        await _eventWriter.WriteAuditAsync(
            new AuditEventBody("system", $"Agent {connector.Descriptor.AgentId} registered", Data: new Dictionary<string, object?>
            {
                ["endpointId"] = connector.Descriptor.EndpointId,
                ["connectorType"] = connector.Descriptor.ConnectorType.ToString(),
            }),
            cancellationToken);
        return registration;
    }
}
