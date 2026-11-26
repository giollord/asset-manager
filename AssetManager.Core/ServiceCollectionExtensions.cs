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
        services.TryAddSingleton(_ => AppPaths.Location);
        services.TryAddSingleton<AppStartup>();
        services.TryAddSingleton<StartupSignals>();
        services.TryAddSingleton<Platform.FileDropBroker>();
        services.TryAddSingleton<Platform.DragOutState>();
        services.TryAddSingleton(sp => new Updates.UpdateChecker(sp.GetRequiredService<IAppSettingsStore>(),
            logger: sp.GetService<Microsoft.Extensions.Logging.ILogger<Updates.UpdateChecker>>()));
        services.AddSingleton<IStartupTask, Updates.UpdateStartupTask>();
        services.AddSingleton<IStartupTask, Platform.SelfExtractCleanupStartupTask>();
        return services;
    }
}
