using HidBridge.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace HidBridge.Persistence;

/// <summary>
/// Registers the file-backed persistence services used by the P0-3 baseline.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds the file-backed persistence services.
    /// </summary>
    /// <param name="services">The service collection to extend.</param>
    /// <param name="options">The file persistence options.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddFilePersistence(this IServiceCollection services, FilePersistenceOptions options)
    {
        services.AddSingleton(options);
        services.AddSingleton<IConnectorCatalogStore, FileConnectorCatalogStore>();
        services.AddSingleton<IEndpointSnapshotStore, FileEndpointSnapshotStore>();
        services.AddSingleton<IEventStore, FileEventStore>();
        services.AddSingleton<ICommandJournalStore, FileCommandJournalStore>();
        services.AddSingleton<ISessionStore, FileSessionStore>();
        services.AddSingleton<IPolicyStore, FilePolicyStore>();
        services.AddSingleton<IPolicyRevisionStore, FilePolicyRevisionStore>();
        return services;
    }
}
