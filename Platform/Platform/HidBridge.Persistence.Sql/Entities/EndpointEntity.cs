namespace HidBridge.Persistence.Sql.Entities;

internal sealed class EndpointEntity
{
    public string EndpointId { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public List<EndpointCapabilityEntity> Capabilities { get; set; } = new();
}

internal sealed class EndpointCapabilityEntity
{
    public long Id { get; set; }
    public string EndpointId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string? MetadataJson { get; set; }
    public EndpointEntity Endpoint { get; set; } = null!;
}
