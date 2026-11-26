using System.Numerics;
using AssetManager.Core.Domain;
using AssetManager.Processing.Handlers;
using AssetManager.Processing.Models;
using AssetManager.Processing.Settings;
using AssetManager.Tests.Fixtures;
using ImageMagick;
using static AssetManager.Tests.Fixtures.ModelFixtures;

namespace AssetManager.Tests.Processing;

public class ModelHandlerTests
{
    private static AssetProcessor Processor(IModelRenderer? renderer = null) =>
        new(new HandlerRegistry([new ModelHandler(renderer), new FallbackHandler()]));

    private static HandlerItem Item(string path, params CompanionInput[] companions) => new()
    {
        Asset = ImageHandlerTests.Item(path, AssetType.Model3D).Asset,
        PrimaryPath = path,
        Companions = companions,
    };

    /// <summary>An OBJ's .mtl companion (claimed by name) is not a texture: rendering it as one failed the preview.</summary>
    [Fact]
    public void A_material_library_companion_is_not_used_as_a_texture()
    {
        using var dir = FixtureBuilder.TempDir();
        var path = Write(dir, "tree.obj", Box(new BoxOptions { Material = "Bark" }), "obj");
        var mtl = Path.ChangeExtension(path, ".mtl");
        File.WriteAllLines(mtl, ["newmtl Bark", "Kd 0.6 0.6 0.6"]);
        var item = Item(path, new CompanionInput(new AssetFile { FileName = "tree.mtl", RelativePath = "tree.mtl", Extension = ".mtl" }, mtl));
        using var model = ModelAnalyzer.Load(path);

        var (textures, _, _) = ModelHandler.ResolveTextures(model.Scene, item, includeBytes: false);

        Assert.Empty(textures);
    }

    [Fact]
    public async Task Fbx_stats_match()
    {
        using var dir = FixtureBuilder.TempDir();
        var path = WriteFbx(dir, "knight.fbx", Box(new BoxOptions { Size = new(2, 3, 4), Skeleton = true, Animation = "Walk" }));
        var item = Item(path);

        var result = await Processor().ProcessAsync(item, new PreviewOptions());

        var m = item.Asset.Model!;
        Assert.Equal(12, m.Triangles);
        Assert.InRange(m.Vertices, 8, 36);
        Assert.Equal(1, m.Meshes);
        Assert.True(m.Materials >= 1);
        Assert.Equal(1, m.Bones);
        Assert.True(m.HasSkeleton);
        Assert.Contains(m.Animations, a => a.Contains("Walk"));
        Assert.Equal([2, 3, 4], m.BoundsSize.Select(v => Math.Round(v, 3)));
        Assert.Equal("FBX", m.Format);
        Assert.Contains("renderer unavailable", result.Error);
    }

    [Fact]
    public async Task Obj_stats_match()
    {
        using var dir = FixtureBuilder.TempDir();
        var path = Write(dir, "crate.obj", Box(new BoxOptions { Size = new(1, 2, 1) }), "obj");
        var item = Item(path);

        await Processor().ProcessAsync(item, new PreviewOptions());

        var m = item.Asset.Model!;
        Assert.Equal(12, m.Triangles);
        Assert.Equal(1, m.Meshes);
        Assert.Equal(0, m.Bones);
        Assert.Empty(m.Animations);
        Assert.Equal([1, 2, 1], m.BoundsSize.Select(v => Math.Round(v, 3)));
    }

    [Fact]
    public void Node_transforms_apply_to_world_bounds()
    {
        using var dir = FixtureBuilder.TempDir();
        var path = Write(dir, "moved.glb", Box(new BoxOptions { RootTransform = Matrix4x4.CreateTranslation(0, 5, 0) * Matrix4x4.CreateScale(2) }), "glb2");
        using var model = ModelAnalyzer.Load(path);
        var (min, max) = ModelAnalyzer.WorldBounds(model.Scene);
        Assert.Equal(10, min.Y, 3);
        Assert.Equal(12, max.Y, 3);
        Assert.Equal(2, max.X - min.X, 3);
    }

