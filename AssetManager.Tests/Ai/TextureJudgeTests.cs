using AssetManager.AI.Enrichment;
using AssetManager.AI.Runtime;
using AssetManager.Core.Domain;
using AssetManager.Processing.Handlers;
using AssetManager.Processing.Settings;
using ImageMagick;
using Microsoft.Extensions.AI;

namespace AssetManager.Tests.Ai;

/// <summary>The AI texture choice (design D20, spec "Choosing a model's texture", "Texture choice consistency", "Attaching AI texture matches").</summary>
public class TextureJudgeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly AiModalities Vision = new(true, false);

    private static byte[] Webp()
    {
        using var img = new MagickImage(MagickColors.Gray, 8, 8);
        return img.ToByteArray(MagickFormat.WebP);
    }

    private static byte[] Jpeg()
    {
        using var img = new MagickImage(MagickColors.Gray, 8, 8);
        return img.ToByteArray(MagickFormat.Jpeg);
    }

    /// <summary>Plans what it is given, renders gray JPEGs, and records attachments.</summary>
    private sealed class FakeMatcher(params MaterialToMatch[] plan) : ITextureMatcher
    {
        public bool CanRender { get; init; } = true;
        public int Renders { get; private set; }
        public List<(string Material, long FileId, CompanionOrigin Origin, string? Missing)> Attached { get; } = [];

        public Task<IReadOnlyList<MaterialToMatch>> PlanAsync(long assetId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<MaterialToMatch>>(plan);

        public Task<IReadOnlyList<byte[]>> RenderCandidatesAsync(long assetId, MaterialToMatch match, CancellationToken ct = default)
        {
            Renders++;
            return Task.FromResult<IReadOnlyList<byte[]>>(Enumerable.Range(0, match.Candidates.Count + 1).Select(_ => Jpeg()).ToList());
        }

        public Task AttachAsync(Asset asset, string material, long fileId, CompanionOrigin origin, string? missingReference, CancellationToken ct = default)
        {
            Attached.Add((material, fileId, origin, missingReference));
            asset.TexturesMissing = false;
            return Task.CompletedTask;
        }

        public Task<int> SuggestFromPackAsync(long sourceId, CancellationToken ct = default) => Task.FromResult(0);
    }

    private sealed class CountingRerenderer : IPreviewRerenderer
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<PreviewBlob>> RerenderAsync(Asset asset, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<PreviewBlob>>([new PreviewBlob { Kind = PreviewKind.ModelView, Mime = "image/webp", Bytes = Webp() }]);
        }
    }

    /// <summary>The bark of scenario "Two materials": the leaves were matched by name at import.</summary>
    private static readonly MaterialToMatch Bark = new("Bark", null,
    [
        new TextureCandidate(11, "Textures/Tree_Bark.jpg", 2.1, true),
        new TextureCandidate(12, "Textures/Birch_Bark.png", 1.1, true),
        new TextureCandidate(13, "Textures/Color Variations/Bark_Dead.png", 1.0, true),
    ]);

    private static string Answer(int choice, double confidence) =>
        $$"""{"observations":["a","b","c"],"choice":{{choice}},"confidence":{{confidence.ToString(System.Globalization.CultureInfo.InvariantCulture)}},"reason":"bark follows the trunk"}""";

    private const string ItemAnswer = """{"items":[{"index":1,"description":"A tree.","type":"model3_d","tags":["tree"],"colors":[],"style":[],"is_game_asset":true}]}""";

    private static ItemInput Tree()
    {
        var asset = new Asset { Id = 5, Name = "Tree_3", Type = AssetType.Model3D, Model = new ModelMetadata() };
        return new ItemInput(asset, "Name: Tree_3\nType: Model3D", [new PreviewBlob { Kind = PreviewKind.ModelView, Mime = "image/webp", Bytes = Webp() }]);
    }

    private static async Task<(ModelEnrichment.Result Result, FakeChatClient Chat, CountingRerenderer Rerenderer)> RunAsync(
        ItemInput input, FakeMatcher matcher, AiModalities modalities, TextureMatchOptions? options, params string[] replies)
    {
        var chat = new FakeChatClient(replies);
        var rerenderer = new CountingRerenderer();
        var result = await ModelEnrichment.EnrichAsync(new StructuredChat(chat), input, new ItemContext(), modalities, new PreviewOptions(), rerenderer,
            ct: Ct, textures: matcher, textureOptions: options);
        return (result, chat, rerenderer);
    }

    /// <summary>Scenario "Two materials": the AI picks Tree_Bark twice (shown 3rd when reversed) and it is attached before the description.</summary>
    [Fact]
    public async Task Confident_consistent_choice_is_attached_and_the_model_re_rendered()
    {
        var matcher = new FakeMatcher(Bark);
        var (result, chat, rerenderer) = await RunAsync(Tree(), matcher, Vision, null, Answer(1, 0.9), Answer(3, 0.85), ItemAnswer);

        Assert.Equal([("Bark", 11L, CompanionOrigin.Ai, (string?)null)], matcher.Attached);
        Assert.Equal(1, rerenderer.Calls);
        Assert.Equal(0.85, Assert.Single(result.Textures).Confidence);
        Assert.Equal(3, chat.Requests.Count); // two texture questions, then the item pass
        var first = chat.Requests[0].Messages.Last();
        Assert.Equal(4, first.Contents.OfType<DataContent>().Count()); // the model as it is + 3 candidates
        Assert.Contains("Candidate 1: Textures/Tree_Bark.jpg", first.Text);
        Assert.Contains("Candidate 1: Textures/Color Variations/Bark_Dead.png", chat.Requests[1].Messages.Last().Text); // reversed

        // Design D8: the text before the model's own lines is the same for every model, so the server reuses it.
        var fixedPart = first.Text[..(first.Text.IndexOf(TextureJudge.FixedEnd, StringComparison.Ordinal) + TextureJudge.FixedEnd.Length)];
        Assert.StartsWith(fixedPart, chat.Requests[1].Messages.Last().Text);
        Assert.DoesNotContain("Bark", fixedPart);
        Assert.DoesNotContain("Tree", fixedPart);
    }

    /// <summary>Scenario "Atlas shared by a pack": one candidate for a missing reference; no second question is needed.</summary>
    [Fact]
    public async Task Missing_reference_is_filled_and_textures_missing_cleared()
    {
        var input = Tree();
        input.Asset.TexturesMissing = true;
        var palette = new MaterialToMatch("Palette", "palette.png", [new TextureCandidate(21, "Textures/PolygonFarm_Texture_01.png", 0.5, false)]);
        var matcher = new FakeMatcher(palette);

        var (_, chat, rerenderer) = await RunAsync(input, matcher, Vision, null, Answer(1, 0.92), ItemAnswer);

        Assert.Equal([("Palette", 21L, CompanionOrigin.Ai, "palette.png")], matcher.Attached);
        Assert.False(input.Asset.TexturesMissing);
        Assert.Equal((2, 1), (chat.Requests.Count, rerenderer.Calls));
    }

    /// <summary>Scenario "Unsure choice".</summary>
    [Fact]
    public async Task Unsure_choice_becomes_a_suggestion()
    {
        var input = Tree();
        var matcher = new FakeMatcher(Bark);

        var (_, _, rerenderer) = await RunAsync(input, matcher, Vision, null, Answer(2, 0.5), Answer(2, 0.55), ItemAnswer);

        Assert.Empty(matcher.Attached);
        Assert.Equal(0, rerenderer.Calls);
        var s = Assert.Single(input.Asset.Model!.TextureSuggestions);
        Assert.Equal(("Bark", 12L, "Textures/Birch_Bark.png", 0.5), (s.Material, s.FileId, s.RelativePath, s.Confidence));
    }

    /// <summary>Scenario "None fits".</summary>
    [Fact]
    public async Task None_fits_changes_nothing()
    {
        var input = Tree();
        var matcher = new FakeMatcher(Bark);

        var (_, chat, rerenderer) = await RunAsync(input, matcher, Vision, null, Answer(0, 0.9), ItemAnswer);

        Assert.Empty(matcher.Attached);
        Assert.Empty(input.Asset.Model!.TextureSuggestions);
        Assert.Equal((2, 0), (chat.Requests.Count, rerenderer.Calls)); // no second question for "none"
    }

    /// <summary>Scenario "Order changes the answer".</summary>
    [Fact]
    public async Task Disagreeing_answers_attach_nothing()
    {
        var input = Tree();
        var matcher = new FakeMatcher(Bark);

        // Candidate 2 (Birch_Bark) first; reversed, candidate 3 is Tree_Bark: another texture.
        var (result, _, rerenderer) = await RunAsync(input, matcher, Vision, null, Answer(2, 0.95), Answer(3, 0.95), ItemAnswer);

        Assert.Empty(matcher.Attached);
        Assert.Empty(input.Asset.Model!.TextureSuggestions);
        Assert.Equal(0, Assert.Single(result.Textures).Confidence);
        Assert.Equal(0, rerenderer.Calls);
    }

    [Fact]
    public async Task Without_image_input_the_best_candidate_is_only_suggested()
    {
        var input = Tree();
        var matcher = new FakeMatcher(Bark);

        var (_, chat, _) = await RunAsync(input, matcher, AiModalities.None, null, ItemAnswer);

        Assert.Equal(0, matcher.Renders);
        Assert.Empty(matcher.Attached);
        Assert.Equal(("Bark", 11L), (input.Asset.Model!.TextureSuggestions.Single().Material, input.Asset.Model.TextureSuggestions.Single().FileId));
        Assert.Single(chat.Requests); // only the item pass
    }

    [Fact]
    public async Task Turned_off_or_user_edited_textures_are_not_matched()
    {
        var matcher = new FakeMatcher(Bark);
        await RunAsync(Tree(), matcher, Vision, new TextureMatchOptions(Enabled: false), ItemAnswer);

        var edited = Tree();
        edited.Asset.UserEdited |= EditedFields.Textures;
        await RunAsync(edited, matcher, Vision, null, ItemAnswer);

        Assert.Equal(0, matcher.Renders);
        Assert.Empty(matcher.Attached);
    }
}
