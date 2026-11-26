using System.Diagnostics;
using AssetManager.Core.Domain;
using AssetManager.Core.Search;
using AssetManager.Data.Search;
using AssetManager.Data.Services;

namespace AssetManager.Tests.Data;

public class CatalogStatsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Totals_shown()
    {
        await using var c = await TestCatalog.CreateAsync();
        // 1,200 images, 300 sounds and 80 models from 15 sources.
        for (var s = 0; s < 15; s++)
        {
            var images = s < 12 ? 100 : 0;
            var sounds = s is >= 12 and < 15 ? 100 : 0;
            var models = s < 8 ? 10 : 0;
            await TestData.SourceAsync(c, images + sounds + models, src => src.DisplayName = $"Pack {s}", i => new Asset
            {
                Name = $"a{i}", Type = i < images ? AssetType.Image : i < images + sounds ? AssetType.Audio : AssetType.Model3D,
            });
        }
        var stats = await new CatalogStatsService(c.Store, new AssetSearchService(c.Store)).GetAsync(Ct);

        Assert.Equal(1580, stats.Assets);
        Assert.Equal([("Image", 1200), ("Audio", 300), ("Model3D", 80)], stats.ByType.Select(t => (t.Label, t.Count)));
        Assert.Equal(15, stats.Sources);
        Assert.True(stats.Size.Total > 0 && stats.Size.Other > 0);
    }

    [Fact]
    public async Task Every_drill_down_count_equals_its_browse_total()
    {
        await using var c = await TestCatalog.CreateAsync();
        await TestData.SourceAsync(c, 6, s => s.License = new LicenseInfo { Commercial = TriState.Yes, Attribution = TriState.Yes }, i => new Asset
        {
            Name = $"m{i}", Type = AssetType.Model3D, AiStatus = (AiStatus)(i % 4), ScaleAnomaly = i == 1 ? ScaleAnomaly.TooLarge : ScaleAnomaly.None,
            OrientationUncertain = i == 2, TexturesMissing = i == 3, IsStored = i == 4,
            HasLicenseOverride = i == 5, LicenseOverride = new LicenseInfo { Commercial = i == 5 ? TriState.No : TriState.Unknown },
        });
        await TestData.SourceAsync(c, 3, make: i => new Asset { Name = $"img{i}", Type = AssetType.Image, AiStatus = AiStatus.Failed });
        var search = new AssetSearchService(c.Store);
        var stats = await new CatalogStatsService(c.Store, search).GetAsync(Ct);

        var all = stats.ByType.Concat(stats.Enrichment).Concat(stats.Availability).Concat(stats.License).Concat(stats.ModelIssues).ToList();
        foreach (var count in all)
            Assert.True(count.Count == (await search.SearchAsync(count.Query, 0, 1, Ct)).Total, $"{count.Label}: {count.Count}");
        Assert.Equal(4, stats.Enrichment.Single(s => s.Label == "Failed enrichment").Count);   // the drill-down lists exactly these
        Assert.Equal((5, 1, 3), (stats.License[0].Count, stats.License[1].Count, stats.License[2].Count));
        Assert.Equal(1, stats.ModelIssues.Single(s => s.Label == "Scale anomaly").Count);
        Assert.Equal(1, stats.Availability.Single(s => s.Label == "Stored in catalog").Count);
    }

    [Fact]
    public async Task Collection_count_follows_tags_and_equals_browse()
    {
        await using var c = await TestCatalog.CreateAsync();
        var src = await TestData.SourceAsync(c, 10, make: i => new Asset { Name = $"t{i}", Type = i < 8 ? AssetType.Model3D : AssetType.Image });
        var ids = src.Assets.Select(a => a.Id).ToList();
        var tags = new TagService(c.Store);
        await tags.AddTagsAsync(ids.Take(3).ToList(), ["tree"], TagOrigin.User, Ct);
        await new CollectionService(c.Store).SaveAsync(new Collection { Name = "Trees", IncludeTags = ["tree"] }, Ct);
        var search = new AssetSearchService(c.Store);
        var stats = new CatalogStatsService(c.Store, search);
        Assert.Equal(3, (await stats.CollectionsAsync(Ct)).Single().Count);

        await tags.AddTagsAsync(ids.Skip(3).Take(5).ToList(), ["tree"], TagOrigin.User, Ct);   // 5 more assets get the tag

        var trees = (await stats.CollectionsAsync(Ct)).Single();
        Assert.Equal(8, trees.Count);
        Assert.Equal(trees.Count, (await search.SearchAsync(trees.Query, 0, 1, Ct)).Total);
        Assert.Equal([("Model3D", 8)], trees.ByType.Select(t => (t.Label, t.Count)));
        Assert.Equal(("tree", 8), (await stats.TagsAsync(Ct)).Select(t => (t.Name, t.Count)).Single());
    }

    [Fact]
    public async Task Size_breakdown_counts_previews_and_source_counts_include_nested_sources()
    {
        await using var c = await TestCatalog.CreateAsync();
        var outer = await TestData.SourceAsync(c, 2, s => s.DisplayName = "Folder");
        await TestData.SourceAsync(c, 3, s => { s.DisplayName = "Inner"; s.ParentSourceId = outer.Id; });
        await using (var db = c.Db())
        {
            db.Previews.Add(new PreviewBlob { AssetId = outer.Assets[0].Id, Kind = PreviewKind.Thumbnail, Mime = "image/webp", Bytes = new byte[200_000] });
            await db.SaveChangesAsync(Ct);
        }
        var stats = new CatalogStatsService(c.Store, new AssetSearchService(c.Store));

        var size = (await stats.GetAsync(Ct)).Size;
        Assert.InRange(size.Previews, 200_000, 300_000);
        Assert.Equal(size.Total, size.Previews + size.StoredOriginals + size.Other);
        Assert.Equal([("Folder", 5), ("Inner", 3)], (await stats.SourcesAsync(Ct)).Select(s => (s.Name, s.Count)));
    }
}

