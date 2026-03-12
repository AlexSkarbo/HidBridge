namespace HidBridge.Persistence.Sql.Entities;

internal sealed class AgentEntity
{
    public string AgentId { get; set; } = string.Empty;
    public string EndpointId { get; set; } = string.Empty;
    public string ConnectorType { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public List<AgentCapabilityEntity> Capabilities { get; set; } = new();
}

internal sealed class AgentCapabilityEntity
{
    public long Id { get; set; }
    public string AgentId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string? MetadataJson { get; set; }
    public AgentEntity Agent { get; set; } = null!;
}
