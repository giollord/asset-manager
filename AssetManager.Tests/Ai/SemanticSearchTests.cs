using AssetManager.AI.Enrichment;
using AssetManager.AI.Runtime;
using AssetManager.Core.Domain;
using AssetManager.Core.Search;
using AssetManager.Data.Search;
using AssetManager.Tests.Data;
using Microsoft.EntityFrameworkCore;
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

    // ---- search by image (design D6) ----

    private static byte[] Photo(string color)
    {
        using var img = new ImageMagick.MagickImage(new ImageMagick.MagickColor(color), 1200, 900); // larger than 768: resized
        return img.ToByteArray(ImageMagick.MagickFormat.Png);
    }

    private const string RainAnswer =
        """{"items":[{"index":1,"description":"Rain falling on a window.","type":"image","tags":["rain","window"],"colors":["blue"],"style":["realistic"],"is_game_asset":true}]}""";

    private static async Task<long> TotalRowsAsync(TestCatalog c)
    {
        await using var db = c.Db();
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync(Ct);
        var tables = new List<string>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND sql NOT LIKE 'CREATE VIRTUAL%' AND name NOT LIKE 'sqlite_%'";
            await using var r = await cmd.ExecuteReaderAsync(Ct);
            while (await r.ReadAsync(Ct)) tables.Add(r.GetString(0));
        }
        long total = 0;
        foreach (var t in tables)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT count(*) FROM \"{t}\"";
            total += Convert.ToInt64(await cmd.ExecuteScalarAsync(Ct));
        }
        return total;
    }

    [Fact]
    public async Task Search_by_image_describes_embeds_and_ranks_without_writing_the_catalog()
    {
        await using var c = await TestCatalog.CreateAsync();
        var (_, _) = await SeedAsync(c);
        var index = new InMemoryVectorIndex(c.Store);
        await index.RebuildAsync(Ct);
        var embeddings = new EmbeddingService(c.Store, index, () => new EmbeddingModel(new FixedGenerator(), "fixed"));
        var cache = new ImageQueryCache();
        var chat = new FakeChatClient(RainAnswer);
        var images = new ImageQueryService(embeddings, cache) { Chat = () => chat, Modalities = () => new AiModalities(true, false) };
        var search = new CatalogSearch(c.Store, new AssetSearchService(c.Store), embeddings, index, cache);
        var before = await TotalRowsAsync(c);

        var query = await images.CreateAsync(Photo("#3060C0"), Ct);
        var page = await search.SearchAsync(new SearchQuery { ImageQueryId = query.Id, Types = [AssetType.Audio] }, 0, 10, Ct);

        Assert.Single(chat.Requests); // one chat request per query
        Assert.Equal(["rain", "window"], query.Tags);
        Assert.Equal("Rain on roof", page.Items[0].Name); // the description "rain ..." embeds next to the rain sound
        Assert.Equal(4, page.Total); // the type filter still applies
        Assert.Contains("blue", query.Palette[0].Name);
        Assert.StartsWith("data:image/webp;base64,", query.ThumbnailDataUrl);
        Assert.Equal(before, await TotalRowsAsync(c)); // nothing written to the catalog
    }

    [Fact]
    public async Task Search_by_image_re_ranks_equal_meanings_by_palette()
    {
        await using var c = await TestCatalog.CreateAsync();
        var src = await TestData.SourceAsync(c, 2, make: i => new Asset
        {
            Name = i == 0 ? "Red puddle" : "Blue puddle", Type = AssetType.Texture,
            Colors = [i == 0 ? new ColorInfo("#C03020", "red", 1) : new ColorInfo("#2050C0", "blue", 1)],
        });
        var index = new InMemoryVectorIndex(c.Store);
        foreach (var a in src.Assets) await index.UpsertAsync(a.Id, V(0, 1, 0), "fixed", Ct);
        var embeddings = new EmbeddingService(c.Store, index, () => new EmbeddingModel(new FixedGenerator(), "fixed"));
        var cache = new ImageQueryCache();
        var search = new CatalogSearch(c.Store, new AssetSearchService(c.Store), embeddings, index, cache);
        var images = new ImageQueryService(embeddings, cache) { Chat = () => new FakeChatClient(RainAnswer), Modalities = () => new AiModalities(true, false) };

        var blue = await images.CreateAsync(Photo("#2858C8"), Ct);
        var red = await images.CreateAsync(Photo("#B82818"), Ct);

        Assert.Equal(["Blue puddle", "Red puddle"], (await search.SearchAsync(new SearchQuery { ImageQueryId = blue.Id }, 0, 10, Ct)).Items.Select(i => i.Name));
        Assert.Equal(["Red puddle", "Blue puddle"], (await search.SearchAsync(new SearchQuery { ImageQueryId = red.Id }, 0, 10, Ct)).Items.Select(i => i.Name));
    }

    [Fact]
    public async Task Expired_query_image_and_missing_models()
    {
        await using var c = await TestCatalog.CreateAsync();
        var (search, _) = await SeedAsync(c);
        await Assert.ThrowsAsync<ImageQueryExpiredException>(() => search.SearchAsync(new SearchQuery { ImageQueryId = "gone" }, 0, 10, Ct));

        var index = new InMemoryVectorIndex(c.Store);
        var noEmbedding = new EmbeddingService(c.Store, index, () => null);
        var images = new ImageQueryService(noEmbedding, new ImageQueryCache()) { Chat = () => new FakeChatClient(RainAnswer), Modalities = () => new AiModalities(true, false) };
        Assert.False(images.IsAvailable);
        await Assert.ThrowsAsync<InvalidOperationException>(() => images.CreateAsync(Photo("#808080"), Ct));
        var noVision = new ImageQueryService(new EmbeddingService(c.Store, index, () => new EmbeddingModel(new FixedGenerator(), "fixed")), new ImageQueryCache())
            { Chat = () => new FakeChatClient(RainAnswer), Modalities = () => new AiModalities(false, false) };
        Assert.False(noVision.IsAvailable);
    }

    [Fact]
    public void Query_cache_keeps_the_last_eight()
    {
        var cache = new ImageQueryCache();
        for (var i = 0; i < 9; i++) cache.Add(new ImageQuery($"q{i}", [1], [], "", [], ""));
        Assert.Equal(8, cache.Count);
        Assert.Null(cache.Get("q0"));
        Assert.NotNull(cache.Get("q1")); // now the most recent
        cache.Add(new ImageQuery("q9", [1], [], "", [], ""));
        Assert.NotNull(cache.Get("q1"));
        Assert.Null(cache.Get("q2"));
    }

    [Fact]
    public void Palette_similarity_and_rerank_score()
    {
        var (l, a, b) = PaletteSimilarity.Lab("#FFFFFF")!.Value;
        Assert.Equal((100.0, 0.0, 0.0), (Math.Round(l), Math.Round(a) + 0.0, Math.Round(b) + 0.0));
        var red = PaletteSimilarity.Lab("#FF0000")!.Value;
        Assert.Equal(53.24, red.L, 0.05);
        Assert.Equal(80.09, red.A, 0.05);
        Assert.Equal(67.20, red.B, 0.05);

        List<ColorInfo> blue = [new("#2050C0", "blue", 0.7), new("#FFFFFF", "white", 0.3)];
        Assert.Equal(1, PaletteSimilarity.Of(blue, blue));
        var nearBlue = PaletteSimilarity.Of(blue, [new("#2858C8", "blue", 1)])!.Value;
        var redOnly = PaletteSimilarity.Of(blue, [new("#C03020", "red", 1)])!.Value;
        Assert.True(nearBlue > redOnly, $"{nearBlue} vs {redOnly}");
        Assert.InRange(redOnly, 0, 1);
        Assert.Null(PaletteSimilarity.Of(blue, []));

        Assert.Equal(0.8 * 0.9 + 0.2 * 0.5, PaletteSimilarity.Score(0.9, 0.5), 10);
        Assert.Equal(0.9, PaletteSimilarity.Score(0.9, null));
        // A slightly worse meaning with the right colours overtakes: 0.8*0.90 + 0.2*0.95 > 0.8*0.92 + 0.2*0.30
        Assert.True(PaletteSimilarity.Score(0.90, 0.95) > PaletteSimilarity.Score(0.92, 0.30));
    }
}