    [Fact]
    public async Task Model_exported_100x_too_large()
    {
        using var dir = FixtureBuilder.TempDir();
        var scene = Box(new BoxOptions { Size = new(0.5f, 1.8f, 0.3f), NodeTransform = Matrix4x4.CreateScale(100) });
        var item = Item(WriteFbx(dir, "giant.fbx", scene, unitScaleFactor: 100)); // file unit = metre

        await Processor().ProcessAsync(item, new PreviewOptions());

        var t = item.Asset.Model!.Transform;
        Assert.Equal(180, t.SizeMeters[1], 2);
        Assert.Equal(100, t.UnitScale);
        Assert.Equal([100, 100, 100], t.RootScale);
        Assert.True(t.Uniform);
        Assert.Equal(ScaleAnomaly.TooLarge, t.Anomaly);
        Assert.Equal(ScaleAnomaly.TooLarge, item.Asset.ScaleAnomaly);
    }

    [Theory]
    [InlineData("fbx")]
    [InlineData("glb2")]
    public async Task Normal_scale(string format)
    {
        using var dir = FixtureBuilder.TempDir();
        // FBX default unit is the centimetre (unit scale 1): a 180-unit character is 1.8 m. glTF is in metres.
        var height = format == "fbx" ? 180f : 1.8f;
        var scene = Box(new BoxOptions { Size = new(height / 4, height, height / 6) });
        var path = format == "fbx" ? WriteFbx(dir, "hero.fbx", scene) : Write(dir, "hero.glb", scene, format);
        var item = Item(path);

        await Processor().ProcessAsync(item, new PreviewOptions());

        var t = item.Asset.Model!.Transform;
        Assert.Equal(1.8, t.SizeMeters[1], 3);
        Assert.Equal(1, t.UnitScale);
        Assert.Equal(ScaleAnomaly.None, t.Anomaly);
    }

    [Fact]
    public async Task Non_uniform_and_tiny_models_are_flagged()
    {
        using var dir = FixtureBuilder.TempDir();
        var stretched = Item(Write(dir, "s.glb", Box(new BoxOptions { RootTransform = Matrix4x4.CreateScale(1, 3, 1) }), "glb2"));
        await Processor().ProcessAsync(stretched, new PreviewOptions());
        Assert.Equal(ScaleAnomaly.NonUniform, stretched.Asset.ScaleAnomaly);

        var tiny = Item(Write(dir, "t.glb", Box(new BoxOptions { Size = new(0.001f, 0.002f, 0.001f) }), "glb2"));
        await Processor().ProcessAsync(tiny, new PreviewOptions());
        Assert.Equal(ScaleAnomaly.TooSmall, tiny.Asset.ScaleAnomaly);

        await Processor().ProcessAsync(tiny, new PreviewOptions { ScaleTooSmallMeters = 0.0001 });
        Assert.Equal(ScaleAnomaly.None, tiny.Asset.ScaleAnomaly);
    }

    [Theory]
    [InlineData(2, 1, "+Z", 270, 0)]
    [InlineData(1, 1, "+Y", 0, 0)]
    [InlineData(0, 1, "+X", 0, 90)]
    [InlineData(1, -1, "-Y", 180, 0)]
    public async Task Up_axis_metadata_sets_orientation(int axis, int sign, string expected, int rotX, int rotZ)
    {
        using var dir = FixtureBuilder.TempDir();
        var item = Item(WriteFbx(dir, "tree.fbx", Box(new BoxOptions()), upAxis: axis, upSign: sign));

        await Processor().ProcessAsync(item, new PreviewOptions());

        var m = item.Asset.Model!;
        Assert.Equal(expected, m.Transform.SourceUpAxis);
        Assert.Equal(OrientationDecidedBy.Metadata, m.Orientation.DecidedBy);
        Assert.Equal((rotX, 0, rotZ), (m.Orientation.RotX, m.Orientation.RotY, m.Orientation.RotZ));
        Assert.False(item.Asset.OrientationVerified); // no AI: "orientation not verified"
    }

    [Fact]
    public async Task User_orientation_survives_reprocessing()
    {
        using var dir = FixtureBuilder.TempDir();
        var item = Item(WriteFbx(dir, "rock.fbx", Box(new BoxOptions()), upAxis: 2));
        item.Asset.Model = new ModelMetadata { Orientation = new ModelOrientation { RotY = 90, DecidedBy = OrientationDecidedBy.User } };

        await Processor().ProcessAsync(item, new PreviewOptions());

        Assert.Equal(OrientationDecidedBy.User, item.Asset.Model!.Orientation.DecidedBy);
        Assert.Equal(90, item.Asset.Model.Orientation.RotY);
    }

