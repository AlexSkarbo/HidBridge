using HidBridge.Abstractions;
using HidBridge.Application;

namespace HidBridge.ControlPlane.Api;

/// <summary>
/// Registers configured connectors during API startup so inventory is immediately available.
/// </summary>
public sealed class ConnectorBootstrapHostedService : IHostedService
{
    private readonly IEnumerable<IConnector> _connectors;
    private readonly RegisterAgentUseCase _registerAgentUseCase;
    private readonly ILogger<ConnectorBootstrapHostedService> _logger;

    /// <summary>
    /// Creates the startup bootstrap service.
    /// </summary>
    public ConnectorBootstrapHostedService(
        IEnumerable<IConnector> connectors,
        RegisterAgentUseCase registerAgentUseCase,
        ILogger<ConnectorBootstrapHostedService> logger)
    {
        _connectors = connectors;
        _registerAgentUseCase = registerAgentUseCase;
        _logger = logger;
    }

    /// <summary>
    /// Registers each configured connector when the host starts.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var connector in _connectors)
        {
            // ControlPlane owns initial connector registration so the API can expose
            // inventory and accept sessions immediately after startup.
            var registration = await _registerAgentUseCase.ExecuteAsync(connector, cancellationToken);
            _logger.LogInformation(
                "Registered connector {AgentId} -> {EndpointId} ({ConnectorType}, {CapabilityCount} capabilities)",
                connector.Descriptor.AgentId,
                connector.Descriptor.EndpointId,
                registration.ConnectorType,
                registration.Capabilities.Count);
        }
    }

    /// <summary>
    /// Stops the hosted service. No shutdown work is required yet.
    /// </summary>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
