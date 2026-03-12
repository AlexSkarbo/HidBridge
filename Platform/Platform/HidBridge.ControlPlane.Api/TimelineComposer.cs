using HidBridge.Contracts;

namespace HidBridge.ControlPlane.Api;

/// <summary>
/// Builds a unified collaboration timeline from audit, telemetry, and command journal records.
/// </summary>
public static class TimelineComposer
{
    /// <summary>
    /// Merges the supplied audit, telemetry, and command journal records into a single reverse-chronological timeline.
    /// </summary>
    /// <param name="auditEvents">The audit event sequence.</param>
    /// <param name="telemetryEvents">The telemetry event sequence.</param>
    /// <param name="commandJournal">The command journal sequence.</param>
    /// <param name="sessionId">The target session identifier.</param>
    /// <param name="take">The maximum number of timeline items to return.</param>
    /// <returns>The merged timeline entries.</returns>
    public static IReadOnlyList<TimelineEntryBody> Compose(
        IReadOnlyList<AuditEventBody> auditEvents,
        IReadOnlyList<TelemetryEventBody> telemetryEvents,
        IReadOnlyList<CommandJournalEntryBody> commandJournal,
        string sessionId,
        int take)
    {
        var normalizedTake = Math.Clamp(take, 1, 500);

        var auditEntries = auditEvents
            .Where(x => string.Equals(x.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
            .Select(x => new TimelineEntryBody(
                Kind: "audit",
                Title: x.Message,
                OccurredAtUtc: x.CreatedAtUtc ?? DateTimeOffset.UtcNow,
                SessionId: x.SessionId,
                Data: x.Data));

        var telemetryEntries = telemetryEvents
            .Where(x => string.Equals(x.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
            .Select(x => new TimelineEntryBody(
                Kind: "telemetry",
                Title: $"Telemetry {x.Scope}",
                OccurredAtUtc: x.CreatedAtUtc ?? DateTimeOffset.UtcNow,
                SessionId: x.SessionId,
                Data: x.Metrics));

        var commandEntries = commandJournal
            .Where(x => string.Equals(x.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
            .Select(x => new TimelineEntryBody(
                Kind: "command",
                Title: $"Command {x.Action} -> {x.Status}",
                OccurredAtUtc: x.CompletedAtUtc ?? x.CreatedAtUtc,
                SessionId: x.SessionId,
                Data: new Dictionary<string, object?>
                {
                    ["commandId"] = x.CommandId,
                    ["agentId"] = x.AgentId,
                    ["channel"] = x.Channel.ToString(),
                    ["status"] = x.Status.ToString(),
                    ["participantId"] = x.ParticipantId,
                    ["principalId"] = x.PrincipalId,
                    ["shareId"] = x.ShareId,
                    ["errorCode"] = x.Error?.Code,
                }));

        return auditEntries
            .Concat(telemetryEntries)
            .Concat(commandEntries)
            .OrderByDescending(x => x.OccurredAtUtc)
            .Take(normalizedTake)
            .ToArray();
    }
}