    [Fact]
    public async Task Missing_and_found_textures()
    {
        using var dir = FixtureBuilder.TempDir();
        var missing = Item(Write(dir, "a/barrel.obj", Box(new BoxOptions { DiffuseTexture = "barrel_albedo.png" }), "obj"));
        await Processor().ProcessAsync(missing, new PreviewOptions());
        Assert.True(missing.Asset.TexturesMissing);
        Assert.Equal(["barrel_albedo.png"], missing.Asset.Model!.MissingTextures);

        // Present next to the model.
        var modelPath = Write(dir, "b/barrel.obj", Box(new BoxOptions { DiffuseTexture = "barrel_albedo.png" }), "obj");
        using (var img = new MagickImage(MagickColors.SaddleBrown, 4096, 4096)) img.Write(Path.Combine(dir.Path, "b", "barrel_albedo.png"));
        var found = Item(modelPath);
        await Processor().ProcessAsync(found, new PreviewOptions());
        Assert.False(found.Asset.TexturesMissing);

        // Referenced as .jpg, shipped as .png: still found.
        var renamed = Item(Write(dir, "c/barrel.obj", Box(new BoxOptions { DiffuseTexture = "barrel_albedo.jpg" }), "obj"));
        File.Copy(Path.Combine(dir.Path, "b", "barrel_albedo.png"), Path.Combine(dir.Path, "c", "barrel_albedo.png"));
        await Processor().ProcessAsync(renamed, new PreviewOptions());
        Assert.False(renamed.Asset.TexturesMissing);

        var request = ModelHandler.BuildRenderRequest(found, new PreviewOptions());
        var tex = Assert.Single(request.Textures);
        Assert.Equal(("Wood", "map", "image/png"), (tex.Material, tex.Slot, tex.Mime));
        using var decoded = new MagickImage(tex.Bytes);
        Assert.Equal(1024u, decoded.Width); // downscaled for rendering
        Assert.True(request.Glb.AsSpan(0, 4).SequenceEqual("glTF"u8));
    }

    [Fact]
    public async Task Renders_all_views_and_marks_primary_thumbnail()
    {
        using var dir = FixtureBuilder.TempDir();
        var renderer = new FakeRenderer();
        var item = Item(Write(dir, "box.glb", Box(new BoxOptions()), "glb2"));

        var result = await Processor(renderer).ProcessAsync(item, new PreviewOptions());

        Assert.Null(result.Error);
        Assert.Equal(24, result.Previews.Count(p => p.Kind == PreviewKind.ModelView));
        var thumb = Assert.Single(result.Previews, p => p.Kind == PreviewKind.Thumbnail);
        Assert.Equal(new PreviewOptions().PrimaryViewIndex, thumb.Bytes[0]);
        Assert.Equal([-15, 20, 55], renderer.Last!.Elevations);
        Assert.Equal(384, renderer.Last.Size);
    }

    [Fact]
    public async Task Unrenderable_model_falls_back_to_unity_preview()
    {
        using var dir = FixtureBuilder.TempDir();
        var blend = dir.Write("scene.blend", "BLENDER-v300 not supported by Assimp 6");
        var unity = dir.Write("preview.png", [0x89, 0x50, 0x4E, 0x47, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 64, 0, 0, 0, 64]);
        var item = new HandlerItem { Asset = Item(blend).Asset, PrimaryPath = blend, UnityPreviewPath = unity };

        var result = await Processor(new FakeRenderer()).ProcessAsync(item, new PreviewOptions());

        Assert.StartsWith("Metadata: Cannot load model", result.Error);
        Assert.Equal(result.Error, item.Asset.PreviewError);
        var p = Assert.Single(result.Previews);
        Assert.Equal((PreviewKind.UnityPreview, 64, 64), (p.Kind, p.Width, p.Height));

        // Without a Unity preview nothing is stored and the UI shows the type placeholder.
        var bare = Item(blend);
        Assert.Empty((await Processor(new FakeRenderer()).ProcessAsync(bare, new PreviewOptions())).Previews);
        Assert.NotNull(bare.Asset.PreviewError);
    }

