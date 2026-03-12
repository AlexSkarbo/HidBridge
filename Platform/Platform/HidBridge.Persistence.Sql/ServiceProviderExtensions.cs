using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HidBridge.Persistence.Sql;

/// <summary>
/// Exposes SQL persistence startup helpers for service providers.
/// </summary>
public static class ServiceProviderExtensions
{
    /// <summary>
    /// Applies all pending EF Core migrations for the HidBridge SQL store.
    /// </summary>
    /// <param name="services">The root service provider hosting the persistence services.</param>
    /// <returns>A task that completes when migrations are applied.</returns>
    public static async Task ApplySqlPersistenceMigrationsAsync(this IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContextFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PlatformDbContext>>();
        await using var dbContext = await dbContextFactory.CreateDbContextAsync();
        await dbContext.Database.MigrateAsync();
    }
}
