using AssetManager.Core.Domain;
using AssetManager.Data.Services;
using Microsoft.EntityFrameworkCore;

namespace AssetManager.Tests.Data;

/// <summary>Spec "Credits report" and "Unknown licenses first in credits" (design D9).</summary>
public class CreditsReportTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A collection "Forest" over assets from three sources; "Mystery Pack" has no known license.</summary>
    private static async Task<(TestCatalog Catalog, long CollectionId)> SeedAsync(bool detached = false)
    {
        var c = await TestCatalog.CreateAsync();
        void Detach(Source s)
        {
            if (!detached) return;
            s.Location = new Location("VOL-GONE", @"\Packs\" + s.DisplayName);
            s.VolumeId = "VOL-GONE";
        }
        await TestData.SourceAsync(c, 2, s =>
        {
            s.DisplayName = "Kenney Nature";
            s.License = new LicenseInfo { Kind = "CC0", Commercial = TriState.Yes, Attribution = TriState.No };
            s.Links = [new LinkInfo("https://kenney.nl/assets/nature-kit", "Nature Kit")];
            Detach(s);
        }, i => new Asset { Name = $"tree_{i}", Type = AssetType.Model3D, Tags = [] });
        await TestData.SourceAsync(c, 2, s =>
        {
            s.DisplayName = "Forest Sounds";
            s.License = new LicenseInfo { Kind = "CC-BY 4.0", Commercial = TriState.Yes, Attribution = TriState.Yes, AttributionText = "Forest Sounds by Jane Doe, used under CC-BY 4.0" };
            s.Links = [new LinkInfo("https://example.org/forest", null)];
            Detach(s);
        }, i => i == 0
            ? new Asset { Name = "birds, morning", Type = AssetType.Audio }
            : new Asset
            {
                Name = "owl", Type = AssetType.Audio, HasLicenseOverride = true,
                LicenseOverride = new LicenseInfo { Kind = "CC0", Commercial = TriState.Yes, Attribution = TriState.No },
            });
        await TestData.SourceAsync(c, 1, s =>
        {
            s.DisplayName = "Mystery Pack";
            s.Store = new StoreInfo { Publisher = "Some Publisher" };
            Detach(s);
        }, _ => new Asset { Name = "moss", Type = AssetType.Texture });

        await using var db = c.Db();
        var tag = new Tag { Name = "forest" };
        foreach (var a in await db.Assets.ToListAsync(Ct)) a.Tags.Add(new AssetTag { Tag = tag, Origin = TagOrigin.User });
        var collection = new Collection { Name = "Forest", IncludeTags = ["forest"], CreatedAt = DateTime.UtcNow };
        db.Collections.Add(collection);
        await db.SaveChangesAsync(Ct);
        return (c, collection.Id);
    }

    private static async Task<CreditsModel> ForCollectionAsync(TestCatalog c, long collectionId)
    {
        var credits = new CreditsService(c.Store, new CollectionService(c.Store));
        return await credits.BuildAsync("Forest", await credits.CollectionIdsAsync(collectionId, Ct), Ct);
    }

    [Fact]
    public async Task Credits_for_a_collection_as_markdown()
    {
        var (c, id) = await SeedAsync();
        await using var _ = c;
        var md = CreditsReport.Render(await ForCollectionAsync(c, id), CreditsFormat.Markdown);

        var lines = md.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        Assert.Equal("# Credits: Forest", lines[0]);
        // The file starts with the warning naming the unknown-license source, before any source section.
        var warning = lines.IndexOf("## ⚠ License unknown");
        Assert.True(warning > 0 && warning < lines.FindIndex(l => l.StartsWith("## ") && !l.Contains('⚠')));
        Assert.Contains("- **Mystery Pack** (1): moss", lines);
        // One section per source, unknown first, then by name.
        Assert.Equal(["## Mystery Pack", "## Forest Sounds", "## Kenney Nature"], lines.Where(l => l.StartsWith("## ") && !l.Contains('⚠')));
        Assert.Contains("- License: CC-BY 4.0 · commercial use: allowed · attribution: required", lines);
        Assert.Contains("- Attribution: Forest Sounds by Jane Doe, used under CC-BY 4.0", lines);
        Assert.Contains("- Author: Jane Doe", lines); // from the attribution text
        Assert.Contains("- Author: Some Publisher", lines); // the store publisher
        Assert.Contains("- Link: <https://example.org/forest>", lines);
        Assert.Contains("- Link: [Nature Kit](https://kenney.nl/assets/nature-kit)", lines);
        Assert.Contains("- Assets (1): birds, morning", lines);
        Assert.Contains("  - owl: CC0 · commercial use: allowed · attribution: not required", lines); // its own license
        Assert.Contains("- License: unknown · commercial use: unknown · attribution: unknown", lines);
    }

    [Fact]
    public async Task Credits_for_a_collection_as_text_and_csv()
    {
        var (c, id) = await SeedAsync();
        await using var _ = c;
        var model = await ForCollectionAsync(c, id);

        var text = CreditsReport.Render(model, CreditsFormat.Text);
        Assert.StartsWith("Credits: Forest\r\n===============", text.ReplaceLineEndings("\r\n"));
        Assert.True(text.IndexOf("WARNING: license unknown", StringComparison.Ordinal) < text.IndexOf("Kenney Nature", StringComparison.Ordinal));
        Assert.Contains("  * Mystery Pack (1): moss", text);
        Assert.Contains("Own license: owl: CC0", text);
        Assert.Contains("Link:        https://kenney.nl/assets/nature-kit (Nature Kit)", text);

        var csv = CreditsReport.Render(model, CreditsFormat.Csv).ReplaceLineEndings("\n").TrimEnd('\n').Split('\n');
        Assert.Equal("Asset,Type,Source,License from,License,Commercial use,Attribution,Attribution text,Author,Links", csv[0]);
        Assert.Equal(6, csv.Length); // one row per asset
        Assert.Equal("moss,Texture,Mystery Pack,source,unknown,unknown,unknown,,Some Publisher,", csv[1]);
        Assert.Contains("\"birds, morning\",Audio,Forest Sounds,source,CC-BY 4.0,allowed,required,\"Forest Sounds by Jane Doe, used under CC-BY 4.0\",Jane Doe,https://example.org/forest", csv);
        Assert.Contains("owl,Audio,Forest Sounds,asset,CC0,allowed,not required,,Jane Doe,https://example.org/forest", csv);
    }

    [Fact]
    public async Task Credits_with_every_source_unavailable_are_the_same()
    {
        var (attached, a) = await SeedAsync();
        var (detached, d) = await SeedAsync(detached: true);
        await using var _ = attached;
        await using var __ = detached;
        var when = new DateTime(2026, 11, 25);

        foreach (var format in Enum.GetValues<CreditsFormat>())
        {
            var on = CreditsReport.Render((await ForCollectionAsync(attached, a)) with { CreatedAt = when }, format);
            var off = CreditsReport.Render((await ForCollectionAsync(detached, d)) with { CreatedAt = when }, format);
            Assert.Equal(on, off);
        }

        // Saving writes a new file each time (never overwriting).
        var credits = new CreditsService(detached.Store, new CollectionService(detached.Store));
        var ids = await credits.CollectionIdsAsync(d, Ct);
        var first = await credits.SaveAsync("Forest", ids, CreditsFormat.Markdown, detached.Dir.Path, Ct);
        var second = await credits.SaveAsync("Forest", ids, CreditsFormat.Markdown, detached.Dir.Path, Ct);
        Assert.Equal(["Credits - Forest.md", "Credits - Forest (2).md"], new[] { first, second }.Select(Path.GetFileName));
        Assert.StartsWith("# Credits: Forest", await File.ReadAllTextAsync(first, Ct));
    }
}
