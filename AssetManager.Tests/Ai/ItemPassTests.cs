using System.Text.Json;
using AssetManager.AI.Enrichment;
using AssetManager.AI.Runtime;
using AssetManager.Core.Domain;
using AssetManager.Processing.Handlers;
using AssetManager.Processing.Settings;
using AssetManager.Tests.Fixtures;
using AssetManager.Tests.Processing;
using ImageMagick;
using ImageMagick.Drawing;
using Microsoft.Extensions.AI;

namespace AssetManager.Tests.Ai;

[Collection(AiModelCollection.Name)]
public class ItemPassTests
{
    private static readonly PreviewOptions Options = new();
    private static readonly AiModalities VisionOnly = new(true, false);

    private static byte[] Webp(MagickColor color, int size = 64, Action<MagickImage>? draw = null)
    {
        using var img = new MagickImage(MagickColors.Transparent, (uint)size, (uint)size);
        new Drawables().FillColor(color).Circle(size / 2.0, size / 2.0, size / 2.0, size * 0.1).Draw(img);
        draw?.Invoke(img);
        return img.ToByteArray(MagickFormat.WebP);
    }

    private static PreviewBlob Blob(PreviewKind kind, byte[] bytes, int index = 0, string mime = "image/webp") =>
        new() { Kind = kind, Index = index, Mime = mime, Bytes = bytes };

    private static ItemInput Item(string name, AssetType type, params PreviewBlob[] previews) =>
        new(new Asset { Name = name, Type = type }, $"Name: {name}\nType: {type}", previews);

    private static string Answer(params (int Index, string Type)[] items) =>
        JsonSerializer.Serialize(new
        {
            items = items.Select(i => new
            {
                index = i.Index, description = $"Item {i.Index}.", type = i.Type, tags = new[] { "Tag", "tag " }, colors = new[] { "Dark Green", "mossy green", "glitter" },
                style = new[] { "Cartoon" }, is_game_asset = true,
            }),
        });

    [Fact]
    public void Plans_sheets_models_and_audio_by_modality()
    {
        var images = Enumerable.Range(1, 10).Select(i => Item($"img{i}", AssetType.Image, Blob(PreviewKind.Thumbnail, [1]))).ToList();
        var model = Item("tree", AssetType.Model3D, Blob(PreviewKind.ModelView, [1], 9), Blob(PreviewKind.Thumbnail, [1]));
        var sound = Item("boom", AssetType.Audio, Blob(PreviewKind.AudioClip, [1], mime: "audio/ogg"), Blob(PreviewKind.Spectrogram, [1]));
        var noPreview = Item("data", AssetType.Other);
        List<ItemInput> all = [.. images, model, sound, noPreview];

        var plan = ItemPass.Plan(all, new AiModalities(true, true));
        // Models and audio get their own requests; the remaining images and the text-only item share the last sheet.
        Assert.Equal([ItemRequestKind.Sheet, ItemRequestKind.Model, ItemRequestKind.Audio, ItemRequestKind.Sheet], plan.Select(r => r.Kind));
        Assert.Equal([8, 1, 1, 3], plan.Select(r => r.Items.Count));
        Assert.Equal(Enumerable.Range(1, 8), plan[0].Items.Select(i => i.Label));
        Assert.Equal(AiInputMode.Audio, plan[2].Items[0].Mode);
        Assert.Equal([AiInputMode.Visual, AiInputMode.Visual, AiInputMode.TextOnly], plan[3].Items.Select(i => i.Mode));

        var visionOnly = ItemPass.Plan([sound], VisionOnly);
        Assert.Equal((ItemRequestKind.Sheet, AiInputMode.Spectrogram), (visionOnly[0].Kind, visionOnly[0].Items[0].Mode));

        var textOnly = ItemPass.Plan([model, sound], AiModalities.None);
        Assert.Equal(ItemRequestKind.Sheet, Assert.Single(textOnly).Kind);
        Assert.All(textOnly[0].Items, i => Assert.Equal(AiInputMode.TextOnly, i.Mode));
    }

