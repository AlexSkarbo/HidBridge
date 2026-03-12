using HidBridge.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HidBridge.Persistence.Sql;

/// <summary>
/// Registers the PostgreSQL-backed persistence services.
/// </summary>
public static class ServiceCollectionExtensions
{
    private const string MigrationsAssemblyName = "HidBridge.Persistence.Sql.Migrations";

    /// <summary>
    /// Adds the SQL persistence services backed by PostgreSQL.
    /// </summary>
    /// <param name="services">The service collection to extend.</param>
    /// <param name="options">The SQL persistence options.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddSqlPersistence(this IServiceCollection services, SqlPersistenceOptions options)
    {
        services.AddSingleton(options);
        services.AddDbContextFactory<PlatformDbContext>((sp, builder) =>
        {
            var sqlOptions = sp.GetRequiredService<SqlPersistenceOptions>();
            builder.UseNpgsql(sqlOptions.ConnectionString, npgsql =>
            {
                npgsql.MigrationsAssembly(MigrationsAssemblyName);
                npgsql.MigrationsHistoryTable("__EFMigrationsHistory", sqlOptions.Schema);
            });
        });
        services.AddSingleton<IConnectorCatalogStore, SqlConnectorCatalogStore>();
        services.AddSingleton<IEndpointSnapshotStore, SqlEndpointSnapshotStore>();
        services.AddSingleton<IEventStore, SqlEventStore>();
        services.AddSingleton<ICommandJournalStore, SqlCommandJournalStore>();
        services.AddSingleton<ISessionStore, SqlSessionStore>();
        services.AddSingleton<IPolicyStore, SqlPolicyStore>();
        services.AddSingleton<IPolicyRevisionStore, SqlPolicyRevisionStore>();
        return services;
    }
}
