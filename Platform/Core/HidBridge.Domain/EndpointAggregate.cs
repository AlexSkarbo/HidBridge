using HidBridge.Contracts;

namespace HidBridge.Domain;

/// <summary>
/// Represents one endpoint and the capabilities currently known for it.
/// </summary>
public sealed class EndpointAggregate
{
    private readonly List<CapabilityDescriptor> _capabilities = new();

    /// <summary>
    /// Creates a new endpoint aggregate.
    /// </summary>
    /// <param name="endpointId">The stable endpoint identifier.</param>
    public EndpointAggregate(string endpointId)
    {
        EndpointId = endpointId;
    }

    public string EndpointId { get; }
    public IReadOnlyList<CapabilityDescriptor> Capabilities => _capabilities;

    /// <summary>
    /// Replaces the endpoint capability snapshot.
    /// </summary>
    /// <param name="capabilities">The capabilities currently advertised for the endpoint.</param>
    public void ReplaceCapabilities(IEnumerable<CapabilityDescriptor> capabilities)
    {
        _capabilities.Clear();
        _capabilities.AddRange(capabilities);
    }
}
