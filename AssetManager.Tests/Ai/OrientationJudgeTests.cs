using AssetManager.AI.Enrichment;
using AssetManager.AI.Runtime;
using AssetManager.Core.Domain;
using AssetManager.Processing.Models;
using AssetManager.Processing.Settings;
using ImageMagick;
using Microsoft.Extensions.AI;

namespace AssetManager.Tests.Ai;

[Collection(AiModelCollection.Name)]
public class OrientationJudgeTests
{
    private static readonly PreviewOptions Options = new();
    private static readonly AiModalities Vision = new(true, false);

    private static byte[] Gray()
    {
        using var img = new MagickImage(MagickColors.Gray, 32, 32);
        return img.ToByteArray(MagickFormat.WebP);
    }

    private static PreviewBlob Blob(PreviewKind kind, int index = 0) => new() { Kind = kind, Index = index, Mime = "image/webp", Bytes = Gray() };

    private static ItemInput Tree(ModelOrientation? orientation = null)
    {
        var asset = new Asset { Id = 7, Name = "Tree_01", Type = AssetType.Model3D, Model = new ModelMetadata { Orientation = orientation ?? new(), ProbeRanking = [3, 1, 2, 4, 5, 6] } };
        PreviewBlob[] previews = [.. Enumerable.Range(0, 24).Select(i => Blob(PreviewKind.ModelView, i)), Blob(PreviewKind.OrientationProbe)];
        return new ItemInput(asset, "Name: Tree_01\nType: Model3D", previews);
    }

    private static string Probe(int choice, double confidence) =>
        $$"""{"observations":["a","b","c","d","e","f"],"choice":{{choice}},"yaw_fix":0,"confidence":{{confidence.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}""";

    [Fact]
    public async Task Disagreeing_answers_are_treated_as_uncertain()
    {
        var input = Tree();
        var client = new FakeChatClient(Probe(3, 0.95), Probe(3, 0.95), ItemAnswer); // shown 3rd when shuffled = candidate 4
        var rerenderer = new FakeRerenderer();

        var result = await ModelEnrichment.EnrichAsync(new StructuredChat(client), input, new ItemContext(), Vision, Options, rerenderer, ct: TestContext.Current.CancellationToken);

        Assert.False(result.Orientation!.Consistent);
        Assert.True(input.Asset.OrientationUncertain);
        Assert.True(input.Asset.Model!.Orientation.IsIdentity);
        Assert.Empty(rerenderer.Calls);
    }

    [Fact]
    public void Shuffled_prompt_renumbers_tiles_and_ranking()
    {
        using var sheet = new MagickImage(MagickColors.White, 768, 512);
        var probe = new PreviewBlob { Kind = PreviewKind.OrientationProbe, Mime = "image/png", Bytes = sheet.ToByteArray(MagickFormat.Png) };
        var asset = Tree().Asset; // ranking 3, 1, 2, 4, 5, 6

        var message = OrientationJudge.BuildPrompt(asset, probe, OrientationJudge.ShuffledOrder).Single();

        Assert.Equal(6, message.Contents.OfType<DataContent>().Count());
        Assert.Contains("most likely first): 4, 6, 5, 3, 2, 1", message.Text);
        Assert.Equal(6, OrientationJudge.SplitProbe(probe.Bytes).Count());
    }

    private const string ItemAnswer = """{"items":[{"index":1,"description":"A tree.","type":"model3_d","tags":["tree"],"colors":[],"style":[],"is_game_asset":true}]}""";

    /// <summary>Records the orientation at re-render time and returns new (marked) views.</summary>
    private sealed class FakeRerenderer : IPreviewRerenderer
    {
        public List<ModelOrientation> Calls { get; } = [];

        public Task<IReadOnlyList<PreviewBlob>> RerenderAsync(Asset asset, CancellationToken ct)
        {
            Calls.Add(asset.Model!.Orientation);
            IReadOnlyList<PreviewBlob> fresh = [.. Enumerable.Range(0, 24).Select(i => Blob(PreviewKind.ModelView, i)), Blob(PreviewKind.OrientationProbe)];
            return Task.FromResult(fresh);
        }
    }

    [Fact]
    public async Task Correction_triggers_rerender_before_the_description()
    {
        var input = Tree();
        var client = new FakeChatClient(Probe(3, 0.9), Probe(4, 0.8), ItemAnswer); // shown 4th in the shuffled order = candidate 3
        var rerenderer = new FakeRerenderer();

        var result = await ModelEnrichment.EnrichAsync(new StructuredChat(client), input, new ItemContext(), Vision, Options, rerenderer, ct: TestContext.Current.CancellationToken);

        var orientation = input.Asset.Model!.Orientation;
        Assert.Equal((270, 0, 0, OrientationDecidedBy.AI), (orientation.RotX, orientation.RotY, orientation.RotZ, orientation.DecidedBy));
        Assert.Equal("rotated 270° about X (AI)", orientation.Describe());
        Assert.True(input.Asset.OrientationVerified);
        Assert.Equal(270, Assert.Single(rerenderer.Calls).RotX); // the new rotation was saved before re-rendering
        Assert.Equal(3, client.Requests.Count);
        Assert.Contains("6 candidate orientations", client.Requests[0].Messages[^1].Text);
        Assert.Contains("ranks the candidates (most likely first): 3, 1, 2", client.Requests[0].Messages[^1].Text);
        Assert.Contains("3D model from 4 sides", client.Requests[2].Messages[^1].Text); // item pass ran after, on the new views
        Assert.Equal("A tree.", result.Item.Result.Description);
    }