    [Fact]
    public async Task Audio_fallback_uses_the_spectrogram_and_records_the_mode()
    {
        using var dir = FixtureBuilder.TempDir();
        var item = ImageHandlerTests.Item(await AudioHandlerTests.ToneAsync(dir, "Explosion_01.wav", 2), AssetType.Audio);
        var previews = (await new AudioHandler().GeneratePreviewsAsync(item, Options, TestContext.Current.CancellationToken))
            .Select(p => Blob(p.Kind, p.Bytes, p.Index, p.Mime)).ToList();
        var input = new ItemInput(item.Asset, "Name: Explosion_01\nType: Audio\nMetadata: 2 s, 44.1 kHz", previews);
        var client = new FakeChatClient(Answer((1, "audio")));

        var request = Assert.Single(ItemPass.Plan([input], VisionOnly));
        var outcomes = await ItemPass.RunAsync(new StructuredChat(client), request, new ItemContext("Sci-fi SFX"), Options, TestContext.Current.CancellationToken);
        ItemPass.Apply(outcomes[0]);

        var message = client.Requests[0].Messages[^1];
        Assert.Contains("spectrogram tile 1", message.Text);
        Assert.Contains("Explosion_01", message.Text);
        var image = Assert.Single(message.Contents.OfType<DataContent>());
        Assert.Equal("image/jpeg", image.MediaType);
        Assert.Equal(AiInputMode.Spectrogram, item.Asset.AiInputMode);
        Assert.Equal(AiStatus.Enriched, item.Asset.AiStatus);
        Assert.Empty(item.Asset.Colors.Where(c => c.Name == "glitter"));
    }

    [Fact]
    public async Task Audio_capable_model_gets_the_clip_as_wav()
    {
        using var dir = FixtureBuilder.TempDir();
        var item = ImageHandlerTests.Item(await AudioHandlerTests.ToneAsync(dir, "beep.wav", 1), AssetType.Audio);
        var previews = (await new AudioHandler().GeneratePreviewsAsync(item, Options, TestContext.Current.CancellationToken))
            .Select(p => Blob(p.Kind, p.Bytes, p.Index, p.Mime)).ToList();

        var request = Assert.Single(ItemPass.Plan([new ItemInput(item.Asset, "Name: beep", previews)], new AiModalities(true, true)));
        var prompt = await ItemPass.BuildPromptAsync(request, new ItemContext(), Options, TestContext.Current.CancellationToken);

        var audio = Assert.Single(prompt[0].Contents.OfType<DataContent>());
        Assert.Equal("audio/wav", audio.MediaType);
        Assert.Equal("RIFF"u8.ToArray(), audio.Data.ToArray()[..4]);
    }

    [Fact]
    public async Task Sheet_and_model_requests_carry_one_composed_image()
    {
        var items = new[] { MagickColors.Red, MagickColors.Green, MagickColors.Blue }
            .Select((c, i) => Item($"ball{i}", AssetType.Image, Blob(PreviewKind.Thumbnail, Webp(c)))).ToList();
        var sheet = Assert.Single(ItemPass.Plan(items, VisionOnly));
        var prompt = await ItemPass.BuildPromptAsync(sheet, new ItemContext("Balls", "Colored balls.", "Use the tag 'ball'."), Options, TestContext.Current.CancellationToken);
        var data = Assert.Single(prompt[0].Contents.OfType<DataContent>());
        using (var img = new MagickImage(data.Data.ToArray()))
            Assert.Equal((768u, 256u), (img.Width, img.Height));
        Assert.Contains("Instruction from the user (follow it): Use the tag 'ball'.", prompt[0].Text);
        Assert.Contains("Item 3 [tile 3]:", prompt[0].Text);

        var views = Enumerable.Range(0, 24).Select(i => Blob(PreviewKind.ModelView, Webp(MagickColors.Gray), i)).ToArray();
        Assert.Equal([9, 11, 13, 15], ItemPass.PickModelViews(views, Options).Select(v => v.Index));
        var model = Assert.Single(ItemPass.Plan([Item("tree", AssetType.Model3D, views)], VisionOnly));
        var modelPrompt = await ItemPass.BuildPromptAsync(model, new ItemContext(), Options, TestContext.Current.CancellationToken);
        using var modelImage = new MagickImage(Assert.Single(modelPrompt[0].Contents.OfType<DataContent>()).Data.ToArray());
        Assert.Equal((512u, 512u), (modelImage.Width, modelImage.Height));
    }

