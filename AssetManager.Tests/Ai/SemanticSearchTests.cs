using AssetManager.AI.Enrichment;
using AssetManager.Core.Domain;
using AssetManager.Core.Search;
using AssetManager.Data.Search;
using AssetManager.Tests.Data;
using Microsoft.Extensions.AI;

namespace AssetManager.Tests.Ai;

/// <summary>Semantic search and "find similar" with fixed embeddings (spec "Semantic search").</summary>
public class SemanticSearchTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Unit vectors in a 3-D "meaning space" padded to 256 dimensions: night/forest, water, metal.</summary>
    private static float[] V(float night, float water, float metal)
    {
        var v = new float[256];
        (v[0], v[1], v[2]) = (night, water, metal);
        return VectorMath.Normalize(v);
    }

    /// <summary>Returns a fixed vector for a query by keyword.</summary>
    private sealed class FixedGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken ct = default) =>
            Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(values.Select(t =>
                new Embedding<float>(t.Contains("spooky") ? V(1, 0.1f, 0) : t.Contains("rain") ? V(0, 1, 0) : V(0, 0, 1)))));

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private static async Task<(CatalogSearch Search, Dictionary<string, long> Ids)> SeedAsync(TestCatalog c)
    {
        var assets = new (string Name, AssetType Type, float[] Vector)[]
        {
            ("Owl hoots at midnight", AssetType.Audio, V(0.9f, 0, 0.1f)),
            ("Wind in dark woods", AssetType.Audio, V(1, 0.2f, 0)),
            ("Rain on roof", AssetType.Audio, V(0.1f, 1, 0)),
            ("Sword clang", AssetType.Audio, V(0, 0, 1)),
            ("Haunted tree", AssetType.Model3D, V(1, 0, 0)),
        };
        var src = await TestData.SourceAsync(c, assets.Length, make: i => new Asset { Name = assets[i].Name, Type = assets[i].Type });
        var index = new InMemoryVectorIndex(c.Store);
        var ids = src.Assets.ToDictionary(a => a.Name, a => a.Id);
        foreach (var a in assets) await index.UpsertAsync(ids[a.Name], a.Vector, "fixed", Ct);
        var embeddings = new EmbeddingService(c.Store, index, () => new EmbeddingModel(new FixedGenerator(), "fixed"));
        return (new CatalogSearch(c.Store, new AssetSearchService(c.Store), embeddings, index), ids);
    }

    [Fact]
    public async Task Meaning_based_query_ranks_by_similarity_and_keeps_filters()
    {
        await using var c = await TestCatalog.CreateAsync();
        var (search, _) = await SeedAsync(c);

        var page = await search.SearchAsync(new SearchQuery { Text = "spooky forest ambience", Semantic = true, Types = [AssetType.Audio] }, 0, 10, Ct);

        // No asset contains those words; the eerie woodland/night sounds rank first, and the type filter still applies.
        Assert.Equal(["Wind in dark woods", "Owl hoots at midnight", "Rain on roof", "Sword clang"], page.Items.Select(i => i.Name));
        Assert.Equal(4, page.Total);
        Assert.Empty((await search.SearchAsync(new SearchQuery { Text = "spooky forest ambience" }, 0, 10, Ct)).Items); // word mode: no match
    }

    [Fact]
    public async Task Find_similar_excludes_the_asset_itself()
    {
        await using var c = await TestCatalog.CreateAsync();
        var (search, ids) = await SeedAsync(c);

        var page = await search.SearchAsync(new SearchQuery { SimilarTo = ids["Haunted tree"] }, 0, 2, Ct);

        // cos(tree, owl) = 0.994 > cos(tree, wind) = 0.981
        Assert.Equal(["Owl hoots at midnight", "Wind in dark woods"], page.Items.Select(i => i.Name));
        Assert.Equal(4, page.Total);
        var paged = await search.SearchAsync(new SearchQuery { SimilarTo = ids["Haunted tree"] }, 2, 2, Ct);
        Assert.Equal(["Rain on roof", "Sword clang"], paged.Items.Select(i => i.Name));
    }
}
