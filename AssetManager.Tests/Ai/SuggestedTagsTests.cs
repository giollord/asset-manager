using AssetManager.AI.Enrichment;
using AssetManager.Core.Domain;
using AssetManager.Data.Services;
using AssetManager.Processing.Import;
using AssetManager.Tests.Data;
using AssetManager.Tests.Import;
using ImageMagick;
using Microsoft.EntityFrameworkCore;

namespace AssetManager.Tests.Ai;

/// <summary>Spec "Suggested tag vocabulary" (design D15).</summary>
public class SuggestedTagsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Built_in_list_matches_the_spec()
    {
        Assert.Equal(
        [
            "2d", "3d", "pixel art", "low poly", "hand-painted", "realistic", "stylized", "cartoon", "top-down", "isometric", "side-view",
            "ui", "icon", "tileset", "character", "environment", "prop", "vfx", "seamless", "pbr", "animated", "rigged",
        ], SuggestedTags.Default);
    }

    [Fact]
    public void Synonyms_fold_onto_the_suggested_tag()
    {
        Assert.Equal(["low poly"], SuggestedTags.Fold(["lowpoly"], SuggestedTags.Default));
        Assert.Equal(["low poly"], SuggestedTags.Fold(["low-poly"], SuggestedTags.Default));
        Assert.Equal(["low poly"], SuggestedTags.Fold(["low_poly"], SuggestedTags.Default));
        Assert.Equal(["low poly"], SuggestedTags.Fold(["low-poly style", "lowpoly"], SuggestedTags.Default));
        Assert.Equal(["hand-painted", "top-down", "barrel"], SuggestedTags.Fold(["handpainted", "top down", "barrel"], SuggestedTags.Default));
        // Without a vocabulary, tags pass through.
        Assert.Equal(["lowpoly"], SuggestedTags.Fold(["lowpoly"], null));
    }

    [Fact]
    public void Item_answer_is_folded()
    {
        var folded = ItemPass.Fold(new ItemResult { Tags = ["Lowpoly", "rock"], Style = ["Pixel-Art"] }, SuggestedTags.Default);
        Assert.Equal(["low poly", "rock"], folded.Tags);
        Assert.Equal(["pixel art"], folded.Style);
    }

    [Fact]
    public async Task Catalog_setting_defaults_saves_normalized_and_resets()
    {
        await using var c = await TestCatalog.CreateAsync();
        var settings = new CatalogSettingsService(c.Store);
        Assert.Equal(SuggestedTags.Default, await settings.GetSuggestedTagsAsync(Ct));

        await settings.SetSuggestedTagsAsync([.. SuggestedTags.Default, "  Voxel "], Ct);
        Assert.Equal("voxel", (await settings.GetSuggestedTagsAsync(Ct))[^1]);

        await settings.ResetSuggestedTagsAsync(Ct);
        Assert.Equal(SuggestedTags.Default, await settings.GetSuggestedTagsAsync(Ct));
    }

    [Fact]
    public async Task Import_prompts_list_the_catalogs_suggested_tags()
    {
        var client = new SchemaAwareChatClient();
        await using var kit = await ImportKit.CreateAsync(client);
        await new CatalogSettingsService(kit.Store).SetSuggestedTagsAsync(["voxel", "low poly"], Ct);
        var folder = kit.Catalog.Dir.Write("Voxels/cube.png", ImportStagingTests.Png(MagickColors.Orange, 32));
        kit.Catalog.Dir.Write("Voxels/cube2.png", ImportStagingTests.Png(MagickColors.Blue, 32));

        await kit.CatalogAsync(new ImportRequest([Path.GetDirectoryName(folder)!], Depth: AiDepth.Full));

        var line = SuggestedTags.PromptLine(["voxel", "low poly"]);
        Assert.Contains(client.Prompts, p => p.Contains("Summarize this game asset pack") && p.Contains(line));
        Assert.Contains(client.Prompts, p => p.Contains("game asset") && p.Contains("Item 1") && p.Contains(line));
        await using var db = kit.Catalog.Db();
        Assert.Equal(2, await db.Assets.CountAsync(a => a.AiStatus == AiStatus.Enriched, Ct));
    }
}
