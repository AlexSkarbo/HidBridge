using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace HidBridge.Persistence.Sql;

/// <summary>
/// Creates the Platform database context for design-time tooling and migrations.
/// </summary>
public sealed class PlatformDbContextFactory : IDesignTimeDbContextFactory<PlatformDbContext>
{
    private const string MigrationsAssemblyName = "HidBridge.Persistence.Sql.Migrations";

    /// <summary>
    /// Creates the database context used by EF Core tooling.
    /// </summary>
    /// <param name="args">Optional design-time arguments. Argument 0 may contain the connection string.</param>
    /// <returns>The configured Platform database context.</returns>
    public PlatformDbContext CreateDbContext(string[] args)
    {
        var connectionString = args.FirstOrDefault()
            ?? Environment.GetEnvironmentVariable("HIDBRIDGE_SQL_CONNECTION")
            ?? "Host=localhost;Port=5432;Database=hidbridge;Username=postgres;Password=postgres";
        var schema = Environment.GetEnvironmentVariable("HIDBRIDGE_SQL_SCHEMA") ?? "hidbridge";
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseNpgsql(connectionString, npgsql =>
            {
                npgsql.MigrationsAssembly(MigrationsAssemblyName);
                npgsql.MigrationsHistoryTable("__EFMigrationsHistory", schema);
            })
            .Options;
        return new PlatformDbContext(options, new SqlPersistenceOptions(connectionString, schema));
    }
}