    [Fact]
    public void Probe_ranking_prefers_the_tall_axis()
    {
        using var dir = FixtureBuilder.TempDir();
        // A post authored along Z.
        var zTall = Box(new BoxOptions { Size = new(0.3f, 0.3f, 4f) });
        using (var m = ModelAnalyzer.Load(Write(dir, "post.glb", zTall, "glb2")))
            Assert.Contains(OrientationProbe.Rank(m.Scene)[0], new[] { 3, 4 });
        using (var m = ModelAnalyzer.Load(Write(dir, "pole.glb", Box(new BoxOptions { Size = new(0.3f, 4f, 0.3f) }), "glb2")))
            Assert.Contains(OrientationProbe.Rank(m.Scene)[0], new[] { 1, 2 });
        Assert.Equal([1, 2, 3, 4, 5, 6], OrientationProbe.Candidates.Select(c => c.Label));
        Assert.Equal(ModelAnalyzer.OrientationFromUpAxis("+Z").RotX, OrientationProbe.Candidates.Single(c => c.UpAxis == "+Z").RotX);
    }

    [Fact]
    public async Task Z_up_fbx_is_rendered_upright_via_metadata_without_probe()
    {
        using var dir = FixtureBuilder.TempDir();
        var renderer = new FakeRenderer();
        var item = Item(WriteFbx(dir, "tree.fbx", Box(new BoxOptions()), upAxis: 2));

        var result = await Processor(renderer).ProcessAsync(item, new PreviewOptions());

        Assert.Equal((270, 0, 0), (renderer.Last!.Orientation.RotX, renderer.Last.Orientation.RotY, renderer.Last.Orientation.RotZ));
        Assert.Equal(OrientationDecidedBy.Metadata, renderer.Last.Orientation.DecidedBy);
        Assert.DoesNotContain(result.Previews, p => p.Kind == PreviewKind.OrientationProbe);
        Assert.Equal(0, renderer.ProbeCount);
    }

    [Fact]
    public async Task Model_without_up_metadata_gets_a_probe_sheet()
    {
        using var dir = FixtureBuilder.TempDir();
        var renderer = new FakeRenderer();
        var item = Item(Write(dir, "rock.obj", Box(new BoxOptions()), "obj"));

        var result = await Processor(renderer).ProcessAsync(item, new PreviewOptions());

        var probe = Assert.Single(result.Previews, p => p.Kind == PreviewKind.OrientationProbe);
        Assert.Equal((768, 512), (probe.Width, probe.Height));
        Assert.Equal(6, item.Asset.Model!.ProbeRanking.Count);
        Assert.Equal(1, renderer.ProbeCount);
    }

    [Fact]
    public async Task Original_untouched_with_orientation_correction()
    {
        using var dir = FixtureBuilder.TempDir();
        var path = WriteFbx(dir, "tree.fbx", Box(new BoxOptions { Size = new(1, 1, 3) }), upAxis: 2);
        var before = await File.ReadAllBytesAsync(path);
        var item = Item(path);

        await Processor(new FakeRenderer()).ProcessAsync(item, new PreviewOptions());
        Assert.False(item.Asset.Model!.Orientation.IsIdentity);
        ModelHandler.BuildProbeRequest(item, new PreviewOptions());

        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        // Export copies what the resolver returns: the original bytes.
        using var kit = new ProcessingKit();
        using var resolved = await kit.Resolver.ResolveAsync(new Location(null, path));
        Assert.Equal(before, await File.ReadAllBytesAsync(resolved.Path));
    }

    /// <summary>Returns one tiny "render" per view whose first byte is the view index.</summary>
    internal sealed class FakeRenderer : IModelRenderer
    {
        public ModelRenderRequest? Last { get; private set; }

        public bool IsAvailable => true;

        public Task<IReadOnlyList<RenderedView>> RenderAsync(ModelRenderRequest request, CancellationToken ct = default)
        {
            Last = request;
            var count = request.Elevations.Count * request.Azimuths;
            return Task.FromResult<IReadOnlyList<RenderedView>>(
                Enumerable.Range(0, count).Select(i => new RenderedView(i, request.Size, request.Size, [(byte)i])).ToList());
        }

        public int ProbeCount { get; private set; }

        public Task<RenderedView> RenderProbeAsync(ProbeRequest request, CancellationToken ct = default)
        {
            ProbeCount++;
            return Task.FromResult(new RenderedView(0, request.TileSize * 3, request.TileSize * 2, [42]));
        }
    }
}
