using HidBridge.ControlPlane.Api;
using HidBridge.Contracts;
using Xunit;

namespace HidBridge.Platform.Tests;

/// <summary>
/// Verifies unified timeline composition.
/// </summary>
public sealed class TimelineComposerTests
{
    /// <summary>
    /// Verifies that command journal, audit, and telemetry records are merged in reverse chronological order.
    /// </summary>
    [Fact]
    public void Compose_MergesRecordsByDescendingTimestamp()
    {
        var sessionId = "session-1";
        var baseTime = DateTimeOffset.UtcNow;
        var audit = new[]
        {
            new AuditEventBody("session", "Session opened", sessionId, CreatedAtUtc: baseTime.AddSeconds(-10)),
        };
        var telemetry = new[]
        {
            new TelemetryEventBody("command", new Dictionary<string, object?> { ["status"] = "Applied" }, sessionId, baseTime.AddSeconds(-5)),
        };
        var commands = new[]
        {
            new CommandJournalEntryBody("cmd-1", sessionId, "agent-1", CommandChannel.Hid, "keyboard.text", new Dictionary<string, object?>(), 250, "idem-1", CommandStatus.Applied, baseTime.AddSeconds(-8), baseTime.AddSeconds(-2)),
        };

        var timeline = TimelineComposer.Compose(audit, telemetry, commands, sessionId, 10);

        Assert.Equal(3, timeline.Count);
        Assert.Equal("command", timeline[0].Kind);
        Assert.Equal("telemetry", timeline[1].Kind);
        Assert.Equal("audit", timeline[2].Kind);
    }
}
