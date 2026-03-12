using System.Collections.Concurrent;
using HidBridge.Abstractions;
using HidBridge.Contracts;

namespace HidBridge.ConnectorHost;

/// <summary>
/// Keeps live connector instances in memory while delegating snapshots and event streams to persistence stores.
/// </summary>
public sealed class InMemoryConnectorRegistry : IConnectorRegistry, IEndpointInventory, IEventWriter
{
    private readonly ConcurrentDictionary<string, IConnector> _connectors = new(StringComparer.OrdinalIgnoreCase);
    private readonly IConnectorCatalogStore _connectorCatalogStore;
    private readonly IEndpointSnapshotStore _endpointSnapshotStore;
    private readonly IEventStore _eventStore;

    /// <summary>
    /// Creates the registry with transient in-memory persistence intended for unit tests.
    /// </summary>
    public InMemoryConnectorRegistry()
        : this(new TransientConnectorCatalogStore(), new TransientEndpointSnapshotStore(), new TransientEventStore())
    {
    }

    /// <summary>
    /// Creates the registry with explicitly supplied persistence stores.
    /// </summary>
    /// <param name="connectorCatalogStore">Persists connector descriptor snapshots.</param>
    /// <param name="endpointSnapshotStore">Persists endpoint capability snapshots.</param>
    /// <param name="eventStore">Persists audit and telemetry streams.</param>
    public InMemoryConnectorRegistry(
        IConnectorCatalogStore connectorCatalogStore,
        IEndpointSnapshotStore endpointSnapshotStore,
        IEventStore eventStore)
    {
        _connectorCatalogStore = connectorCatalogStore;
        _endpointSnapshotStore = endpointSnapshotStore;
        _eventStore = eventStore;
    }

    /// <summary>
    /// Registers or replaces a connector instance.
    /// </summary>
    public async Task RegisterAsync(IConnector connector, CancellationToken cancellationToken)
    {
        _connectors[connector.Descriptor.AgentId] = connector;
        await _connectorCatalogStore.UpsertAsync(connector.Descriptor, cancellationToken);
    }

    /// <summary>
    /// Lists all registered connector descriptors.
    /// </summary>
    public Task<IReadOnlyList<ConnectorDescriptor>> ListAsync(CancellationToken cancellationToken)
        => _connectorCatalogStore.ListAsync(cancellationToken);

    /// <summary>
    /// Resolves a connector instance by agent identifier.
    /// </summary>
    public Task<IConnector?> ResolveAsync(string agentId, CancellationToken cancellationToken)
    {
        _connectors.TryGetValue(agentId, out var connector);
        return Task.FromResult(connector);
    }

    /// <summary>
    /// Creates or updates an endpoint capability snapshot.
    /// </summary>
    public Task UpsertEndpointAsync(string endpointId, IReadOnlyList<CapabilityDescriptor> capabilities, CancellationToken cancellationToken)
    {
        return _endpointSnapshotStore.UpsertAsync(
            new EndpointSnapshot(endpointId, capabilities, DateTimeOffset.UtcNow),
            cancellationToken);
    }

