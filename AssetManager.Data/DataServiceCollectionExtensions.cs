using AssetManager.Core;
using AssetManager.Core.Search;
using AssetManager.Core.Settings;
using AssetManager.Data.Search;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AssetManager.Data;

public static class DataServiceCollectionExtensions
{
    public static IServiceCollection AddAssetManagerData(this IServiceCollection services)
    {
        services.TryAddSingleton<SqliteVecInterceptor>(_ => new SqliteVecInterceptor());
        services.TryAddSingleton<CatalogStore>(sp => new CatalogStore(
            sp.GetService<Microsoft.Extensions.Logging.ILogger<CatalogStore>>(), [sp.GetRequiredService<SqliteVecInterceptor>()]));
        services.TryAddSingleton<VectorIndexSelector>();
        services.TryAddSingleton<IVectorIndex>(sp => sp.GetRequiredService<VectorIndexSelector>());
        services.TryAddSingleton<IDbContextFactory<CatalogDbContext>>(sp => sp.GetRequiredService<CatalogStore>());
        services.TryAddSingleton<Services.TagService>();
        services.TryAddSingleton<Services.PreviewQueries>();
        services.TryAddSingleton<Services.AssetDetailsService>();
        services.TryAddSingleton<Search.AssetSearchService>();
        services.TryAddSingleton<Search.CatalogStatsService>();
        services.TryAddSingleton<Search.ICatalogStatsService>(sp => sp.GetRequiredService<Search.CatalogStatsService>());
        services.TryAddSingleton<Services.CollectionService>();
        services.TryAddSingleton<Services.MetadataEditService>();
        services.TryAddSingleton<Services.DeletionService>();
        services.TryAddSingleton<Services.SourceService>();
        services.TryAddSingleton<Services.CatalogLocationService>(sp => new Services.CatalogLocationService(sp.GetRequiredService<CatalogStore>(), sp.GetRequiredService<IAppSettingsStore>(), sp.GetService<Core.Jobs.IProgressHub>()));
        services.AddSingleton<IStartupTask, OpenCatalogStartupTask>();
        services.AddSingleton<IStartupTask, VectorIndexStartupTask>();
        services.TryAddSingleton<Jobs.ProgressHub>();
        services.TryAddSingleton<Core.Jobs.IProgressHub>(sp => sp.GetRequiredService<Jobs.ProgressHub>());
        services.TryAddSingleton<Jobs.JobQueue>(sp => new Jobs.JobQueue(sp.GetRequiredService<CatalogStore>(), () => sp.GetServices<Core.Jobs.IJobHandler>(),
            sp.GetRequiredService<Jobs.ProgressHub>(), sp.GetService<Microsoft.Extensions.Logging.ILogger<Jobs.JobQueue>>()));
        services.TryAddSingleton<Core.Jobs.IJobQueue>(sp => sp.GetRequiredService<Jobs.JobQueue>());
        services.AddSingleton<IStartupTask, Jobs.JobQueueStartupTask>();
        return services;
    }
}

/// <summary>Opens the catalog configured in the app settings at startup.</summary>
internal sealed class OpenCatalogStartupTask(CatalogStore catalog, IAppSettingsStore settings) : IStartupTask
{
    public int Order => 0;

    public Task RunAsync(CancellationToken ct) => catalog.OpenAsync(settings.Current.CatalogPath, ct);
}

/// <summary>Chooses sqlite-vec or the in-memory fallback once the catalog is open, and again after a catalog switch.</summary>
internal sealed class VectorIndexStartupTask(CatalogStore catalog, VectorIndexSelector index) : IStartupTask
{
    public int Order => 10;

    public Task RunAsync(CancellationToken ct)
    {
        catalog.Opened += () => _ = index.InitializeAsync();
        return index.InitializeAsync(ct);
    }
}
