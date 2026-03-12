using HidBridge.Abstractions;
using HidBridge.Application;
using HidBridge.ConnectorHost;
using HidBridge.Contracts;
using Xunit;

namespace HidBridge.Platform.Tests;

/// <summary>
/// Verifies agent registration flow against the in-memory baseline services.
/// </summary>
public sealed class RegisterAgentUseCaseTests
{
    private sealed class FakeConnector : IConnector
    {
        public FakeConnector(string agentId, string endpointId)
        {
            Descriptor = new ConnectorDescriptor(
                agentId,
                endpointId,
                ConnectorType.Agentless,
                new[]
                {
                    new CapabilityDescriptor(CapabilityNames.HidMouseV1, "1.0"),
                    new CapabilityDescriptor(CapabilityNames.DiagnosticsTelemetryV1, "1.0"),
                });
        }

        public ConnectorDescriptor Descriptor { get; }

        public Task<AgentRegisterBody> RegisterAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(new AgentRegisterBody(
                Descriptor.ConnectorType,
                "test-agent",
                Descriptor.Capabilities,
                new Dictionary<string, object?> { ["ready"] = true }));
        }

        public Task<AgentHeartbeatBody> HeartbeatAsync(CancellationToken cancellationToken)
            => Task.FromResult(new AgentHeartbeatBody(AgentStatus.Online));

        public Task<ConnectorCommandResult> ExecuteAsync(CommandRequestBody command, CancellationToken cancellationToken)
            => Task.FromResult(new ConnectorCommandResult(command.CommandId, CommandStatus.Applied));
    }

    [Fact]
    /// <summary>
    /// Verifies that registration updates connector inventory and emits an audit event.
    /// </summary>
    public async Task ExecuteAsync_RegistersConnectorUpdatesInventoryAndWritesAudit()
    {
        var registry = new InMemoryConnectorRegistry();
        var useCase = new RegisterAgentUseCase(registry, registry, registry);
        var connector = new FakeConnector("agent-test", "endpoint-test");

        var registration = await useCase.ExecuteAsync(connector, TestContext.Current.CancellationToken);

        Assert.Equal("test-agent", registration.AgentVersion);
        var agents = await registry.ListAsync(TestContext.Current.CancellationToken);
        Assert.Contains(agents, x => x.AgentId == "agent-test");
        var endpoints = await registry.ListEndpointsAsync(TestContext.Current.CancellationToken);
        Assert.Contains("endpoint-test", endpoints);
        var audit = await registry.AuditSnapshotAsync(TestContext.Current.CancellationToken);
        Assert.Contains(audit, x => x.Message.Contains("agent-test", StringComparison.OrdinalIgnoreCase));
    }
}
