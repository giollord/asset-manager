using AssetManager.Core.Domain;
using AssetManager.Core.Search;
using AssetManager.Data;
using AssetManager.Data.Search;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AssetManager.AI.Enrichment;

/// <summary>The embedding model in use: the generator and an id that changes when the model file changes.</summary>
public sealed record EmbeddingModel(IEmbeddingGenerator<string, Embedding<float>> Generator, string Id);

/// <summary>
/// Computes asset embeddings from name, type, description and tags (EmbeddingGemma prompt format), truncated to the
/// index dimension (Matryoshka) and normalized, and stores them through <see cref="IVectorIndex"/>. Assets are
/// re-embedded when the catalog marks them dirty (see <c>EmbeddingDirtyInterceptor</c>) or the model changes.
/// </summary>
/// <param name="model">The running embedding model, or null.</param>
/// <param name="configured">Whether a model is set up (it may not be loaded yet); default: <paramref name="model"/> is not null.</param>
/// <param name="ensureLoaded">Loads the configured model when it is not running (models load when first needed, design D16).</param>
public sealed class EmbeddingService(CatalogStore catalog, IVectorIndex index, Func<EmbeddingModel?> model, ILogger<EmbeddingService>? logger = null,
    Func<bool>? configured = null, Func<CancellationToken, Task>? ensureLoaded = null)
{
    private readonly ILogger _log = logger ?? (ILogger)NullLogger.Instance;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public int Dimensions { get; init; } = SqliteVecIndex.DefaultDimensions;

    /// <summary>Semantic features can be offered: a model runs or is configured and loads on first use.</summary>
    public bool IsAvailable => model() is not null || configured?.Invoke() == true;

    /// <summary>The running model, loading the configured one first if needed.</summary>
    private async Task<EmbeddingModel?> ModelAsync(CancellationToken ct)
    {
        if (model() is { } running) return running;
        if (ensureLoaded is null || configured?.Invoke() != true) return null;
        await ensureLoaded(ct);
        return model();
    }

    public static string DocumentText(Asset asset, IEnumerable<string> tags)
    {
        var tagList = string.Join(", ", tags.Order(StringComparer.Ordinal));
        var text = $"{asset.Type}. {asset.Description?.Trim()}".Trim();
        if (tagList.Length > 0) text += $" Tags: {tagList}.";
        return $"title: {asset.Name} | text: {text}";
    }

    public static string QueryText(string query) => $"task: search result | query: {query.Trim()}";

    /// <summary>Embeds a search query, loading the model first if needed; null without an embedding model.</summary>
    public async Task<float[]?> EmbedQueryAsync(string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query) || await ModelAsync(ct) is not { } m) return null;
        var result = await m.Generator.GenerateAsync([QueryText(query)], cancellationToken: ct);
        return Fit(result[0].Vector.Span);
    }

    /// <summary>Embeds a document-style text (e.g. a described query image), loading the model first if needed.</summary>
    public async Task<float[]?> EmbedDocumentAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text) || await ModelAsync(ct) is not { } m) return null;
        var result = await m.Generator.GenerateAsync([text], cancellationToken: ct);
        return Fit(result[0].Vector.Span);
    }

    /// <summary>Re-embeds every dirty committed asset (and those embedded with another model); returns how many.</summary>
    public async Task<int> RefreshDirtyAsync(int batchSize = 32, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        if (model() is null)
        {
            // Not loaded: load it only when there is something to embed (design D16). Assets embedded with another
            // model are re-embedded the next time the model runs.
            if (configured?.Invoke() != true) return 0;
            await using (var db = catalog.CreateDbContext())
                if (!await db.Assets.AnyAsync(a => a.EmbeddingDirty, ct)) return 0;
            if (await ModelAsync(ct) is null) return 0;
        }
        await _gate.WaitAsync(ct);
        try
        {
            var m = model();
            if (m is null) return 0;
            await MarkOtherModelsDirtyAsync(m.Id, ct);
            var done = 0;
            long after = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                List<(long Id, string Text)> batch;
                await using (var db = catalog.CreateDbContext())
                {
                    var assets = await db.Assets.AsNoTracking().Where(a => a.EmbeddingDirty && a.Id > after)
                        .OrderBy(a => a.Id).Take(batchSize).ToListAsync(ct);
                    if (assets.Count == 0) break;
                    batch = await TextsAsync(db, assets, ct);
                }
                after = batch[^1].Id;

                var vectors = await m.Generator.GenerateAsync(batch.Select(b => b.Text), cancellationToken: ct);
                for (var i = 0; i < batch.Count; i++)
                    await index.UpsertAsync(batch[i].Id, Fit(vectors[i].Vector.Span), m.Id, ct);

                // Only clear the flag when nothing changed while we were embedding; otherwise the next run redoes it.
                await using (var db = catalog.CreateDbContext())
                {
                    var ids = batch.Select(b => b.Id).ToList();
                    var fresh = await TextsAsync(db, await db.Assets.AsNoTracking().Where(a => ids.Contains(a.Id)).ToListAsync(ct), ct);
                    var unchanged = fresh.Where(f => batch.Any(b => b.Id == f.Id && b.Text == f.Text)).Select(f => f.Id).ToList();
                    await db.AllAssets.Where(a => unchanged.Contains(a.Id)).ExecuteUpdateAsync(s => s.SetProperty(a => a.EmbeddingDirty, false), ct);
                }
                done += batch.Count;
                progress?.Report(done);
            }
            if (done > 0) _log.LogInformation("Embedded {Count} assets with {Model}", done, m.Id);
            return done;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task MarkOtherModelsDirtyAsync(string modelId, CancellationToken ct)
    {
        await using var db = catalog.CreateDbContext();
        var n = await db.AllAssets.Where(a => !a.EmbeddingDirty && db.Embeddings.Any(e => e.AssetId == a.Id && e.Model != modelId))
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.EmbeddingDirty, true), ct);
        if (n > 0) _log.LogInformation("Embedding model changed to {Model}: {Count} assets will be re-embedded", modelId, n);
    }

    private static async Task<List<(long Id, string Text)>> TextsAsync(CatalogDbContext db, List<Asset> assets, CancellationToken ct)
    {
        var ids = assets.Select(a => a.Id).ToList();
        var tags = (await db.AssetTags.Where(t => ids.Contains(t.AssetId)).Select(t => new { t.AssetId, t.Tag.Name }).ToListAsync(ct))
            .ToLookup(t => t.AssetId, t => t.Name);
        return assets.Select(a => (a.Id, DocumentText(a, tags[a.Id]))).ToList();
    }

    /// <summary>Matryoshka truncation to the index dimension, then unit length.</summary>
    private float[] Fit(ReadOnlySpan<float> v)
    {
        if (v.Length < Dimensions) throw new InvalidOperationException($"The embedding model returned {v.Length} dimensions; at least {Dimensions} are needed.");
        return VectorMath.Normalize(v[..Dimensions]);
    }
}