/// <summary>Timing tests run alone, after the parallel ones, so other tests' load does not distort them.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PerformanceCollection
{
    public const string Name = "Performance";
}

/// <summary>200k-asset catalog (spec "Large catalog", design: Stats under 1 s).</summary>
[Collection(PerformanceCollection.Name)]
public class LargeCatalogPerformanceTests(ITestOutputHelper output)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Stats_and_first_tag_filtered_page_at_200k_assets()
    {
        await using var c = await TestCatalog.CreateAsync();
        await BulkData.SeedAsync(c, 200_000);
        await new CollectionService(c.Store).SaveAsync(new Collection { Name = "Trees", IncludeTags = ["tree"], ExcludeTags = ["winter"] }, Ct);
        var search = new AssetSearchService(c.Store);
        await search.SearchAsync(new SearchQuery(), 0, 1, Ct); // warm up the connection and EF model

        var sw = Stopwatch.StartNew();
        var page = await search.SearchAsync(new SearchQuery { IncludeTags = ["tree"] }, 0, 100, Ct);
        var pageTime = sw.Elapsed;
        Assert.Equal(100, page.Items.Count);
        Assert.Equal(20_000, page.Total);
        output.WriteLine($"first page of a tag filter: {pageTime.TotalMilliseconds:F0} ms");
        Assert.True(pageTime < TimeSpan.FromSeconds(1), $"first page took {pageTime}");

        var stats = new CatalogStatsService(c.Store, search);
        sw.Restart();
        var totals = await stats.GetAsync(Ct);
        var collections = await stats.CollectionsAsync(Ct);
        var sources = await stats.SourcesAsync(Ct);
        var tags = await stats.TagsAsync(Ct);
        var statsTime = sw.Elapsed;
        output.WriteLine($"all stats: {statsTime.TotalMilliseconds:F0} ms");
        Assert.Equal(200_001, totals.Assets);
        Assert.True(collections.Single().Count > 0 && sources.Count == 101 && tags.Count == 2);
        Assert.True(statsTime < TimeSpan.FromSeconds(1), $"stats took {statsTime}");
    }

    // ---- license audit (spec "License audit") ----

    private static async Task<(TestCatalog Catalog, Dictionary<string, long> Sources)> AuditSeedAsync()
    {
        var c = await TestCatalog.CreateAsync();
        var ids = new Dictionary<string, long>();
        async Task Add(string name, int assets, LicenseInfo license, Func<int, Asset>? make = null) =>
            ids[name] = (await TestData.SourceAsync(c, assets, s => { s.DisplayName = name; s.License = license; }, make)).Id;
        await Add("Clean CC0", 4, new LicenseInfo { Kind = "CC0", Commercial = TriState.Yes, Attribution = TriState.No });
        await Add("No kind", 3, new LicenseInfo { Commercial = TriState.Yes, Attribution = TriState.No });
        await Add("Commercial unknown", 2, new LicenseInfo { Kind = "Custom", Commercial = TriState.Unknown });
        await Add("Needs credit", 5, new LicenseInfo { Kind = "CC-BY 4.0", Commercial = TriState.Yes, Attribution = TriState.Yes, AttributionText = "  " });
        await Add("Personal only", 2, new LicenseInfo { Kind = "Personal use", Commercial = TriState.No, Attribution = TriState.No });
        // One asset with its own, different license; one with an override equal to the source's.
        await Add("Mixed", 3, new LicenseInfo { Kind = "CC0", Commercial = TriState.Yes, Attribution = TriState.No }, i => new Asset
        {
            Name = $"m{i}",
            HasLicenseOverride = i < 2,
            LicenseOverride = i == 0
                ? new LicenseInfo { Kind = "CC-BY-NC", Commercial = TriState.No, Attribution = TriState.Yes, AttributionText = "by X" }
                : new LicenseInfo { Kind = "CC0", Commercial = TriState.Yes, Attribution = TriState.No },
        });
        return (c, ids);
    }

    [Fact]
    public async Task License_audit_lists_each_problem_with_sources_and_counts_matching_browse()
    {
        var (c, ids) = await AuditSeedAsync();
        await using var _ = c;
        var search = new AssetSearchService(c.Store);
        var audit = await new CatalogStatsService(c.Store, search).LicenseAuditAsync(Ct);

        var byProblem = audit.ToDictionary(p => p.Problem);
        Assert.Equal(["No kind", "Commercial unknown"], byProblem[LicenseProblem.UnknownLicense].Sources.Select(s => s.Name));
        Assert.Equal((2, 5), (byProblem[LicenseProblem.UnknownLicense].SourceCount, byProblem[LicenseProblem.UnknownLicense].AssetCount));
        Assert.Equal([("Needs credit", 5)], byProblem[LicenseProblem.AttributionMissing].Sources.Select(s => (s.Name, s.Assets)));
        Assert.Equal([("Personal only", 2), ("Mixed", 1)], byProblem[LicenseProblem.NonCommercial].Sources.Select(s => (s.Name, s.Assets)));
        Assert.Equal([("Mixed", 1)], byProblem[LicenseProblem.OverrideDiffers].Sources.Select(s => (s.Name, s.Assets)));
        Assert.Equal("Unknown license", byProblem[LicenseProblem.UnknownLicense].Label);

        // Every count equals what Browse lists for its drill-down.
        foreach (var p in audit)
        {
            Assert.Equal(p.AssetCount, await search.CountAsync(p.Query));
            foreach (var s in p.Sources) Assert.Equal(s.Assets, await search.CountAsync(s.Query));
        }
        Assert.Equal(ids["Needs credit"], byProblem[LicenseProblem.AttributionMissing].Sources[0].Query.SourceId);
    }

    [Fact]
    public async Task Missing_attribution_text_disappears_after_an_edit()
    {
        var (c, ids) = await AuditSeedAsync();
        await using var _ = c;
        var stats = new CatalogStatsService(c.Store, new AssetSearchService(c.Store));
        Assert.Contains((await stats.LicenseAuditAsync(Ct)).Single(p => p.Problem == LicenseProblem.AttributionMissing).Sources, s => s.Name == "Needs credit");

        await new MetadataEditService(c.Store).EditSourceAsync(ids["Needs credit"], new AssetManager.Core.Domain.SourceUpdate
        {
            License = new LicenseInfo { Kind = "CC-BY 4.0", Commercial = TriState.Yes, Attribution = TriState.Yes, AttributionText = "Art by Jane Doe (CC-BY 4.0)" },
        }, Ct);

        // The edit raises the catalog change, so the cached audit is recomputed.
        Assert.Empty((await stats.LicenseAuditAsync(Ct)).Single(p => p.Problem == LicenseProblem.AttributionMissing).Sources);
    }
}
