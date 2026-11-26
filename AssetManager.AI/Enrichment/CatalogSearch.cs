using AssetManager.Core.Search;
using AssetManager.Data;
using AssetManager.Data.Search;
using Microsoft.EntityFrameworkCore;

namespace AssetManager.AI.Enrichment;

/// <summary>
/// The search Browse uses (spec "Semantic search"): word search through <see cref="AssetSearchService"/>, or — with
/// <see cref="SearchQuery.Semantic"/> or <see cref="SearchQuery.SimilarTo"/> — ranking by embedding similarity
/// (KNN in the vector index), after which the other filters still apply.
/// </summary>
public sealed class CatalogSearch(CatalogStore catalog, AssetSearchService words, EmbeddingService embeddings, IVectorIndex index,
    ImageQueryCache? images = null)
{
    /// <summary>How many of the nearest results a search by image re-ranks by palette (design D6).</summary>
    public const int ImageRerankCount = 200;

    /// <summary>How many nearest neighbours a semantic search considers before filtering.</summary>
    public int Neighbours { get; init; } = 2000;

    /// <summary>True when the query ranks by meaning (it needs the embedding model, or an embedded asset for "find similar").</summary>
    public static bool IsSemantic(SearchQuery q) =>
        q.SimilarTo is not null || q.ImageQueryId is not null || (q.Semantic && !string.IsNullOrWhiteSpace(q.Text));

    public bool SemanticAvailable => embeddings.IsAvailable;

    public async Task<SearchPage> SearchAsync(SearchQuery query, int skip, int take, CancellationToken ct = default)
    {
        if (!IsSemantic(query)) return await words.SearchAsync(query, skip, take, ct);
        var ranked = await RankAsync(query, ct);
        var pageIds = ranked.Skip(skip).Take(take).ToList();
        await using var db = catalog.CreateDbContext();
        var rows = await AssetSearchService.Project(db.Assets.AsNoTracking().Where(a => pageIds.Contains(a.Id))).ToListAsync(ct);
        var byId = rows.ToDictionary(r => r.Id);
        return new SearchPage(pageIds.Where(byId.ContainsKey).Select(id => byId[id]).ToList(), ranked.Count);
    }

    /// <summary>Matching asset ids, most similar first.</summary>
    private async Task<List<long>> RankAsync(SearchQuery query, CancellationToken ct)
    {
        float[]? vector;
        ImageQuery? image = null;
        if (query.ImageQueryId is { } imageId)
        {
            image = images?.Get(imageId) ?? throw new ImageQueryExpiredException();
            vector = image.Vector;
        }
        else if (query.SimilarTo is { } similar)
        {
            await using var db = catalog.CreateDbContext();
            var stored = await db.Embeddings.AsNoTracking().Where(e => e.AssetId == similar).Select(e => e.Vector).FirstOrDefaultAsync(ct);
            if (stored is null) throw new InvalidOperationException("This asset has no embedding yet; it is created after AI enrichment.");
            vector = VectorMath.FromBytes(stored);
        }
        else
        {
            vector = await embeddings.EmbedQueryAsync(query.Text!, ct)
                ?? throw new InvalidOperationException("Semantic search needs the embedding model (Settings → AI model).");
        }

        var hits = await index.SearchAsync(vector, Neighbours, ct);
        var candidates = hits.Where(h => h.AssetId != query.SimilarTo).ToList();
        // Every other filter applies to the neighbours; the words of a semantic query rank instead of filtering.
        var filters = query with { Text = null, Semantic = false, SimilarTo = null, ImageQueryId = null };
        var allowed = await words.MatchingIdsAsync(filters, candidates.Select(h => h.AssetId).ToList(), ct);
        var ranked = candidates.Where(h => allowed.Contains(h.AssetId)).OrderBy(h => h.Distance).ToList();
        if (image is null) return ranked.Select(h => h.AssetId).ToList();

        // Search by image: the nearest results are re-ranked by 0.8 · cosine + 0.2 · palette similarity.
        var top = ranked.Take(ImageRerankCount).ToList();
        var topIds = top.Select(h => h.AssetId).ToList();
        await using var colorsDb = catalog.CreateDbContext();
        var colors = (await colorsDb.Assets.AsNoTracking().Where(a => topIds.Contains(a.Id)).Select(a => new { a.Id, a.Colors }).ToListAsync(ct))
            .ToDictionary(a => a.Id, a => a.Colors);
        var reranked = top
            .OrderByDescending(h => PaletteSimilarity.Score(1 - h.Distance, PaletteSimilarity.Of(image.Palette, colors.GetValueOrDefault(h.AssetId) ?? [])))
            .Select(h => h.AssetId);
        return reranked.Concat(ranked.Skip(ImageRerankCount).Select(h => h.AssetId)).ToList();
    }
}
