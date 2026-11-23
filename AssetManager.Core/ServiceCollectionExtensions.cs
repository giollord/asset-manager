using AssetManager.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AssetManager.Core;

public static class CoreServiceCollectionExtensions
{
    /// <summary>Registers the Core services: app settings and shared infrastructure.</summary>
    public static IServiceCollection AddAssetManagerCore(this IServiceCollection services, string? settingsPath = null)
    {
        services.TryAddSingleton<IAppSettingsStore>(_ => new JsonAppSettingsStore(settingsPath));
        services.TryAddSingleton<AppStartup>();
        services.TryAddSingleton<Platform.FileDropBroker>();
        return services;
    }
}