    /// <summary>
    /// Lists the endpoint identifiers tracked in memory.
    /// </summary>
    public async Task<IReadOnlyList<string>> ListEndpointsAsync(CancellationToken cancellationToken)
    {
        var items = await _endpointSnapshotStore.ListAsync(cancellationToken);
        return items
            .Select(x => x.EndpointId)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Appends one audit event to the configured persistence store.
    /// </summary>
    public Task WriteAuditAsync(AuditEventBody auditEvent, CancellationToken cancellationToken)
        => _eventStore.AppendAuditAsync(auditEvent, cancellationToken);

    /// <summary>
    /// Appends one telemetry event to the configured persistence store.
    /// </summary>
    public Task WriteTelemetryAsync(TelemetryEventBody telemetryEvent, CancellationToken cancellationToken)
        => _eventStore.AppendTelemetryAsync(telemetryEvent, cancellationToken);

    /// <summary>
    /// Returns a copy of the current audit event stream.
    /// </summary>
    public Task<IReadOnlyList<AuditEventBody>> AuditSnapshotAsync(CancellationToken cancellationToken)
        => _eventStore.ListAuditAsync(cancellationToken);

    /// <summary>
    /// Returns a copy of the current telemetry event stream.
    /// </summary>
    public Task<IReadOnlyList<TelemetryEventBody>> TelemetrySnapshotAsync(CancellationToken cancellationToken)
        => _eventStore.ListTelemetryAsync(cancellationToken);

    private sealed class TransientConnectorCatalogStore : IConnectorCatalogStore
    {
        private readonly ConcurrentDictionary<string, ConnectorDescriptor> _items = new(StringComparer.OrdinalIgnoreCase);

        public Task UpsertAsync(ConnectorDescriptor descriptor, CancellationToken cancellationToken)
        {
            _items[descriptor.AgentId] = descriptor;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ConnectorDescriptor>> ListAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<ConnectorDescriptor>>(_items.Values
                .OrderBy(x => x.AgentId, StringComparer.OrdinalIgnoreCase)
                .ToArray());
        }
    }

    private sealed class TransientEndpointSnapshotStore : IEndpointSnapshotStore
    {
        private readonly ConcurrentDictionary<string, EndpointSnapshot> _items = new(StringComparer.OrdinalIgnoreCase);

        public Task UpsertAsync(EndpointSnapshot snapshot, CancellationToken cancellationToken)
        {
            _items[snapshot.EndpointId] = snapshot;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<EndpointSnapshot>> ListAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<EndpointSnapshot>>(_items.Values
                .OrderBy(x => x.EndpointId, StringComparer.OrdinalIgnoreCase)
                .ToArray());
        }
    }

    private sealed class TransientEventStore : IEventStore
    {
        private readonly ConcurrentQueue<AuditEventBody> _audit = new();
        private readonly ConcurrentQueue<TelemetryEventBody> _telemetry = new();

        public Task AppendAuditAsync(AuditEventBody auditEvent, CancellationToken cancellationToken)
        {
            _audit.Enqueue(auditEvent.CreatedAtUtc.HasValue
                ? auditEvent
                : auditEvent with { CreatedAtUtc = DateTimeOffset.UtcNow });
            return Task.CompletedTask;
        }

        public Task AppendTelemetryAsync(TelemetryEventBody telemetryEvent, CancellationToken cancellationToken)
        {
            _telemetry.Enqueue(telemetryEvent.CreatedAtUtc.HasValue
                ? telemetryEvent
                : telemetryEvent with { CreatedAtUtc = DateTimeOffset.UtcNow });
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AuditEventBody>> ListAuditAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<AuditEventBody>>(_audit.ToArray());

        public Task<IReadOnlyList<TelemetryEventBody>> ListTelemetryAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<TelemetryEventBody>>(_telemetry.ToArray());

        public Task<int> TrimAuditAsync(DateTimeOffset olderThanUtc, CancellationToken cancellationToken)
        {
            var retained = _audit.Where(x => (x.CreatedAtUtc ?? DateTimeOffset.UtcNow) >= olderThanUtc).ToArray();
            var deleted = _audit.Count - retained.Length;
            while (_audit.TryDequeue(out _))
            {
            }

            foreach (var item in retained)
            {
                _audit.Enqueue(item);
            }

            return Task.FromResult(deleted);
        }

        public Task<int> TrimTelemetryAsync(DateTimeOffset olderThanUtc, CancellationToken cancellationToken)
        {
            var retained = _telemetry.Where(x => (x.CreatedAtUtc ?? DateTimeOffset.UtcNow) >= olderThanUtc).ToArray();
            var deleted = _telemetry.Count - retained.Length;
            while (_telemetry.TryDequeue(out _))
            {
            }

            foreach (var item in retained)
            {
                _telemetry.Enqueue(item);
            }

            return Task.FromResult(deleted);
        }
    }
}
