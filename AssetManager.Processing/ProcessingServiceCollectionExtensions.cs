using AssetManager.Core;
using AssetManager.Core.Domain;
using AssetManager.Core.Platform;
using AssetManager.Core.Settings;
using AssetManager.Processing.Cache;
using AssetManager.Processing.Containers;
using AssetManager.Processing.Handlers;
using AssetManager.Processing.Locations;
using AssetManager.Processing.Settings;
using AssetManager.Processing.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace AssetManager.Processing;

public static class ProcessingServiceCollectionExtensions
{
    public static IServiceCollection AddAssetManagerProcessing(this IServiceCollection services)
    {
        services.TryAddSingleton<IVolumeService>(sp => OperatingSystem.IsWindows()
            ? new WindowsVolumeService(sp.GetService<ILogger<WindowsVolumeService>>())
            : new NoVolumeService());
        services.TryAddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<IAppSettingsStore>();
            return new ExtractionCache(AppPaths.CacheFor(settings.Current),
                () => new CacheLimits(settings.Current.CacheMaxSizeMb * 1024 * 1024, TimeSpan.FromDays(settings.Current.CacheMaxAgeDays)),
                sp.GetService<ILogger<ExtractionCache>>());
        });
        services.AddSingleton<IContainerReader, UnityPackageReader>();
        services.AddSingleton<IContainerReader, ArchiveReader>();
        services.TryAddSingleton<VirtualFileSystem>();
        services.TryAddSingleton<VolumeRegistry>();
        services.TryAddSingleton<ILocationResolver, LocationResolver>();
        services.TryAddSingleton<AvailabilityService>();
        services.TryAddSingleton<StoredOriginalsService>();
        services.TryAddSingleton<PreviewSettings>();
        services.TryAddSingleton<ClassificationSettings>();
        services.AddSingleton<IAssetTypeHandler, ImageHandler>();
        services.AddSingleton<IAssetTypeHandler, AudioHandler>();
        services.AddSingleton<IAssetTypeHandler>(sp => new ModelHandler(sp.GetService<Models.IModelRenderer>()));
        services.AddSingleton<IAssetTypeHandler, FontHandler>();
        services.AddSingleton<IAssetTypeHandler, VideoHandler>();
        services.AddSingleton<IAssetTypeHandler, DocumentHandler>();
        services.TryAddSingleton<CompanionResolver>();
        services.TryAddSingleton<PreviewService>();
        services.TryAddSingleton(sp => new TextureMatcher(sp.GetRequiredService<AssetManager.Data.CatalogStore>(), sp.GetRequiredService<ILocationResolver>(),
            sp.GetRequiredService<PreviewSettings>(), sp.GetService<Models.IModelRenderer>()));
        services.TryAddSingleton<ITextureMatcher>(sp => sp.GetRequiredService<TextureMatcher>());
        services.TryAddSingleton<ModelTextureService>();
        services.AddSingleton<IAssetTypeHandler, FallbackHandler>();
        services.TryAddSingleton<HandlerRegistry>();
        services.TryAddSingleton<AssetProcessor>();
        services.AddSingleton<IStartupTask, CacheCleanupStartupTask>();
        services.AddSingleton<IStartupTask, VolumeChangeStartupTask>();
        services.TryAddSingleton<Import.DuplicateDetector>();
        services.TryAddSingleton<Import.SourceVersions>();
        services.TryAddSingleton<Import.StagedRegrouping>();
        services.TryAddSingleton<Import.ImportStager>();
        services.AddSingleton<Core.Jobs.IJobHandler, Jobs.RegeneratePreviewsJob>();
        services.AddSingleton<Core.Jobs.IJobHandler, Jobs.ApplyTextureSuggestionsJob>();
        services.AddSingleton<Core.Jobs.IJobHandler, Jobs.RedetectSpritesJob>();
        services.TryAddSingleton<Sync.RescanService>();
        services.AddSingleton<Core.Jobs.IJobHandler, Sync.RescanJob>();
        services.TryAddSingleton<Sync.LibraryScanService>();
        services.AddSingleton<Core.Jobs.IJobHandler, Sync.LibraryScanJob>();
        services.TryAddSingleton<Sync.SyncStartupTask>();
        services.AddSingleton<IStartupTask>(sp => sp.GetRequiredService<Sync.SyncStartupTask>());
        services.AddSingleton<Core.Jobs.IJobHandler, Jobs.StoreOriginalsJob>();
        services.AddSingleton<Core.Jobs.IJobHandler, Jobs.RemoveStoredOriginalsJob>();
        services.AddSingleton<Core.Jobs.IJobHandler, Jobs.CompactJob>();
        services.AddSingleton<Core.Jobs.IJobHandler, Import.ReimportJob>();
        services.TryAddSingleton<Export.UnityReferences>();
        services.TryAddSingleton<Export.AssetExporter>();
        services.TryAddSingleton<Export.SpriteFrameExporter>();
        services.TryAddSingleton<Export.DragOutPlanner>();
        services.TryAddSingleton<Export.OriginalMedia>();
        services.AddSingleton<Core.Jobs.IJobHandler, Export.ExtractAssetsJob>();
        services.AddSingleton<Core.Jobs.IJobHandler, Export.CopyAssetsJob>();
        services.TryAddSingleton<Import.ReimportService>();
        return services;
    }
}

/// <summary>Applies the cache limits once at startup, in the background.</summary>
/// <summary>
/// A drive attached or detached changes which assets are available: it is reported as a catalog change, so cached
/// statistics are recomputed and open pages (dashboard, Browse, Sources) refresh.
/// </summary>
public sealed class VolumeChangeStartupTask(IVolumeService volumes, Data.CatalogStore catalog) : IStartupTask
{
    public int Order => 20;

    public Task RunAsync(CancellationToken ct)
    {
        volumes.VolumesChanged += catalog.NotifyContentChanged;
        return Task.CompletedTask;
    }
}

/// <summary>
/// Evicts old cache entries after the AI runtime has loaded its models (or a few minutes at most), at background
/// priority: listing thousands of cache folders must not compete with the window or the model load (design D5).
/// </summary>
public sealed class CacheCleanupStartupTask(ExtractionCache cache, ILogger<CacheCleanupStartupTask>? logger = null, Core.StartupSignals? signals = null) : IStartupTask
{
    public int Order => 20;

    /// <summary>The longest eviction waits for the AI runtime.</summary>
    public static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(5);

    public Task RunAsync(CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            if (signals is not null) await Task.WhenAny(signals.AiSettled, Task.Delay(MaxWait, ct));
            using var background = Core.Jobs.BackgroundIo.Enter(); // design D11: never compete with the window's reads
            try { cache.Evict(); }
            catch (Exception e) { logger?.LogWarning(e, "Cache cleanup failed"); }
        }, ct);
        return Task.CompletedTask;
    }
}
