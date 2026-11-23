using AssetManager.AI.Import;
using AssetManager.Core.Domain;
using AssetManager.Core.Jobs;
using AssetManager.Processing.Import;
using AssetManager.Shared.Components;
using AssetManager.Tests.Import;
using Bunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace AssetManager.Tests.Ui;

/// <summary>The import wizard's review step against the real import stack and a fake model.</summary>
public class ImportReviewTests : BunitContext
{
    private void Use(ImportKit kit)
    {
        Services.AddMudServices();
        Services.AddSingleton(kit.Review);
        Services.AddSingleton<IProgressHub>(kit.Hub);
        Services.AddLogging();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private static IRenderedComponent<MudBlazor.MudCheckBox<bool>> CheckBoxOf(IRenderedComponent<ImportReview> review, string name) =>
        review.FindComponents<AssetCard>().Single(c => c.Instance.Name == name).FindComponent<MudBlazor.MudCheckBox<bool>>();

    [Fact]
    public async Task Ai_correction_on_selection_reenriches_only_the_selected_items()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var chat = new SchemaAwareChatClient();
        await using var kit = await ImportKit.CreateAsync(chat);
        Use(kit);
        var folder = await ImportStagingTests.PackFolderAsync(kit.Catalog.Dir);
        var (sessionId, import) = await kit.ImportAsync(new ImportRequest([folder]));
        Assert.True(import.State == JobState.Completed, import.Error);

        var review = Render<ImportReview>(p => p.Add(r => r.SessionId, sessionId));
        review.WaitForAssertion(() => Assert.Equal(3, review.FindComponents<AssetCard>().Count));
        await review.InvokeAsync(() => CheckBoxOf(review, "Coin").Instance.ValueChanged.InvokeAsync(true));
        await review.InvokeAsync(() => CheckBoxOf(review, "Grass_Albedo").Instance.ValueChanged.InvokeAsync(true));
        review.WaitForAssertion(() => Assert.Equal("2 selected", review.Find("#selected-count").TextContent));

        chat.Flavor = "On gravel.";
        review.Find("input#ai-correction").Input("these are footsteps on gravel, not rain");
        review.Find("#ask-ai").Click();

        await using (var db = kit.Catalog.Db())
        {
            var job = await db.Jobs.SingleAsync(j => j.Type == "ai.enrich", ct);
            var payload = System.Text.Json.JsonSerializer.Deserialize<EnrichPayload>(job.PayloadJson, JobJson.Options)!;
            var selected = await db.AllAssets.Where(a => a.Name == "Coin" || a.Name == "Grass_Albedo").Select(a => a.Id).ToListAsync(ct);
            Assert.Equal(selected.Order(), payload.AssetIds.Order());             // only the selection
            Assert.Equal("these are footsteps on gravel, not rain", payload.Correction);
            var done = await kit.Jobs.WaitAsync(job.Id, ct);
            Assert.True(done.State == JobState.Completed && done.Errors == 0, done.Error);
        }
        Assert.Contains(chat.Prompts, p => p.Contains("Correction from the user for these items: these are footsteps on gravel, not rain"));
        await using (var db = kit.Catalog.Db())
        {
            var assets = await db.AllAssets.Where(a => a.ImportSessionId == sessionId).ToDictionaryAsync(a => a.Name, ct);
            Assert.EndsWith("On gravel.", assets["Coin"].Description);
            Assert.EndsWith("On gravel.", assets["Grass_Albedo"].Description);
            Assert.Equal("Described beep.", assets["beep"].Description);          // not selected: unchanged
        }
    }

    [Fact]
    public async Task Exclude_item_from_the_review_is_not_committed()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await using var kit = await ImportKit.CreateAsync();
        Use(kit);
        var folder = await ImportStagingTests.PackFolderAsync(kit.Catalog.Dir);
        var (sessionId, _) = await kit.ImportAsync(new ImportRequest([folder], Depth: AiDepth.None));

        var review = Render<ImportReview>(p => p.Add(r => r.SessionId, sessionId));
        review.WaitForAssertion(() => Assert.Equal(3, review.FindComponents<AssetCard>().Count));
        await review.InvokeAsync(() => CheckBoxOf(review, "Coin").Instance.ValueChanged.InvokeAsync(true));
        review.Find("#exclude-selected").Click();
        review.WaitForAssertion(() => Assert.Single(review.FindAll(".excluded-badge")));
        Assert.Contains("2 included", review.Find("#review-summary").TextContent);