    [Fact]
    public void Apply_respects_user_edits_and_type_families()
    {
        var edited = new Asset { Type = AssetType.Texture, Description = "Mine", UserEdited = EditedFields.Description | EditedFields.Type };
        ItemPass.Apply(new ItemOutcome(edited, new ItemResult { Description = "AI", Type = AssetType.SpriteSheet, Colors = ["red"] }, AiInputMode.Visual));
        Assert.Equal(("Mine", AssetType.Texture), (edited.Description, edited.Type));
        Assert.Equal("#D21E23", Assert.Single(edited.Colors).Hex);

        var palette = new Asset { Type = AssetType.Image, Colors = [new ColorInfo("#000000", "black", 1)] };
        ItemPass.Apply(new ItemOutcome(palette, new ItemResult { Description = "x", Type = AssetType.SpriteSheet, Colors = ["red"] }, AiInputMode.Visual));
        Assert.Equal(AssetType.SpriteSheet, palette.Type);
        Assert.Equal("black", Assert.Single(palette.Colors).Name); // measured palette wins over named colors

        Assert.False(ItemPass.CanRefine(AssetType.Model3D, AssetType.Image));
        Assert.False(ItemPass.CanRefine(AssetType.Other, AssetType.Model3D));
        Assert.True(ItemPass.CanRefine(AssetType.Audio, AssetType.Music));
        Assert.True(ItemPass.CanRefine(AssetType.Other, AssetType.Font));

        var outcome = new ItemOutcome(new Asset(), new ItemResult { Tags = ["Tag", "tag ", "Rock"] }, AiInputMode.TextOnly);
        Assert.Equal(["tag", "rock"], outcome.Tags);
        var failed = new Asset();
        ItemPass.MarkFailed([failed], "Invalid AI output");
        Assert.Equal((AiStatus.Failed, "Invalid AI output"), (failed.AiStatus, failed.AiError));
    }

    /// <summary>Manual check: non-English names yield English output; audio goes in directly when supported.</summary>
    [AiModelFact]
    public async Task Real_model_answers_in_english_for_german_names_and_hears_audio()
    {
        Assert.SkipWhen(AiModelFactAttribute.Mmproj is null, "Set ASSETMANAGER_AI_MMPROJ to run.");
        using var dir = FixtureBuilder.TempDir();
        await using var server = await AiTestServer.StartAsync(vision: true);
        var chat = new StructuredChat(server.Client);

        var items = new List<ItemInput>
        {
            Item("Roter_Ball", AssetType.Image, Blob(PreviewKind.Thumbnail, Webp(MagickColors.Red, 128))),
            Item("Grüner_Baum_Symbol", AssetType.Image, Blob(PreviewKind.Thumbnail, Webp(MagickColors.ForestGreen, 128,
                img => new Drawables().FillColor(MagickColors.SaddleBrown).Rectangle(56, 80, 72, 127).Draw(img)))),
            Item("Blaues_Quadrat", AssetType.Texture, Blob(PreviewKind.Thumbnail, Webp(MagickColors.Blue, 128,
                img => new Drawables().FillColor(MagickColors.Blue).Rectangle(0, 0, 127, 127).Draw(img)))),
        };
        var tone = ImageHandlerTests.Item(await AudioHandlerTests.ToneAsync(dir, "Piepton.wav", 2), AssetType.Audio);
        var clipPreviews = (await new AudioHandler().GeneratePreviewsAsync(tone, Options, TestContext.Current.CancellationToken))
            .Select(p => Blob(p.Kind, p.Bytes, p.Index, p.Mime)).ToArray();
        items.Add(new ItemInput(tone.Asset, "Name: Piepton\nType: Audio\nFile: Geräusche/Piepton.wav", clipPreviews));

        var outcomes = new List<ItemOutcome>();
        foreach (var request in ItemPass.Plan(items, server.Host.Modalities))
            outcomes.AddRange(await ItemPass.RunAsync(chat, request, new ItemContext("Testpaket"), Options, TestContext.Current.CancellationToken));

        var report = JsonSerializer.Serialize(outcomes.Select(o => new { o.Asset.Name, o.Mode, o.Result }), StructuredChat.JsonOptions);
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "am-item-pass.json"), report);
        Assert.Equal(4, outcomes.Count);
        Assert.Equal(server.Host.Modalities.Audio ? AiInputMode.Audio : AiInputMode.Spectrogram, outcomes.Single(o => o.Asset.Name == "Piepton").Mode);
        Assert.Contains("red", outcomes.Single(o => o.Asset.Name == "Roter_Ball").Result.Description, StringComparison.OrdinalIgnoreCase);
        string[] german = ["roter", "ball_", "grüner", "baum", "blaues", "quadrat", "piepton"];
        Assert.All(outcomes.SelectMany(o => o.Tags), t => Assert.DoesNotContain(german, g => t.Contains(g)));
    }
}
