using System.Reflection;
using Xunit;

namespace HidBridge.Platform.Tests;

/// <summary>
/// Verifies that the SQL migration baseline is present in the migrations assembly.
/// </summary>
public sealed class SqlMigrationsMetadataTests
{
    /// <summary>
    /// Ensures that the initial SQL migration type is available for runtime migration application.
    /// </summary>
    [Fact]
    public void MigrationsAssembly_ContainsInitialCreateMigration()
    {
        var migrationAssembly = Assembly.Load("HidBridge.Persistence.Sql.Migrations");
        var migrationType = migrationAssembly.GetType("HidBridge.Persistence.Sql.Migrations.Migrations.InitialCreate");

        Assert.NotNull(migrationType);
    }
}
