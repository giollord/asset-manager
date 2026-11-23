using System.Text;
using System.Text.Json.Nodes;
using AssetManager.Core.Domain;
using AssetManager.Processing.Handlers;
using AssetManager.Processing.Models;
using AssetManager.Processing.Settings;
using AssetManager.Tests.Fixtures;
using ImageMagick;
using static AssetManager.Tests.Fixtures.ModelFixtures;

namespace AssetManager.Tests.Processing;

/// <summary>Animation clips: detection, the skinned/animated GLB, and the animated WebP preview.</summary>
public class AnimationTests
{
    private static JsonObject GltfJson(byte[] glb)
    {
        var length = BitConverter.ToInt32(glb, 12);
        return JsonNode.Parse(Encoding.UTF8.GetString(glb, 20, length))!.AsObject();
    }

    [Fact]
    public void Animated_glb_carries_skin_and_clip_that_assimp_reads_back()
    {
        using var dir = FixtureBuilder.TempDir();
        var source = WriteFbx(dir, "walk.fbx", Box(new BoxOptions { Skeleton = true, Animation = "Walk" }));
        using var original = ModelAnalyzer.Load(source);

        var glb = ModelAnalyzer.ToGlb(original.Scene, animated: true);
        var json = GltfJson(glb);
        Assert.Single(json["skins"]!.AsArray());
        Assert.Single(json["animations"]!.AsArray());
        Assert.Null(GltfJson(ModelAnalyzer.ToGlb(original.Scene))["animations"]); // static previews stay as before

        var path = Path.Combine(dir.Path, "walk.glb");
        File.WriteAllBytes(path, glb);
        using var copy = ModelAnalyzer.Load(path);
        var clip = Assert.Single(copy.Scene.Animations);
        Assert.Equal(1.0, ModelAnalyzer.ClipSeconds(clip), 2);
        Assert.Contains(clip.NodeAnimationChannels, c => c.NodeName == "Hips" && c.PositionKeys.Count == 2);
        Assert.Equal(["Hips"], copy.Scene.Meshes.SelectMany(m => m.Bones).Select(b => b.Name).Distinct());
    }

    [Theory]
    [InlineData("Assets/Mixamo Animations/Zombie/zombie idle.fbx", "mixamo.com", 1, 2.5, true)]
    [InlineData("Assets/Characters/Knight@Run.fbx", "Take 001", 1, 1.0, true)]
    [InlineData("Assets/Anims/Locomotion/Walk.fbx", "Take 001", 1, 1.2, true)]
    [InlineData("Assets/Characters/Knight.fbx", "Take 001", 1, 1.2, false)]           // a character that happens to carry one clip
    [InlineData("Assets/Animations/Knight.fbx", "Idle|Run|Jump", 1, 1.0, false)]      // several clips: a rigged character
    [InlineData("Assets/Mixamo/Ellen.fbx", "mixamo.com", 1, 0.03, false)]             // a one-frame bind-pose take
    [InlineData("Assets/Clips/Run.fbx", "Run", 0, 1.0, true)]                         // skeleton only, no mesh
    public void Detects_animation_clips(string path, string clips, int meshes, double seconds, bool expected)
    {
        var names = clips.Split('|').ToList();
        var meta = new ModelMetadata
        {
            Meshes = meshes, Bones = 52, Animations = names, AnimationSeconds = names.Select(_ => seconds).ToList(),
        };
        Assert.Equal(expected, ModelAnalyzer.LooksLikeAnimationClip(meta, path));
    }

    [Fact]
    public async Task Clip_file_becomes_an_animation_with_an_animated_preview()
    {
        using var dir = FixtureBuilder.TempDir();
        var path = WriteFbx(dir, "Animations/Walk.fbx", Box(new BoxOptions { Skeleton = true, Animation = "Walk" }));
        var item = new HandlerItem
        {
            Asset = ImageHandlerTests.Item(path, AssetType.Model3D).Asset,
            PrimaryPath = path,
        };
        var renderer = new ClipRenderer();
        var processor = new AssetProcessor(new HandlerRegistry([new ModelHandler(renderer), new FallbackHandler()]));

        var result = await processor.ProcessAsync(item, new PreviewOptions());

        Assert.Equal(AssetType.Animation, item.Asset.Type);
        Assert.Equal([1.0], item.Asset.Model!.AnimationSeconds);
        Assert.NotNull(renderer.Last);
        Assert.Equal((4, 10, 256), (renderer.Last!.Fps, renderer.Last.MaxSeconds, renderer.Last.Size));
        Assert.Equal(["Hips"], GltfJson(renderer.Last.Glb)["skins"]![0]!["joints"]!.AsArray()
            .Select(j => GltfJson(renderer.Last.Glb)["nodes"]![(int)j!]!["name"]!.GetValue<string>()));
        var animation = Assert.Single(result.Previews, p => p.Kind == PreviewKind.Animation);
        Assert.Single(result.Previews, p => p.Kind == PreviewKind.Thumbnail);
        Assert.DoesNotContain(result.Previews, p => p.Kind is PreviewKind.ModelView or PreviewKind.OrientationProbe);
        using var frames = new MagickImageCollection(animation.Bytes);
        Assert.Equal(4, frames.Count);
        Assert.Equal(25u, frames[0].AnimationDelay);
    }