        var commit = await kit.Jobs.WaitAsync(await kit.Imports.ConfirmAsync(sessionId, ct), ct);
        Assert.True(commit.State == JobState.Completed, commit.Error);
        await using var db = kit.Catalog.Db();
        Assert.Equal(["beep", "Grass_Albedo"], (await db.Assets.Select(a => a.Name).ToListAsync(ct)).Order(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Inline_edit_and_tags_are_user_edits()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await using var kit = await ImportKit.CreateAsync();
        Use(kit);
        var folder = await ImportStagingTests.PackFolderAsync(kit.Catalog.Dir);
        var (sessionId, _) = await kit.ImportAsync(new ImportRequest([folder], Depth: AiDepth.None));
        var review = Render<ImportReview>(p => p.Add(r => r.SessionId, sessionId));
        review.WaitForAssertion(() => Assert.Equal(3, review.FindComponents<AssetCard>().Count));

        var coin = review.FindComponents<AssetCard>().Single(c => c.Instance.Name == "Coin");
        var coinId = coin.Instance.AssetId;
        await review.InvokeAsync(() => coin.Instance.OnOpen.InvokeAsync(coinId));
        review.Find("input#edit-name").Change("Gold Coin");
        review.Find("textarea#edit-description").Change("A spinning coin.");
        review.Find("#edit-save").Click();
        review.WaitForAssertion(() => Assert.Contains(review.FindComponents<AssetCard>(), c => c.Instance.Name == "Gold Coin"));
        await kit.Review.AddTagAsync([coinId], "pickup", ct);

        await using var db = kit.Catalog.Db();
        var asset = await db.AllAssets.Include(a => a.Tags).ThenInclude(t => t.Tag).SingleAsync(a => a.Id == coinId, ct);
        Assert.Equal(("Gold Coin", "A spinning coin."), (asset.Name, asset.Description));
        Assert.True(asset.UserEdited.HasFlag(EditedFields.Name) && asset.UserEdited.HasFlag(EditedFields.Description));
        Assert.Contains(asset.Tags, t => t.Tag.Name == "pickup" && t.Origin == TagOrigin.User);
    }
    [Fact]
    public async Task Regrouping_merges_and_splits_staged_items()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await using var kit = await ImportKit.CreateAsync();
        var folder = await ImportStagingTests.PackFolderAsync(kit.Catalog.Dir);
        var (sessionId, _) = await kit.ImportAsync(new ImportRequest([folder], ["forest"], Depth: AiDepth.None));
        var items = (await kit.Review.LoadAsync(sessionId, ct))!.Sources.SelectMany(s => s.Items).ToDictionary(i => i.Name);

        await kit.Review.MergeAsync(items["Grass_Albedo"].Id, [items["Grass_Albedo"].Id, items["Coin"].Id], ct);

        var merged = (await kit.Review.LoadAsync(sessionId, ct))!.Sources.SelectMany(s => s.Items).ToList();
        Assert.DoesNotContain(merged, i => i.Name == "Coin");
        var grass = merged.Single(i => i.Name == "Grass_Albedo");
        var coinFile = grass.Files.Single(f => f.Role == FileRole.Companion);
        Assert.EndsWith("Coin.png", coinFile.RelativePath);

        var newId = await kit.Review.SplitAsync(coinFile.Id, ct);

        var split = (await kit.Review.LoadAsync(sessionId, ct))!.Sources.SelectMany(s => s.Items).ToList();
        var coin = split.Single(i => i.Id == newId);
        Assert.Equal(("Coin", AssetType.Image, 0), (coin.Name, coin.Type, coin.Companions));
        Assert.Contains(coin.Tags, t => t.Name == "forest" && t.Origin == TagOrigin.Import);   // shared tags follow
        Assert.Equal(0, split.Single(i => i.Name == "Grass_Albedo").Companions);
        Assert.NotNull(await kit.Review.PreviewAsync(newId, ct));                               // previews generated again
    }
}