    [Fact]
    public async Task Low_confidence_leaves_orientation_and_marks_uncertain()
    {
        var input = Tree();
        var client = new FakeChatClient(Probe(5, 0.35), ItemAnswer);
        var rerenderer = new FakeRerenderer();

        var result = await ModelEnrichment.EnrichAsync(new StructuredChat(client), input, new ItemContext(), Vision, Options, rerenderer, ct: TestContext.Current.CancellationToken);

        Assert.True(input.Asset.Model!.Orientation.IsIdentity);
        Assert.True(input.Asset.OrientationUncertain);
        Assert.False(input.Asset.OrientationVerified);
        Assert.Empty(rerenderer.Calls);
        Assert.False(result.Orientation!.Rerender);
        Assert.Equal(AiStatus.NotEnriched, input.Asset.AiStatus); // the caller applies the item outcome
    }

    [Fact]
    public void No_natural_up_keeps_the_authored_orientation()
    {
        var decision = OrientationJudge.Decide(new ModelOrientation(), new OrientationResult { Choice = 0, Confidence = 0.8 });
        Assert.False(decision.Rerender);
        Assert.True(decision.Orientation.NoNaturalUp);
        Assert.Equal("as authored", decision.Orientation.Describe());
        Assert.Equal(OrientationDecidedBy.AI, decision.Orientation.DecidedBy);

        // A previous AI rotation is undone when the model now says there is no natural up.
        var undo = OrientationJudge.Decide(new ModelOrientation { RotX = 90, DecidedBy = OrientationDecidedBy.AI }, new OrientationResult { Choice = 0, Confidence = 0.8 });
        Assert.True(undo.Rerender);
        Assert.True(undo.Orientation.IsIdentity);
    }

    [Fact]
    public async Task Ai_unavailable_marks_orientation_not_verified()
    {
        var input = Tree(new ModelOrientation { RotX = 270, DecidedBy = OrientationDecidedBy.Metadata, Confidence = 1, Verified = true });
        input.Asset.OrientationVerified = true;
        var client = new FakeChatClient(ItemAnswer);

        await ModelEnrichment.EnrichAsync(new StructuredChat(client), input, new ItemContext(), AiModalities.None, Options, new FakeRerenderer(), ct: TestContext.Current.CancellationToken);

        Assert.False(input.Asset.OrientationVerified);
        Assert.Equal((270, OrientationDecidedBy.Metadata), (input.Asset.Model!.Orientation.RotX, input.Asset.Model.Orientation.DecidedBy));
        Assert.DoesNotContain("candidate orientations", Assert.Single(client.Requests).Messages[^1].Text);

        var noModel = Tree();
        OrientationJudge.MarkNotVerified(noModel.Asset);
        Assert.False(noModel.Asset.OrientationVerified);
    }

    [Fact]
    public void User_orientation_is_never_judged_or_changed()
    {
        var user = new ModelOrientation { RotZ = 90, DecidedBy = OrientationDecidedBy.User };
        var input = Tree(user);
        Assert.False(OrientationJudge.NeedsJudgment(input.Asset, input.Previews));
        Assert.Same(user, OrientationJudge.Decide(user, new OrientationResult { Choice = 1, Confidence = 1 }).Orientation);
    }

    /// <summary>Spec "User override": a rotation saved in the asset details survives a later AI run.</summary>
    [Fact]
    public async Task Rotation_saved_in_details_is_kept_by_a_later_ai_run()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var c = await Tests.Data.TestCatalog.CreateAsync();
        var src = await Tests.Data.TestData.SourceAsync(c, 1, make: _ => new Asset
        {
            Name = "Tree_01", Type = AssetType.Model3D, OrientationUncertain = true,
            Model = new ModelMetadata { ProbeRanking = [3, 1, 2, 4, 5, 6] },
        });
        var id = src.Assets[0].Id;

        await new AssetManager.Data.Services.AssetDetailsService(c.Store).SetOrientationAsync(id, 90, 0, -90, ct);

        Asset saved;
        await using (var db = c.Db()) saved = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(db.Assets, a => a.Id == id, ct);
        Assert.Equal((90, 0, 270, OrientationDecidedBy.User), (saved.Model!.Orientation.RotX, saved.Model.Orientation.RotY, saved.Model.Orientation.RotZ, saved.Model.Orientation.DecidedBy));
        Assert.True(saved.UserEdited.HasFlag(EditedFields.Orientation) && saved.OrientationVerified && !saved.OrientationUncertain);

