using AssetManager.Core.Domain;
using AssetManager.Core.Search;
using AssetManager.Data.Search;
using AssetManager.Processing.Handlers;
using AssetManager.Processing.Import;
using AssetManager.Processing.Settings;
using AssetManager.Shared.Components;
using AssetManager.Shared.Services;
using AssetManager.Tests.Import;
using Bunit;
using ImageMagick;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using static AssetManager.Tests.Fixtures.ModelFixtures;

namespace AssetManager.Tests.Ui;

/// <summary>The details drawer's "Textures" section (spec "Reviewing a model's textures", "User texture edits are kept", design D20).</summary>
public sealed class ModelTexturesUiTests : BunitContext
{
    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private static void Png(string path, string color)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var img = new MagickImage(new MagickColor(color), 64, 64);
        img.Write(path, MagickFormat.Png);
    }

    /// <summary>A barrel whose material "Wood" references nothing, next to two textures without a name match.</summary>
    private async Task<(ImportKit Kit, TextureMatcher Matcher, long BarrelId)> SetUpAsync()
    {
        var kit = await ImportKit.CreateAsync();
        var pack = Path.Combine(kit.Catalog.Dir.Path, "Props");
        Write(kit.Catalog.Dir, "Props/Models/Barrel.fbx", Box(new BoxOptions { Material = "Wood" }), "fbx");
        Png(Path.Combine(pack, "Textures", "Oak_Staves.png"), "#a07040");
        Png(Path.Combine(pack, "Textures", "Pine_Planks.png"), "#806030");
        await kit.CatalogAsync(new ImportRequest([pack], Depth: AiDepth.None));
        var settings = new PreviewSettings(kit.Store);
        var matcher = new TextureMatcher(kit.Store, kit.Processing.Resolver, settings);
        Services.AddMudServices();
        Services.AddSingleton(kit.Store);
        Services.AddSingleton(new ModelTextureService(kit.Store, kit.Processing.Resolver, kit.Previews, matcher));
        Services.AddScoped<UiActions>();
        Services.AddLogging();
        JSInterop.Mode = JSRuntimeMode.Loose;
        await using var db = kit.Catalog.Db();
        return (kit, matcher, await db.Assets.Where(a => a.Name == "Barrel").Select(a => a.Id).SingleAsync(Ct));
    }

    /// <summary>Scenario "Choose a texture".</summary>
    [Fact]
    public async Task Choose_a_texture_for_a_material()
    {
        var (kit, _, id) = await SetUpAsync();
        await using var _ = kit;
        var changed = 0;
        var panel = Render<ModelTextures>(p => p.Add(x => x.AssetId, id).Add(x => x.OnChanged, () => changed++));
        panel.WaitForAssertion(() => Assert.NotNull(panel.Find("#textures-none")));

        panel.Find("#texture-choose").Click();
        panel.WaitForAssertion(() => Assert.NotNull(panel.Find("#texture-choose-form")));
        // Pick the texture through the component's state (MudSelect popovers are not rendered by bUnit).
        var choices = await Services.GetRequiredService<ModelTextureService>().ChoicesAsync(id, Ct);
        Assert.Equal(["Textures/Oak_Staves.png", "Textures/Pine_Planks.png"], choices.Select(c => c.RelativePath.Replace('\\', '/')));
        await panel.InvokeAsync(() => panel.FindComponents<MudBlazor.MudSelect<long>>().Single().Instance.SelectedValuesChanged.InvokeAsync([choices[0].FileId]));
        await panel.InvokeAsync(() => panel.FindComponents<MudBlazor.MudSelect<long>>().Single().Instance.ValueChanged.InvokeAsync(choices[0].FileId));
        panel.Find("#texture-choose-attach").Click();

        panel.WaitForAssertion(() => Assert.Equal("Oak_Staves.png", panel.Find(".texture-row").QuerySelector(".selectable")!.TextContent));
        Assert.Equal("user", panel.Find(".texture-origin").TextContent.Trim());
        Assert.Equal(1, changed);
        await using var db = kit.Catalog.Db();
        var barrel = await db.Assets.Include(a => a.Files).SingleAsync(a => a.Id == id, Ct);
        Assert.True(barrel.UserEdited.HasFlag(EditedFields.Textures));
        Assert.Contains(barrel.Files, f => (f.FileName, f.Material, f.Slot, f.Origin) == ("Oak_Staves.png", "Wood", "map", CompanionOrigin.User));
    }

    /// <summary>Scenario "Wrong AI match": detaching re-renders and keeps texture matching away.</summary>
    [Fact]
    public async Task Detaching_an_AI_match_marks_the_textures_as_edited()
    {
        var (kit, matcher, id) = await SetUpAsync();
        await using var _ = kit;
        await using (var db = kit.Catalog.Db())
        {
            var barrel = await db.Assets.SingleAsync(a => a.Id == id, Ct);
            var crate = (await matcher.SourceTexturesAsync(id, Ct)).Single(c => c.FileName == "Pine_Planks.png");
            await matcher.AttachAsync(barrel, "Wood", crate.FileId, CompanionOrigin.Ai, null, Ct);
        }
        var search = new AssetSearchService(kit.Store);
        Assert.Single((await search.SearchAsync(new SearchQuery { TexturesMatchedByAi = true }, 0, 10, Ct)).Items);
        var panel = Render<ModelTextures>(p => p.Add(x => x.AssetId, id));
        panel.WaitForAssertion(() => Assert.Equal("AI", panel.Find(".texture-origin").TextContent.Trim()));

        panel.Find(".texture-detach").Click();

        panel.WaitForAssertion(() => Assert.NotNull(panel.Find("#textures-none")));
        Assert.NotNull(panel.Find("#textures-edited"));
        Assert.Empty((await search.SearchAsync(new SearchQuery { TexturesMatchedByAi = true }, 0, 10, Ct)).Items);
        Assert.Empty(await matcher.PlanAsync(id, Ct)); // re-enrichment will not match it again
    }

    [Fact]
    public async Task Suggestion_can_be_applied_or_dismissed()
    {
        var (kit, matcher, id) = await SetUpAsync();
        await using var _ = kit;
        var oak = (await matcher.SourceTexturesAsync(id, Ct)).Single(c => c.FileName == "Oak_Staves.png");
        await using (var db = kit.Catalog.Db())
        {
            var barrel = await db.Assets.SingleAsync(a => a.Id == id, Ct);
            barrel.Model = new ModelMetadata { TextureSuggestions = [new TextureSuggestion("Wood", oak.FileId, oak.RelativePath, 0.55, "staves match")] };
            await db.SaveChangesAsync(Ct);
        }
        var panel = Render<ModelTextures>(p => p.Add(x => x.AssetId, id));
        panel.WaitForAssertion(() => Assert.Contains("Oak_Staves.png", panel.Find(".texture-suggestion").TextContent));
        Assert.Contains("55", panel.Find(".texture-suggestion").TextContent);

        panel.Find(".texture-apply").Click();

        panel.WaitForAssertion(() => Assert.Empty(panel.FindAll(".texture-suggestion")));
        Assert.Equal("Oak_Staves.png", panel.Find(".texture-row .selectable").TextContent);
    }
}