    [Fact]
    public async Task User_typed_model_is_not_reclassified()
    {
        using var dir = FixtureBuilder.TempDir();
        var path = WriteFbx(dir, "Animations/Walk.fbx", Box(new BoxOptions { Skeleton = true, Animation = "Walk" }));
        var item = new HandlerItem { Asset = ImageHandlerTests.Item(path, AssetType.Model3D).Asset, PrimaryPath = path };
        item.Asset.UserEdited = EditedFields.Type;

        await new ModelHandler().ExtractMetadataAsync(item, new PreviewOptions());

        Assert.Equal(AssetType.Model3D, item.Asset.Type);
    }

    /// <summary>Returns <c>fps × seconds</c> small transparent WebP frames.</summary>
    private sealed class ClipRenderer : IModelRenderer
    {
        public AnimationRenderRequest? Last { get; private set; }

        public bool IsAvailable => true;

        public Task<IReadOnlyList<RenderedView>> RenderAsync(ModelRenderRequest request, CancellationToken ct = default) =>
            throw new InvalidOperationException("Clips render as animations.");

        public Task<RenderedView> RenderProbeAsync(ProbeRequest request, CancellationToken ct = default) =>
            throw new InvalidOperationException("Clips are not orientation-judged.");

        public Task<AnimationFrames> RenderAnimationAsync(AnimationRenderRequest request, CancellationToken ct = default)
        {
            Last = request;
            var frames = Enumerable.Range(0, request.Fps).Select(i =>
            {
                using var image = new MagickImage(MagickColors.Transparent, 32, 32);
                image.Draw(new ImageMagick.Drawing.Drawables().FillColor(MagickColors.Red).Rectangle(i * 6, 4, i * 6 + 6, 28));
                return image.ToByteArray(MagickFormat.WebP);
            }).ToList();
            return Task.FromResult(new AnimationFrames(32, 32, request.Fps, frames));
        }
    }
}

/// <summary>Diagnostics: set ASSETMANAGER_DUMP_GLB=input.fbx to write input.fbx.glb (animated) and print the node tree.</summary>
public class AnimationDumpTests(ITestOutputHelper output)
{
    [Fact]
    public void Dump_animated_glb()
    {
        var input = Environment.GetEnvironmentVariable("ASSETMANAGER_DUMP_GLB");
        Assert.SkipWhen(string.IsNullOrEmpty(input), "Set ASSETMANAGER_DUMP_GLB to an FBX path.");
        var log = new System.Text.StringBuilder();
        void Log(string line) { log.AppendLine(line); output.WriteLine(line); }
        using var model = ModelAnalyzer.Load(input!);
        File.WriteAllBytes(input + ".glb", ModelAnalyzer.ToGlb(model.Scene, animated: true));
        void Print(Assimp.Node n, string indent)
        {
            var t = n.Transform;
            Log($"{indent}{n.Name} meshes={n.MeshCount} T41=({t.M41:0.##},{t.M42:0.##},{t.M43:0.##}) T14=({t.M14:0.##},{t.M24:0.##},{t.M34:0.##}) S≈{new System.Numerics.Vector3(t.M11, t.M12, t.M13).Length():0.###}");
            if (indent.Length < 12) foreach (var c in n.Children) Print(c, indent + "  ");
        }
        Print(model.Scene.RootNode, "");
        foreach (var m in model.Scene.Meshes)
        {
            var b = m.Bones.FirstOrDefault(x => x.Name.Contains("Hips"));
            if (b is null) continue;
            var o = b.OffsetMatrix;
            Log($"mesh {m.Name} bones={m.BoneCount} hipsOffset T41=({o.M41:0.##},{o.M42:0.##},{o.M43:0.##}) T14=({o.M14:0.##},{o.M24:0.##},{o.M34:0.##}) row1=({o.M11:0.###},{o.M12:0.###},{o.M13:0.###},{o.M14:0.###})");
        }
        foreach (var a in model.Scene.Animations)
            Log($"anim {a.Name} ticks={a.DurationInTicks} tps={a.TicksPerSecond} channels={a.NodeAnimationChannelCount} first={a.NodeAnimationChannels[0].NodeName}");
        File.WriteAllText(input + ".txt", log.ToString());
    }
}
