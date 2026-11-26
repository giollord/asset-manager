using AssetManager.Core.Domain;
using AssetManager.Processing.Handlers;
using AssetManager.Processing.Import;
using AssetManager.Processing.Models;
using AssetManager.Processing.Settings;
using AssetManager.Tests.Fixtures;
using AssetManager.Tests.Import;
using ImageMagick;
using Microsoft.EntityFrameworkCore;
using static AssetManager.Tests.Fixtures.ModelFixtures;

namespace AssetManager.Tests.Processing;

/// <summary>Texture matching candidates, renders and attaching (design D20, spec "Materials that need texture matching", "Texture candidates").</summary>
public class TextureMatcherTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Records each request and returns a real 8×8 WebP per view.</summary>
    internal sealed class CandidateRenderer : IModelRenderer
    {
        public List<ModelRenderRequest> Requests { get; } = [];

        public bool IsAvailable => true;

        public Task<IReadOnlyList<RenderedView>> RenderAsync(ModelRenderRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            using var img = new MagickImage(MagickColors.Orange, 8, 8);
            var webp = img.ToByteArray(MagickFormat.WebP);
            return Task.FromResult<IReadOnlyList<RenderedView>>(
                Enumerable.Range(0, request.Elevations.Count * request.Azimuths).Select(i => new RenderedView(i, 8, 8, webp)).ToList());
        }

        public Task<RenderedView> RenderProbeAsync(ProbeRequest request, CancellationToken ct = default) => throw new RendererUnavailableException();
    }

    private static void Png(string path, string color)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var img = new MagickImage(new MagickColor(color), 64, 64);
        img.Write(path, MagickFormat.Png);
    }

    /// <summary>A box without texture coordinates.</summary>
    private static Assimp.Scene NoUv(string material)
    {
        var scene = Box(new BoxOptions { Material = material });
        scene.Meshes[0].TextureCoordinateChannels[0].Clear();
        scene.Meshes[0].UVComponentCount[0] = 0;
        return scene;
    }

    private static async Task<(ImportKit Kit, TextureMatcher Matcher, CandidateRenderer Renderer, string Pack)> SetUpAsync(Action<string, TempDir> build)
    {
        var kit = await ImportKit.CreateAsync();
        var pack = Path.Combine(kit.Catalog.Dir.Path, "Pack");
        build(pack, kit.Catalog.Dir);
        await kit.CatalogAsync(new ImportRequest([pack], Depth: AiDepth.None));
        var renderer = new CandidateRenderer();
        return (kit, new TextureMatcher(kit.Store, kit.Processing.Resolver, new PreviewSettings(kit.Store), renderer), renderer, pack);
    }

    private static async Task<Asset> ModelAsync(ImportKit kit, string name)
    {
        await using var db = kit.Catalog.Db();
        return await db.Assets.Include(a => a.Files).SingleAsync(a => a.Name == name, Ct);
    }

    [Fact]
    public async Task Material_with_a_related_texture_is_planned()
    {
        var (kit, matcher, _, _) = await SetUpAsync((pack, dir) =>
        {
            Write(dir, "Pack/Models/Crate.fbx", Box(new BoxOptions { Material = "Wood" }), "fbx");
            Png(Path.Combine(pack, "Textures", "Wood_Planks.png"), "#a07040");
            Png(Path.Combine(pack, "Textures", "Stone_Wall.png"), "#909090");
        });
        await using var _ = kit;

        var plan = await matcher.PlanAsync((await ModelAsync(kit, "Crate")).Id, Ct);

        var m = Assert.Single(plan);
        Assert.Equal(("Wood", null), (m.Material, m.MissingReference));
        Assert.Equal(["Wood_Planks.png"], m.Candidates.Select(c => c.FileName)); // no shared word with Stone_Wall: no evidence
    }

    /// <summary>Scenario "Model without texture coordinates".</summary>
    [Fact]
    public async Task Model_without_texture_coordinates_is_not_matched()
    {
        var (kit, matcher, _, _) = await SetUpAsync((pack, dir) =>
        {
            Write(dir, "Pack/Models/Crate.fbx", NoUv("Wood"), "fbx");
            Png(Path.Combine(pack, "Textures", "Wood_Planks.png"), "#a07040");
        });
        await using var _ = kit;

        Assert.Empty(await matcher.PlanAsync((await ModelAsync(kit, "Crate")).Id, Ct));
    }

    /// <summary>Scenario "Normal map is not a colour candidate": by its name, and by its colour.</summary>
    [Fact]
    public async Task Normal_maps_are_not_colour_candidates()
    {
        var (kit, matcher, _, _) = await SetUpAsync((pack, dir) =>
        {
            Write(dir, "Pack/Models/SM_Rock.fbx", Box(new BoxOptions { Material = "Stone" }), "fbx");
            Png(Path.Combine(pack, "Textures", "T_Rock_Normal.png"), "#8080ff");
            Png(Path.Combine(pack, "Textures", "Rock_Detail.png"), "#7f84fa");
            Png(Path.Combine(pack, "Textures", "Rock_Surface.png"), "#807060");
        });
        await using var _ = kit;

        var m = Assert.Single(await matcher.PlanAsync((await ModelAsync(kit, "SM_Rock")).Id, Ct));
        Assert.Equal(["Rock_Surface.png"], m.Candidates.Select(c => c.FileName));
    }

    /// <summary>Scenario "Untextured low-poly pack": material colours, no references, images named after other things.</summary>
    [Fact]
    public async Task Untextured_models_without_evidence_are_left_alone()
    {
        var (kit, matcher, renderer, _) = await SetUpAsync((pack, dir) =>
        {
            Write(dir, "Pack/Models/wallPillar.fbx", Box(new BoxOptions { Material = "stone" }), "fbx");
            Png(Path.Combine(pack, "Isometric", "towerSquare_NE.png"), "#d8d4c0");
            Png(Path.Combine(pack, "Preview.png"), "#202020");
        });
        await using var _ = kit;

        Assert.Empty(await matcher.PlanAsync((await ModelAsync(kit, "wallPillar")).Id, Ct));
        Assert.Empty(renderer.Requests);
    }

    [Fact]
    public async Task A_missing_reference_considers_textures_without_evidence()
    {
        var (kit, matcher, _, _) = await SetUpAsync((pack, dir) =>
        {
            Write(dir, "Pack/Models/Barn.fbx", Box(new BoxOptions { Material = "Material", DiffuseTexture = @"C:\Users\artist\Desktop\House2Text.jpg" }), "fbx");
            Png(Path.Combine(pack, "Textures", "Color 2.png"), "#b05030");
        });
        await using var _ = kit;

        var m = Assert.Single(await matcher.PlanAsync((await ModelAsync(kit, "Barn")).Id, Ct));
        Assert.Equal(("Material", "House2Text.jpg"), (m.Material, m.MissingReference));
        Assert.Equal(["Color 2.png"], m.Candidates.Select(c => c.FileName));
    }

    [Fact]
    public async Task Candidates_are_rendered_on_their_material_and_the_choice_attached()
    {
        var (kit, matcher, renderer, _) = await SetUpAsync((pack, dir) =>
        {
            Write(dir, "Pack/Models/Barn.fbx", Box(new BoxOptions { Material = "Material", DiffuseTexture = "House2Text.jpg" }), "fbx");
            Png(Path.Combine(pack, "Textures", "Red_Planks.png"), "#b05030");
            Png(Path.Combine(pack, "Textures", "Red_Planks_Normal.png"), "#8080ff");
            Png(Path.Combine(pack, "Textures", "Blue_Planks.png"), "#3050b0");
        });
        await using var _ = kit;
        var model = await ModelAsync(kit, "Barn");
        // The test kit has no model handler: give the asset what metadata extraction would have recorded.
        model.Model = new ModelMetadata { MissingTextures = ["House2Text.jpg"], TextureSuggestions = [new("Material", 1, "x.png", 0.5, "unsure")] };
        model.TexturesMissing = true;
        var m = Assert.Single(await matcher.PlanAsync(model.Id, Ct));
        Assert.Equal(["Blue_Planks.png", "Red_Planks.png"], m.Candidates.Select(c => c.FileName));

        var renders = await matcher.RenderCandidatesAsync(model.Id, m, Ct);

        Assert.Equal(3, renders.Count); // as it is, then each candidate
        Assert.All(renders, r => Assert.Equal(MagickFormat.Jpeg, new MagickImageInfo(r).Format));
        Assert.Empty(renderer.Requests[0].Textures);
        Assert.Contains("Material", renderer.Requests[0].NeutralMaterials);
        Assert.All(renderer.Requests.Skip(1), r =>
        {
            Assert.Equal(("Material", "map"), (r.Textures.Single().Material, r.Textures.Single().Slot));
            Assert.Empty(r.NeutralMaterials);
            Assert.Equal((TextureMatcher.RenderSize, 1, 1), (r.Size, r.Elevations.Count, r.Azimuths));
        });

        var red = m.Candidates.Single(c => c.FileName == "Red_Planks.png");
        await matcher.AttachAsync(model, "Material", red.FileId, CompanionOrigin.Ai, m.MissingReference, Ct);

        Assert.False(model.TexturesMissing);
        Assert.Empty(model.Model.TextureSuggestions); // the material is matched now
        await using var db = kit.Catalog.Db();
        var files = await db.AssetFiles.Where(f => f.AssetId == model.Id && f.Role == FileRole.Companion).ToListAsync(Ct);
        Assert.Equal([("Red_Planks.png", "map"), ("Red_Planks_Normal.png", "normalMap")],
            files.OrderBy(f => f.FileName, StringComparer.Ordinal).Select(f => (f.FileName, f.Slot!)));
        Assert.All(files, f => Assert.Equal((CompanionOrigin.Ai, "Material"), (f.Origin, f.Material)));
        Assert.Equal(3, await db.Assets.CountAsync(a => a.Type != AssetType.Model3D, Ct)); // the textures stay their own assets
        Assert.Empty(await matcher.PlanAsync(model.Id, Ct)); // now textured
    }

    [Fact]
    public void Normal_map_colour()
    {
        using var normal = new MagickImage(new MagickColor("#8080ff"), 16, 16);
        using var wood = new MagickImage(new MagickColor("#a07040"), 16, 16);
        Assert.True(TextureMatcher.IsNormalMapColor(normal.ToByteArray(MagickFormat.Png)));
        Assert.False(TextureMatcher.IsNormalMapColor(wood.ToByteArray(MagickFormat.Png)));
    }

    /// <summary>Five farm models whose material "Material" references a palette that is not there, and the pack's palette.</summary>
    private static Task<(ImportKit Kit, TextureMatcher Matcher, CandidateRenderer Renderer, string Pack)> FarmAsync() => SetUpAsync((pack, dir) =>
    {
        foreach (var name in new[] { "Barn", "Silo", "Windmill", "Tractor", "Well" })
            Write(dir, $"Pack/Models/{name}.fbx", Box(new BoxOptions { Material = "Material" }), "fbx");
        Png(Path.Combine(pack, "Textures", "Color 2.png"), "#c08040");
        Png(Path.Combine(pack, "Textures", "Grass_Tile.png"), "#40a040");
    });

    private static async Task AttachAsync(ImportKit kit, TextureMatcher matcher, string model, string texture)
    {
        await using var db = kit.Catalog.Db();
        var asset = await db.Assets.Include(a => a.Files).SingleAsync(a => a.Name == model, Ct);
        var file = (await matcher.SourceTexturesAsync(asset.Id, Ct)).Single(c => c.FileName == texture);
        await matcher.AttachAsync(asset, "Material", file.FileId, CompanionOrigin.Ai, null, Ct);
        await db.SaveChangesAsync(Ct);
    }

    /// <summary>Scenario "Pack texture suggestion".</summary>
    [Fact]
    public async Task Texture_most_models_use_for_a_material_is_suggested_for_the_rest()
    {
        var (kit, matcher, _, _) = await FarmAsync();
        await using var _ = kit;
        foreach (var name in new[] { "Barn", "Silo", "Windmill" }) await AttachAsync(kit, matcher, name, "Color 2.png");

        var made = await matcher.SuggestFromPackAsync((await ModelAsync(kit, "Barn")).SourceId, Ct);

        Assert.Equal(2, made);
        foreach (var name in new[] { "Tractor", "Well" })
        {
            var s = Assert.Single((await ModelAsync(kit, name)).Model!.TextureSuggestions);
            Assert.Equal(("Material", "Color 2.png", 1.0), (s.Material, s.RelativePath[(s.RelativePath.LastIndexOf('/') + 1)..], s.Confidence));
            Assert.Contains("3 of 3 models", s.Reason);
        }
        Assert.Empty((await ModelAsync(kit, "Barn")).Model?.TextureSuggestions ?? []);

        var search = new AssetManager.Data.Search.AssetSearchService(kit.Store);
        var found = await search.SearchAsync(new AssetManager.Core.Search.SearchQuery { TextureSuggestions = true }, 0, 10, Ct);
        Assert.Equal(["Tractor", "Well"], found.Items.Select(i => i.Name).Order());
    }

    /// <summary>Scenario "No clear pack texture".</summary>
    [Fact]
    public async Task No_suggestion_without_a_clear_majority_or_enough_models()
    {
        var (kit, matcher, _, _) = await FarmAsync();
        await using var _ = kit;
        await AttachAsync(kit, matcher, "Barn", "Color 2.png");
        await AttachAsync(kit, matcher, "Silo", "Color 2.png");
        var source = (await ModelAsync(kit, "Barn")).SourceId;

        Assert.Equal(0, await matcher.SuggestFromPackAsync(source, Ct)); // only 2 models

        await AttachAsync(kit, matcher, "Windmill", "Grass_Tile.png");
        await AttachAsync(kit, matcher, "Tractor", "Grass_Tile.png");
        await AttachAsync(kit, matcher, "Well", "Grass_Tile.png"); // 2 vs 3 of 5: Grass_Tile is 60 % but nothing is left untextured
        Assert.Equal(0, await matcher.SuggestFromPackAsync(source, Ct));
    }

    [Fact]
    public async Task User_edited_models_get_no_pack_suggestion_and_suggestions_apply_in_bulk()
    {
        var (kit, matcher, _, _) = await FarmAsync();
        await using var _ = kit;
        foreach (var name in new[] { "Barn", "Silo", "Windmill" }) await AttachAsync(kit, matcher, name, "Color 2.png");
        await using (var db = kit.Catalog.Db())
        {
            var well = await db.Assets.SingleAsync(a => a.Name == "Well", Ct);
            well.UserEdited |= EditedFields.Textures;
            await db.SaveChangesAsync(Ct);
        }

        Assert.Equal(1, await matcher.SuggestFromPackAsync((await ModelAsync(kit, "Barn")).SourceId, Ct));
        Assert.Empty((await ModelAsync(kit, "Well")).Model?.TextureSuggestions ?? []);

        var service = new ModelTextureService(kit.Store, kit.Processing.Resolver, kit.Previews, matcher);
        var ids = (await Task.WhenAll(new[] { "Tractor", "Well", "Barn" }.Select(n => ModelAsync(kit, n)))).Select(a => a.Id).ToList();
        var (models, textures) = await service.AttachSuggestionsAsync(ids, Ct);

        var tractor = await ModelAsync(kit, "Tractor");
        Assert.Equal([tractor.Id], models);
        Assert.Equal(1, textures);
        Assert.Empty(tractor.Model!.TextureSuggestions);
        Assert.True(tractor.UserEdited.HasFlag(EditedFields.Textures));
        Assert.Contains(tractor.Files, f => (f.FileName, f.Material, f.Slot, f.Origin) == ("Color 2.png", "Material", "map", CompanionOrigin.User));
    }
}
