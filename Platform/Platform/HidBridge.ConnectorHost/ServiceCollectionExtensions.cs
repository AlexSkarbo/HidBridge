using HidBridge.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace HidBridge.ConnectorHost;

/// <summary>
/// Registers the connector host services that keep live connector instances in memory.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds the connector registry, inventory, and event writer services.
    /// </summary>
    /// <param name="services">The service collection to extend.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddConnectorHost(this IServiceCollection services)
    {
        services.AddSingleton<InMemoryConnectorRegistry>();
        services.AddSingleton<IConnectorRegistry>(sp => sp.GetRequiredService<InMemoryConnectorRegistry>());
        services.AddSingleton<IEndpointInventory>(sp => sp.GetRequiredService<InMemoryConnectorRegistry>());
        services.AddSingleton<IEventWriter>(sp => sp.GetRequiredService<InMemoryConnectorRegistry>());
        return services;
    }
}
