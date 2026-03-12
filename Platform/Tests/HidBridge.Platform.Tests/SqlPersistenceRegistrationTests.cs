using HidBridge.Abstractions;
using HidBridge.Persistence.Sql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HidBridge.Platform.Tests;

/// <summary>
/// Verifies SQL persistence service registration and model baseline behavior.
/// </summary>
public sealed class SqlPersistenceRegistrationTests
{
    [Fact]
    /// <summary>
    /// Verifies that SQL persistence registers the expected store abstractions.
    /// </summary>
    public void AddSqlPersistence_RegistersExpectedServices()
    {
        var services = new ServiceCollection();
        services.AddSqlPersistence(new SqlPersistenceOptions("Host=localhost;Port=5432;Database=hidbridge;Username=postgres;Password=postgres", "hidbridge"));

        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetService<IConnectorCatalogStore>());
        Assert.NotNull(provider.GetService<IEndpointSnapshotStore>());
        Assert.NotNull(provider.GetService<ISessionStore>());
        Assert.NotNull(provider.GetService<IEventStore>());
        Assert.NotNull(provider.GetService<IDbContextFactory<PlatformDbContext>>());
    }

    [Fact]
    /// <summary>
    /// Verifies that the design-time factory creates a context with the configured default schema.
    /// </summary>
    public void PlatformDbContextFactory_CreatesContextWithConfiguredSchema()
    {
        var factory = new PlatformDbContextFactory();
        using var context = factory.CreateDbContext(new[]
        {
            "Host=localhost;Port=5432;Database=hidbridge;Username=postgres;Password=postgres",
        });

        Assert.Equal("hidbridge", context.Model.GetDefaultSchema());
    }
}
