using AssetManager.AI.Runtime;
using AssetManager.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AssetManager.AI;

public static class AiServiceCollectionExtensions
{
    public static IServiceCollection AddAssetManagerAi(this IServiceCollection services)
    {
        services.TryAddSingleton<AiRuntime>();
        services.TryAddSingleton<ModelDownloader>(sp => new ModelDownloader(sp.GetRequiredService<Core.Settings.IAppSettingsStore>(),
            logger: sp.GetService<Microsoft.Extensions.Logging.ILogger<ModelDownloader>>()));
        services.TryAddSingleton<ModelInstalls>();
        services.AddSingleton<IStartupTask, AiRuntimeStartupTask>();
        services.TryAddSingleton<Enrichment.IPreviewRerenderer, Enrichment.CatalogPreviewRerenderer>();
        services.TryAddSingleton<Enrichment.EmbeddingRefresher>();
        services.TryAddSingleton<Enrichment.CollectionDescriber>();
        services.TryAddSingleton(sp => new Enrichment.AiQueue(sp.GetRequiredService<Data.CatalogStore>(), sp.GetRequiredService<AiRuntime>(),
            sp.GetRequiredService<Core.Jobs.IJobQueue>(), sp.GetRequiredService<Core.Jobs.IProgressHub>()));
        services.AddSingleton<IStartupTask>(sp => sp.GetRequiredService<Enrichment.EmbeddingRefresher>());
        services.TryAddSingleton(sp =>
        {
            var runtime = sp.GetRequiredService<AiRuntime>();
            var settings = sp.GetRequiredService<Core.Settings.IAppSettingsStore>();
            return new Enrichment.EmbeddingService(sp.GetRequiredService<Data.CatalogStore>(), sp.GetRequiredService<Core.Search.IVectorIndex>(),
                () => runtime.Embeddings is { } g ? new Enrichment.EmbeddingModel(g, Path.GetFileName(settings.Current.EmbeddingModelPath ?? "embedding")) : null,
                sp.GetService<Microsoft.Extensions.Logging.ILogger<Enrichment.EmbeddingService>>());
        });
        services.TryAddSingleton<Enrichment.CatalogSearch>();
        services.TryAddSingleton<Import.ImportEnricher>();
        services.TryAddSingleton<Import.ImportService>();
        services.AddSingleton<Core.Jobs.IJobHandler, Import.ImportJob>();
        services.AddSingleton<Core.Jobs.IJobHandler, Import.ImportCommitJob>();
        services.AddSingleton<Core.Jobs.IJobHandler, Import.EnrichAssetsJob>();
        services.AddSingleton<Core.Jobs.IJobHandler, Enrichment.DescribeCollectionJob>();
        services.AddSingleton<Import.ImportReviewService>();
        services.TryAddSingleton<Enrichment.ReenrichService>();
        return services;
    }
}
