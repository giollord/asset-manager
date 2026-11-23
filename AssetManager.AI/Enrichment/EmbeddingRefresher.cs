using AssetManager.AI.Runtime;
using AssetManager.Core;
using AssetManager.Data;
using Microsoft.Extensions.Logging;

namespace AssetManager.AI.Enrichment;

/// <summary>
/// Keeps embeddings current in the background: after catalog changes (debounced) and when the embedding server
/// becomes ready, dirty assets are re-embedded. Runs after the vector index is initialized.
/// </summary>
public sealed class EmbeddingRefresher(CatalogStore catalog, AiRuntime runtime, EmbeddingService embeddings, ILogger<EmbeddingRefresher> logger)
    : IStartupTask, IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(2);
    private readonly Lock _lock = new();
    private Timer? _timer;
    private bool _wasReady;

    public int Order => 40;

    public Task RunAsync(CancellationToken ct)
    {
        catalog.ContentChanged += Schedule;
        catalog.Opened += Schedule;
        runtime.Changed += OnRuntimeChanged;
        Schedule();
        return Task.CompletedTask;
    }

    private void OnRuntimeChanged()
    {
        var ready = runtime.EmbeddingsReady;
        if (ready && !_wasReady) Schedule();
        _wasReady = ready;
    }

    private void Schedule()
    {
        lock (_lock)
        {
            _timer ??= new Timer(_ => _ = RunOnceAsync());
            _timer.Change(Debounce, Timeout.InfiniteTimeSpan);
        }
    }

    private async Task RunOnceAsync()
    {
        if (catalog.Path is null || !embeddings.IsAvailable) return;
        try
        {
            await embeddings.RefreshDirtyAsync();
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Embedding refresh failed");
        }
    }

    public void Dispose()
    {
        catalog.ContentChanged -= Schedule;
        catalog.Opened -= Schedule;
        runtime.Changed -= OnRuntimeChanged;
        _timer?.Dispose();
    }
}