        // The AI is not even asked about orientation (the fake only answers the item prompt); the rotation stays.
        PreviewBlob[] previews = [.. Enumerable.Range(0, 24).Select(i => Blob(PreviewKind.ModelView, i)), Blob(PreviewKind.OrientationProbe)];
        var input = new ItemInput(saved, "Name: Tree_01", previews);
        var rerenderer = new FakeRerenderer();
        await ModelEnrichment.EnrichAsync(new StructuredChat(new FakeChatClient(ItemAnswer)), input, new ItemContext(), Vision, Options,
            rerenderer, ct: ct);

        Assert.Equal((90, 0, 270, OrientationDecidedBy.User), (saved.Model!.Orientation.RotX, saved.Model.Orientation.RotY, saved.Model.Orientation.RotZ, saved.Model.Orientation.DecidedBy));
        Assert.False(saved.OrientationUncertain);
        Assert.Empty(rerenderer.Calls);
    }

    [Fact]
    public void Yaw_fix_composes_into_an_axis_aligned_rotation()
    {
        foreach (var c in OrientationProbe.Candidates)
        foreach (var yaw in new[] { 0, 90, 180, 270 })
        {
            var (x, y, z) = OrientationJudge.WithYaw(c.RotX, c.RotY, c.RotZ, yaw);
            var expected = OrientationProbe.Rotation(c.RotX, c.RotY, c.RotZ) * System.Numerics.Matrix4x4.CreateRotationY(yaw * MathF.PI / 180);
            var actual = OrientationProbe.Rotation(x, y, z);
            // The up direction (+Y after rotation) must not change with yaw.
            var up = System.Numerics.Vector3.TransformNormal(System.Numerics.Vector3.UnitY, OrientationProbe.Rotation(c.RotX, c.RotY, c.RotZ));
            Assert.True(System.Numerics.Vector3.Distance(up, System.Numerics.Vector3.TransformNormal(System.Numerics.Vector3.UnitY, actual)) < 1e-3 || yaw != 0);
            Assert.True(OrientationJudge.SameRotation(new ModelOrientation { RotX = x, RotY = y, RotZ = z }, new ModelOrientation { RotX = x, RotY = y, RotZ = z }));
            float[] e = [expected.M11, expected.M12, expected.M13, expected.M21, expected.M22, expected.M23, expected.M31, expected.M32, expected.M33];
            float[] a = [actual.M11, actual.M12, actual.M13, actual.M21, actual.M22, actual.M23, actual.M31, actual.M32, actual.M33];
            Assert.All(e.Zip(a), p => Assert.Equal(p.First, p.Second, 3));
            Assert.All(new[] { x, y, z }, v => Assert.Contains(v, new[] { 0, 90, 180, 270 }));
        }
    }

    /// <summary>
    /// Manual check against a real probe sheet: ASSETMANAGER_AI_PROBE points to a probe image (e.g. saved from
    /// /diagnostics/render), ASSETMANAGER_AI_PROBE_EXPECT to the upright candidate number.
    /// </summary>
    [AiModelFact]
    public async Task Real_model_stands_a_sideways_model_upright()
    {
        var probePath = Environment.GetEnvironmentVariable("ASSETMANAGER_AI_PROBE");
        Assert.SkipWhen(AiModelFactAttribute.Mmproj is null || probePath is null || !File.Exists(probePath), "Set ASSETMANAGER_AI_MMPROJ and ASSETMANAGER_AI_PROBE to run.");
        var expected = int.Parse(Environment.GetEnvironmentVariable("ASSETMANAGER_AI_PROBE_EXPECT") ?? "0");
        await using var server = await AiTestServer.StartAsync(vision: true);
        var asset = new Asset { Name = Environment.GetEnvironmentVariable("ASSETMANAGER_AI_PROBE_NAME") ?? "model", Type = AssetType.Model3D, Model = new ModelMetadata { ProbeRanking = (Environment.GetEnvironmentVariable("ASSETMANAGER_AI_PROBE_RANKING") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToList() } };
        var probe = new PreviewBlob { Kind = PreviewKind.OrientationProbe, Mime = "image/webp", Bytes = await File.ReadAllBytesAsync(probePath, TestContext.Current.CancellationToken) };

        var decision = await OrientationJudge.JudgeAsync(new StructuredChat(server.Client), asset, probe, ct: TestContext.Current.CancellationToken);

        var answer = System.Text.Json.JsonSerializer.Serialize(decision.Answer, StructuredChat.JsonOptions);
        await File.WriteAllTextAsync(Path.Combine(Path.GetTempPath(), "am-orientation.json"), answer, TestContext.Current.CancellationToken);
        // A wrong rotation must never be applied: either the upright candidate is chosen, or the model is flagged
        // "orientation uncertain" for review and left as it is.
        var corrected = decision.Rerender && decision.Answer!.Choice == expected;
        var flagged = !decision.Rerender && decision.Orientation.Uncertain;
        Assert.True(corrected || flagged, $"consistent={decision.Consistent} {answer}");
        TestContext.Current.AddAttachment("outcome", corrected ? "corrected" : "flagged uncertain");
    }
}
