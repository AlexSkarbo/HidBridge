using HidBridge.Abstractions;
using HidBridge.Contracts;

namespace HidBridge.ControlPlane.Api;

/// <summary>
/// Applies retention rules to versioned policy revision snapshots.
/// </summary>
public sealed class PolicyRevisionLifecycleService
{
    private readonly IPolicyRevisionStore _revisionStore;
    private readonly IEventWriter _eventWriter;
    private readonly PolicyRevisionLifecycleOptions _options;

    /// <summary>
    /// Creates the policy revision lifecycle service.
    /// </summary>
    public PolicyRevisionLifecycleService(
        IPolicyRevisionStore revisionStore,
        IEventWriter eventWriter,
        PolicyRevisionLifecycleOptions options)
    {
        _revisionStore = revisionStore;
        _eventWriter = eventWriter;
        _options = options;
    }

    /// <summary>
    /// Gets the active lifecycle options.
    /// </summary>
    public PolicyRevisionLifecycleOptions Options => _options;

    /// <summary>
    /// Applies retention rules and returns the number of pruned revisions.
    /// </summary>
    public async Task<int> TrimAsync(CancellationToken cancellationToken)
    {
        var retainSinceUtc = DateTimeOffset.UtcNow - _options.Retention;
        var deleted = await _revisionStore.PruneAsync(retainSinceUtc, _options.MaxRevisionsPerEntity, cancellationToken);
        if (deleted <= 0)
        {
            return 0;
        }

        await _eventWriter.WriteAuditAsync(
            new AuditEventBody(
                Category: "policy.retention",
                Message: $"Pruned {deleted} policy revisions",
                SessionId: null,
                Data: new Dictionary<string, object?>
                {
                    ["deleted"] = deleted,
                    ["retainSinceUtc"] = retainSinceUtc,
                    ["maxRevisionsPerEntity"] = _options.MaxRevisionsPerEntity,
                },
                CreatedAtUtc: DateTimeOffset.UtcNow),
            cancellationToken);

        return deleted;
    }
}
