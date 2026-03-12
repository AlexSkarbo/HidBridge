using HidBridge.Persistence.Sql.Entities;
using Microsoft.EntityFrameworkCore;

namespace HidBridge.Persistence.Sql;

/// <summary>
/// Represents the relational persistence model of the HidBridge control plane.
/// </summary>
public sealed class PlatformDbContext : DbContext
{
    private readonly SqlPersistenceOptions _options;

    /// <summary>
    /// Initializes the Platform database context.
    /// </summary>
    /// <param name="options">The EF Core database context options.</param>
    /// <param name="sqlOptions">The SQL persistence options.</param>
    public PlatformDbContext(DbContextOptions<PlatformDbContext> options, SqlPersistenceOptions sqlOptions)
        : base(options)
    {
        _options = sqlOptions;
    }

    /// <summary>
    /// Gets the persisted agent catalog table.
    /// </summary>
    internal DbSet<AgentEntity> Agents => Set<AgentEntity>();

    /// <summary>
    /// Gets the persisted endpoint snapshot table.
    /// </summary>
    internal DbSet<EndpointEntity> Endpoints => Set<EndpointEntity>();

    /// <summary>
    /// Gets the persisted agent capability table.
    /// </summary>
    internal DbSet<AgentCapabilityEntity> AgentCapabilities => Set<AgentCapabilityEntity>();

    /// <summary>
    /// Gets the persisted endpoint capability table.
    /// </summary>
    internal DbSet<EndpointCapabilityEntity> EndpointCapabilities => Set<EndpointCapabilityEntity>();

    /// <summary>
    /// Gets the persisted session snapshot table.
    /// </summary>
    internal DbSet<SessionEntity> Sessions => Set<SessionEntity>();

    /// <summary>
    /// Gets the persisted audit event table.
    /// </summary>
    internal DbSet<AuditEventEntity> AuditEvents => Set<AuditEventEntity>();

    /// <summary>
    /// Gets the persisted telemetry event table.
    /// </summary>
    internal DbSet<TelemetryEventEntity> TelemetryEvents => Set<TelemetryEventEntity>();

    /// <summary>
    /// Gets the persisted command journal table.
    /// </summary>
    internal DbSet<CommandJournalEntity> CommandJournal => Set<CommandJournalEntity>();

    /// <summary>
    /// Gets the persisted policy scope table.
    /// </summary>
    internal DbSet<PolicyScopeEntity> PolicyScopes => Set<PolicyScopeEntity>();

    /// <summary>
    /// Gets the persisted policy assignment table.
    /// </summary>
    internal DbSet<PolicyAssignmentEntity> PolicyAssignments => Set<PolicyAssignmentEntity>();

    /// <summary>
    /// Gets the persisted policy revision table.
    /// </summary>
    internal DbSet<PolicyRevisionEntity> PolicyRevisions => Set<PolicyRevisionEntity>();

    /// <summary>
    /// Configures the relational schema model.
    /// </summary>
    /// <param name="modelBuilder">The model builder.</param>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(_options.Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(PlatformDbContext).Assembly);
    }
}
