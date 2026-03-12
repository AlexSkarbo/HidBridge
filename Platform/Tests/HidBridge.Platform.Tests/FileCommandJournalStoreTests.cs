using HidBridge.Contracts;
using HidBridge.Persistence;
using Xunit;

namespace HidBridge.Platform.Tests;

/// <summary>
/// Verifies file-backed command journal persistence.
/// </summary>
public sealed class FileCommandJournalStoreTests
{
    /// <summary>
    /// Verifies that appended command entries can be listed globally and by session.
    /// </summary>
    [Fact]
    public async Task AppendAndListAsync_PersistsJournalEntries()
    {
        var rootDirectory = Path.Combine(Path.GetTempPath(), "hidbridge-command-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FileCommandJournalStore(new FilePersistenceOptions(rootDirectory));
            var entry = new CommandJournalEntryBody(
                CommandId: "cmd-1",
                SessionId: "session-1",
                AgentId: "agent-1",
                Channel: CommandChannel.Hid,
                Action: "keyboard.text",
                Args: new Dictionary<string, object?> { ["text"] = "abc" },
                TimeoutMs: 300,
                IdempotencyKey: "idem-1",
                Status: CommandStatus.Applied,
                CreatedAtUtc: DateTimeOffset.UtcNow,
                CompletedAtUtc: DateTimeOffset.UtcNow,
                PrincipalId: "owner");

            await store.AppendAsync(entry, TestContext.Current.CancellationToken);

            var all = await store.ListAsync(TestContext.Current.CancellationToken);
            var bySession = await store.ListBySessionAsync("session-1", TestContext.Current.CancellationToken);

            Assert.Single(all);
            Assert.Single(bySession);
            Assert.Equal("cmd-1", all[0].CommandId);
            Assert.Equal("keyboard.text", bySession[0].Action);
        }
        finally
        {
            if (Directory.Exists(rootDirectory))
            {
                Directory.Delete(rootDirectory, recursive: true);
            }
        }
    }

    /// <summary>
    /// Verifies that the file-backed journal treats repeated command identifiers and idempotency keys as idempotent writes.
    /// </summary>
    [Fact]
    public async Task AppendAsync_DeduplicatesRepeatedCommandEntries()
    {
        var rootDirectory = Path.Combine(Path.GetTempPath(), "hidbridge-command-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FileCommandJournalStore(new FilePersistenceOptions(rootDirectory));
            var createdAtUtc = DateTimeOffset.UtcNow;
            var entry = new CommandJournalEntryBody(
                CommandId: "cmd-dup-1",
                SessionId: "session-dup",
                AgentId: "agent-1",
                Channel: CommandChannel.Hid,
                Action: "noop",
                Args: new Dictionary<string, object?>(),
                TimeoutMs: 100,
                IdempotencyKey: "idem-dup-1",
                Status: CommandStatus.Applied,
                CreatedAtUtc: createdAtUtc,
                CompletedAtUtc: createdAtUtc);

            await store.AppendAsync(entry, TestContext.Current.CancellationToken);
            await store.AppendAsync(entry, TestContext.Current.CancellationToken);

            var all = await store.ListAsync(TestContext.Current.CancellationToken);
            var byCommandId = await store.FindByCommandIdAsync("cmd-dup-1", TestContext.Current.CancellationToken);
            var byIdempotencyKey = await store.FindByIdempotencyKeyAsync("session-dup", "idem-dup-1", TestContext.Current.CancellationToken);

            Assert.Single(all);
            Assert.NotNull(byCommandId);
            Assert.NotNull(byIdempotencyKey);
        }
        finally
        {
            if (Directory.Exists(rootDirectory))
            {
                Directory.Delete(rootDirectory, recursive: true);
            }
        }
    }
}
