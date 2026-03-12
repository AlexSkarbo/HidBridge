using System.Text.Json;
using HidBridge.Abstractions;
using HidBridge.Contracts;
using HidBridge.Persistence.Sql.Entities;

namespace HidBridge.Persistence.Sql;

/// <summary>
/// Converts persistence entities to platform contracts and back.
/// </summary>
internal static class SqlStoreMapper
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Maps a connector descriptor into a persisted agent entity.
    /// </summary>
    public static AgentEntity ToEntity(ConnectorDescriptor descriptor)
    {
        return new AgentEntity
        {
            AgentId = descriptor.AgentId,
            EndpointId = descriptor.EndpointId,
            ConnectorType = descriptor.ConnectorType.ToString(),
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            Capabilities = descriptor.Capabilities.Select(x => new AgentCapabilityEntity
            {
                AgentId = descriptor.AgentId,
                Name = x.Name,
                Version = x.Version,
                MetadataJson = Serialize(x.Meta),
            }).ToList(),
        };
    }

    /// <summary>
    /// Maps a persisted agent entity into a connector descriptor.
    /// </summary>
    public static ConnectorDescriptor ToDescriptor(AgentEntity entity)
    {
        return new ConnectorDescriptor(
            entity.AgentId,
            entity.EndpointId,
            Enum.Parse<ConnectorType>(entity.ConnectorType, ignoreCase: true),
            entity.Capabilities
                .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .Select(x => new CapabilityDescriptor(x.Name, x.Version, DeserializeStringDictionary(x.MetadataJson)))
                .ToArray());
    }

    /// <summary>
    /// Maps an endpoint snapshot into a persisted endpoint entity.
    /// </summary>
    public static EndpointEntity ToEntity(EndpointSnapshot snapshot)
    {
        return new EndpointEntity
        {
            EndpointId = snapshot.EndpointId,
            UpdatedAtUtc = snapshot.UpdatedAtUtc,
            Capabilities = snapshot.Capabilities.Select(x => new EndpointCapabilityEntity
            {
                EndpointId = snapshot.EndpointId,
                Name = x.Name,
                Version = x.Version,
                MetadataJson = Serialize(x.Meta),
            }).ToList(),
        };
    }

    /// <summary>
    /// Maps a persisted endpoint entity into an endpoint snapshot.
    /// </summary>
    public static EndpointSnapshot ToSnapshot(EndpointEntity entity)
    {
        return new EndpointSnapshot(
            entity.EndpointId,
            entity.Capabilities
                .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .Select(x => new CapabilityDescriptor(x.Name, x.Version, DeserializeStringDictionary(x.MetadataJson)))
                .ToArray(),
            entity.UpdatedAtUtc);
    }

    /// <summary>
    /// Maps a session snapshot into a persisted session entity graph.
    /// </summary>
    public static SessionEntity ToEntity(SessionSnapshot snapshot)
    {
        return new SessionEntity
        {
            SessionId = snapshot.SessionId,
            AgentId = snapshot.AgentId,
            EndpointId = snapshot.EndpointId,
            Profile = snapshot.Profile.ToString(),
            RequestedBy = snapshot.RequestedBy,
            TenantId = snapshot.TenantId,
            OrganizationId = snapshot.OrganizationId,
            Role = snapshot.Role.ToString(),
            State = snapshot.State.ToString(),
            UpdatedAtUtc = snapshot.UpdatedAtUtc,
            ActiveControllerParticipantId = snapshot.ControlLease?.ParticipantId,
            ActiveControllerPrincipalId = snapshot.ControlLease?.PrincipalId,
            ControlGrantedBy = snapshot.ControlLease?.GrantedBy,
            ControlGrantedAtUtc = snapshot.ControlLease?.GrantedAtUtc,
            ControlLeaseExpiresAtUtc = snapshot.ControlLease?.ExpiresAtUtc,
            LastHeartbeatAtUtc = snapshot.LastHeartbeatAtUtc,
            LeaseExpiresAtUtc = snapshot.LeaseExpiresAtUtc,
            Participants = snapshot.Participants?.Select(x => new SessionParticipantEntity
            {
                SessionId = snapshot.SessionId,
                ParticipantId = x.ParticipantId,
                PrincipalId = x.PrincipalId,
                Role = x.Role.ToString(),
                JoinedAtUtc = x.JoinedAtUtc,
                UpdatedAtUtc = x.UpdatedAtUtc,
            }).ToList() ?? new List<SessionParticipantEntity>(),
            Shares = snapshot.Shares?.Select(x => new SessionShareEntity
            {
                SessionId = snapshot.SessionId,
                ShareId = x.ShareId,
                PrincipalId = x.PrincipalId,
                GrantedBy = x.GrantedBy,
                Role = x.Role.ToString(),
                Status = x.Status.ToString(),
                CreatedAtUtc = x.CreatedAtUtc,
                UpdatedAtUtc = x.UpdatedAtUtc,
            }).ToList() ?? new List<SessionShareEntity>(),
        };
    }

    /// <summary>
    /// Maps a persisted session entity graph into a session snapshot.
    /// </summary>
    public static SessionSnapshot ToSnapshot(SessionEntity entity)
    {
        return new SessionSnapshot(
            entity.SessionId,
            entity.AgentId,
            entity.EndpointId,
            Enum.Parse<SessionProfile>(entity.Profile, ignoreCase: true),
            entity.RequestedBy,
            Enum.Parse<SessionRole>(entity.Role, ignoreCase: true),
            Enum.Parse<SessionState>(entity.State, ignoreCase: true),
            entity.UpdatedAtUtc,
            entity.Participants
                .OrderBy(x => x.ParticipantId, StringComparer.OrdinalIgnoreCase)
                .Select(x => new SessionParticipantSnapshot(
                    x.ParticipantId,
                    x.PrincipalId,
                    Enum.Parse<SessionRole>(x.Role, ignoreCase: true),
                    x.JoinedAtUtc,
                    x.UpdatedAtUtc))
                .ToArray(),
            entity.Shares
                .OrderBy(x => x.ShareId, StringComparer.OrdinalIgnoreCase)
                .Select(x => new SessionShareSnapshot(
                    x.ShareId,
                    x.PrincipalId,
                    x.GrantedBy,
                    Enum.Parse<SessionRole>(x.Role, ignoreCase: true),
                    Enum.Parse<SessionShareStatus>(x.Status, ignoreCase: true),
                    x.CreatedAtUtc,
                    x.UpdatedAtUtc))
                .ToArray(),
            entity.ActiveControllerParticipantId is not null
                && entity.ActiveControllerPrincipalId is not null
                && entity.ControlGrantedBy is not null
                && entity.ControlGrantedAtUtc.HasValue
                && entity.ControlLeaseExpiresAtUtc.HasValue
                ? new SessionControlLeaseBody(
                    entity.ActiveControllerParticipantId,
                    entity.ActiveControllerPrincipalId,
                    entity.ControlGrantedBy,
                    entity.ControlGrantedAtUtc.Value,
                    entity.ControlLeaseExpiresAtUtc.Value)
                : null,
            entity.LastHeartbeatAtUtc,
            entity.LeaseExpiresAtUtc,
            entity.TenantId,
            entity.OrganizationId);
    }

    /// <summary>
    /// Maps an audit event body into a persisted audit entity.
    /// </summary>
    public static AuditEventEntity ToEntity(AuditEventBody body)
    {
        return new AuditEventEntity
        {
            Category = body.Category,
            Message = body.Message,
            SessionId = body.SessionId,
            DataJson = Serialize(body.Data),
            CreatedAtUtc = body.CreatedAtUtc ?? DateTimeOffset.UtcNow,
        };
    }

    /// <summary>
    /// Maps a persisted audit entity into an audit event body.
    /// </summary>
    public static AuditEventBody ToBody(AuditEventEntity entity)
    {
        return new AuditEventBody(
            entity.Category,
            entity.Message,
            entity.SessionId,
            DeserializeObjectDictionary(entity.DataJson),
            entity.CreatedAtUtc);
    }

    /// <summary>
    /// Maps a telemetry event body into a persisted telemetry entity.
    /// </summary>
    public static TelemetryEventEntity ToEntity(TelemetryEventBody body)
    {
        return new TelemetryEventEntity
        {
            Scope = body.Scope,
            SessionId = body.SessionId,
            MetricsJson = Serialize(body.Metrics) ?? "{}",
            CreatedAtUtc = body.CreatedAtUtc ?? DateTimeOffset.UtcNow,
        };
    }

    /// <summary>
    /// Maps a persisted telemetry entity into a telemetry event body.
    /// </summary>
    public static TelemetryEventBody ToBody(TelemetryEventEntity entity)
    {
        return new TelemetryEventBody(
            entity.Scope,
            DeserializeObjectDictionary(entity.MetricsJson) ?? new Dictionary<string, object?>(),
            entity.SessionId,
            entity.CreatedAtUtc);
    }

    /// <summary>
    /// Maps a command journal entry into a persisted command journal entity.
    /// </summary>
    public static CommandJournalEntity ToEntity(CommandJournalEntryBody body)
    {
        return new CommandJournalEntity
        {
            CommandId = body.CommandId,
            SessionId = body.SessionId,
            AgentId = body.AgentId,
            Channel = body.Channel.ToString(),
            Action = body.Action,
            ArgsJson = Serialize(body.Args) ?? "{}",
            TimeoutMs = body.TimeoutMs,
            IdempotencyKey = body.IdempotencyKey,
            Status = body.Status.ToString(),
            ParticipantId = body.ParticipantId,
            PrincipalId = body.PrincipalId,
            ShareId = body.ShareId,
            ErrorCode = body.Error?.Code,
            ErrorJson = Serialize(body.Error),
            MetricsJson = Serialize(body.Metrics),
            CreatedAtUtc = body.CreatedAtUtc,
            CompletedAtUtc = body.CompletedAtUtc,
        };
    }

    /// <summary>
    /// Maps a persisted command journal entity into a command journal entry.
    /// </summary>
    public static CommandJournalEntryBody ToBody(CommandJournalEntity entity)
    {
        return new CommandJournalEntryBody(
            entity.CommandId,
            entity.SessionId,
            entity.AgentId,
            Enum.Parse<CommandChannel>(entity.Channel, ignoreCase: true),
            entity.Action,
            DeserializeObjectDictionary(entity.ArgsJson) ?? new Dictionary<string, object?>(),
            entity.TimeoutMs,
            entity.IdempotencyKey,
            Enum.Parse<CommandStatus>(entity.Status, ignoreCase: true),
            entity.CreatedAtUtc,
            entity.CompletedAtUtc,
            DeserializeErrorInfo(entity.ErrorJson),
            DeserializeDoubleDictionary(entity.MetricsJson),
            entity.ParticipantId,
            entity.PrincipalId,
            entity.ShareId);
    }

    private static string? Serialize(object? value)
        => value is null ? null : JsonSerializer.Serialize(value, SerializerOptions);

    private static IReadOnlyDictionary<string, string>? DeserializeStringDictionary(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? null
            : JsonSerializer.Deserialize<Dictionary<string, string>>(value, SerializerOptions);

    private static IReadOnlyDictionary<string, object?>? DeserializeObjectDictionary(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var raw = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(value, SerializerOptions);
        return raw?.ToDictionary(x => x.Key, x => ConvertJsonElement(x.Value));
    }

    private static IReadOnlyDictionary<string, double>? DeserializeDoubleDictionary(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return JsonSerializer.Deserialize<Dictionary<string, double>>(value, SerializerOptions);
    }

    private static ErrorInfo? DeserializeErrorInfo(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? null
            : JsonSerializer.Deserialize<ErrorInfo>(value, SerializerOptions);

    private static object? ConvertJsonElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when element.TryGetInt64(out var int64) => int64,
            JsonValueKind.Number when element.TryGetDouble(out var doubleValue) => doubleValue,
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Array => element.EnumerateArray().Select(ConvertJsonElement).ToArray(),
            JsonValueKind.Object => element.EnumerateObject().ToDictionary(x => x.Name, x => ConvertJsonElement(x.Value)),
            _ => element.GetRawText(),
        };
    }
}
