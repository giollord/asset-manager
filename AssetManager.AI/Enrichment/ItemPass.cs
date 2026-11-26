using System.Text;
using AssetManager.AI.Runtime;
using AssetManager.Core.Domain;
using AssetManager.Processing.Handlers;
using AssetManager.Processing.Media;
using AssetManager.Processing.Settings;
using AssetManager.Processing.Sprites;
using ImageMagick;
using ImageMagick.Drawing;
using Microsoft.Extensions.AI;

namespace AssetManager.AI.Enrichment;

/// <summary>One asset to enrich: its text summary (from the type handler) and its stored previews.</summary>
public sealed record ItemInput(Asset Asset, string Text, IReadOnlyList<PreviewBlob> Previews);

/// <summary>Source context shared by the items of one pack.</summary>
/// <param name="SuggestedTags">The catalog's suggested tag vocabulary (design D15); answers are folded onto it.</param>
public sealed record ItemContext(string? PackName = null, string? PackDescription = null, string? Instruction = null, IReadOnlyList<string>? SuggestedTags = null);

public enum ItemRequestKind { Sheet, Model, Audio }

/// <summary>An item inside a request: its label number, how the model sees it, and the preview used.</summary>
public sealed record PlannedItem(int Label, ItemInput Input, AiInputMode Mode, PreviewBlob? Image);

public sealed record ItemRequest(ItemRequestKind Kind, IReadOnlyList<PlannedItem> Items);

/// <summary>The validated answer for one asset, ready to apply.</summary>
public sealed record ItemOutcome(Asset Asset, ItemResult Result, AiInputMode Mode)
{
    /// <summary>The AI tags, with "pixel art" following the measurement for images (design D4).</summary>
    public IReadOnlyList<string> Tags => PixelArtRules.Tags(Asset, AiText.NormalizeTags(Result.Tags));
}

/// <summary>
/// The item pass (design D7): images, sprites and textures go in contact sheets of up to 8 labeled tiles per request;
/// each 3D model gets its own request with 4 views; audio is sent as audio when the model accepts it, otherwise as
/// its spectrogram. The input mode is recorded on every asset.
/// </summary>
public static class ItemPass
{
    public const int SheetSize = 8;
    private const int Tile = 256;
    private const int SheetColumns = 4;
    private const int ModelViews = 4;

    public static IReadOnlyList<ItemRequest> Plan(IReadOnlyList<ItemInput> items, AiModalities modalities, int sheetSize = SheetSize)
    {
        var requests = new List<ItemRequest>();
        var sheet = new List<(ItemInput Input, AiInputMode Mode, PreviewBlob? Image)>();

        void Flush()
        {
            if (sheet.Count == 0) return;
            requests.Add(new ItemRequest(ItemRequestKind.Sheet, sheet.Select((s, i) => new PlannedItem(i + 1, s.Input, s.Mode, s.Image)).ToList()));
            sheet.Clear();
        }

        foreach (var item in items)
        {
            var type = item.Asset.Type;
            if (type == AssetType.Model3D && modalities.Vision && item.Previews.Any(p => p.Kind == PreviewKind.ModelView))
            {
                requests.Add(new ItemRequest(ItemRequestKind.Model, [new PlannedItem(1, item, AiInputMode.Visual, null)]));
                continue;
            }
            if (type is AssetType.Audio or AssetType.Music)
            {
                var clip = item.Previews.FirstOrDefault(p => p.Kind == PreviewKind.AudioClip);
                if (modalities.Audio && clip is not null)
                {
                    requests.Add(new ItemRequest(ItemRequestKind.Audio, [new PlannedItem(1, item, AiInputMode.Audio, clip)]));
                    continue;
                }
                var spectrogram = modalities.Vision ? item.Previews.FirstOrDefault(p => p.Kind == PreviewKind.Spectrogram) : null;
                sheet.Add((item, spectrogram is null ? AiInputMode.TextOnly : AiInputMode.Spectrogram, spectrogram));
            }
            else
            {
                var image = modalities.Vision ? VisualPreview(item.Previews) : null;
                sheet.Add((item, image is null ? AiInputMode.TextOnly : AiInputMode.Visual, image));
            }
            if (sheet.Count >= sheetSize) Flush();
        }
        Flush();
        return requests;
    }

    public static async Task<List<ChatMessage>> BuildPromptAsync(ItemRequest request, ItemContext context, PreviewOptions options, CancellationToken ct = default)
    {
        // Order matters for speed (design D8): the text up to the pack context is identical for every request of one
        // source, so llama-server reuses it from its cache; only what follows (the items and their media) is new.
        var sb = new StringBuilder();
        AppendSharedPrefix(sb, context);

        var contents = new List<AIContent>();
        switch (request.Kind)
        {
            case ItemRequestKind.Sheet:
                var tiles = request.Items.Where(i => i.Image is not null).ToList();
                if (tiles.Count > 0)
                    sb.AppendLine($"The image is a contact sheet; each tile is labeled with the item number in its top-left corner{(tiles.Any(t => t.Mode == AiInputMode.Spectrogram) ? " (sounds are shown as spectrograms: time left to right, frequency bottom to top)" : "")}.");
                break;
            case ItemRequestKind.Model:
                sb.AppendLine($"The image shows the 3D model from {ModelViews} sides (front, right, back, left), in that order left to right, top to bottom.");
                break;
            case ItemRequestKind.Audio:
                sb.AppendLine("The audio clip is the first seconds of the sound.");
                break;
        }
        sb.AppendLine(request.Items.Count == 1 ? "Enrich this game asset." : $"Enrich these {request.Items.Count} game assets.");
        sb.AppendLine();
        foreach (var item in request.Items)
        {
            var how = item.Mode switch
            {
                AiInputMode.Visual when request.Kind == ItemRequestKind.Sheet => " [tile " + item.Label + "]",
                AiInputMode.Spectrogram => " [spectrogram tile " + item.Label + "]",
                AiInputMode.TextOnly => " [no preview]",
                _ => "",
            };
            sb.AppendLine($"Item {item.Label}{how}:");
            foreach (var line in item.Input.Text.TrimEnd().Split('\n')) sb.AppendLine("  " + line.TrimEnd('\r'));
        }
        contents.Add(new TextContent(sb.ToString()));

        switch (request.Kind)
        {
            case ItemRequestKind.Sheet when request.Items.Any(i => i.Image is not null):
                contents.Add(new DataContent(ContactSheet(request.Items.Where(i => i.Image is not null).Select(i => (i.Label.ToString(), i.Image!.Bytes)).ToList(), labels: true), "image/jpeg"));
                break;
            case ItemRequestKind.Model:
                var views = PickModelViews(request.Items[0].Input.Previews, options);
                contents.Add(new DataContent(ContactSheet(views.Select(v => ("", v.Bytes)).ToList(), labels: false, columns: 2), "image/jpeg"));
                break;
            case ItemRequestKind.Audio:
                contents.Add(new DataContent(await ToWavAsync(request.Items[0].Image!.Bytes, ct), "audio/wav"));
                break;
        }
        return [new ChatMessage(ChatRole.User, contents)];
    }

    /// <summary>
    /// The start of every item prompt for one source: fixed rules first, then the source's own context (pack, user
    /// instruction, suggested tags). It does not depend on the request, so the server's prompt cache reuses it (design D8).
    /// </summary>
    public static void AppendSharedPrefix(StringBuilder sb, ItemContext context)
    {
        sb.AppendLine("Describe what each item actually shows or sounds like. Names and paths may be in any language: translate their meaning, answer in English.");
        sb.AppendLine("Tags are separate English words or short phrases about content, material, theme and use; never copy a file name into tags.");
        sb.AppendLine("Type \"animation\" is a motion clip (e.g. a walk cycle or an attack), even when the file shows a stand-in character; describe the motion.");
        if (context.PackName is not null) sb.AppendLine($"The items come from the pack \"{context.PackName}\".");
        if (!string.IsNullOrWhiteSpace(context.PackDescription)) sb.AppendLine($"Pack description: {context.PackDescription.Trim()}");
        if (!string.IsNullOrWhiteSpace(context.Instruction)) sb.AppendLine($"Instruction from the user (follow it): {context.Instruction.Trim()}");
        if (context.SuggestedTags is { Count: > 0 } suggested) sb.AppendLine(SuggestedTags.PromptLine(suggested));
        sb.AppendLine();
    }

    public static async Task<IReadOnlyList<ItemOutcome>> RunAsync(StructuredChat chat, ItemRequest request, ItemContext context, PreviewOptions options, CancellationToken ct = default)
    {
        var prompt = await BuildPromptAsync(request, context, options, ct);
        var labels = request.Items.Select(i => i.Label).ToList();
        var batch = await chat.AskAsync<ItemBatchResult>(prompt, r => { r.Expected = labels; return []; }, ct);
        return request.Items.Select(i => new ItemOutcome(i.Input.Asset, Fold(batch.Items.First(r => r.Index == i.Label), context.SuggestedTags), i.Mode)).ToList();
    }

    /// <summary>Folds near-synonyms in the answer's tags and style onto the suggested tags ("lowpoly" → "low poly").</summary>
    public static ItemResult Fold(ItemResult result, IReadOnlyList<string>? suggested) => suggested is not { Count: > 0 } ? result : result with
    {
        Tags = SuggestedTags.Fold(AiText.NormalizeTags(result.Tags), suggested),
        Style = SuggestedTags.Fold(AiText.NormalizeTags(result.Style), suggested),
    };

    /// <summary>
    /// Writes the answer to the asset, leaving user-edited fields alone. The type only changes within its family
    /// (image/texture/sprite sheet, audio/music) or away from Other. Tags are applied by the caller with origin AI.
    /// </summary>
    public static void Apply(ItemOutcome outcome)
    {
        var (asset, r, mode) = outcome;
        if (!asset.UserEdited.HasFlag(EditedFields.Description) && !string.IsNullOrWhiteSpace(r.Description)) asset.Description = r.Description.Trim();
        // Sprite sheet or not is decided by slicing data and the grid detector; the AI's word is only a hint (design D3).
        if (!asset.UserEdited.HasFlag(EditedFields.Type) && CanRefine(asset.Type, r.Type) && r.Type != AssetType.SpriteSheet && asset.Type != AssetType.SpriteSheet)
            asset.Type = r.Type;
        // "pixel art" in the style follows the measurement for images; the AI's pixel-art answer is only a hint (design D4).
        if (!asset.UserEdited.HasFlag(EditedFields.Style)) asset.Style = PixelArtRules.Style(asset, AiText.NormalizeTags(r.Style));
        if (!asset.UserEdited.HasFlag(EditedFields.Colors) && asset.Colors.Count == 0)
            asset.Colors = r.Colors.Select(c => c.Trim().ToLowerInvariant()).Distinct().Take(4)
                .Select(c => ColorNames.HexOf(c) is { } hex ? new ColorInfo(hex, c, 0) : null).OfType<ColorInfo>().ToList();
        asset.IsGameAsset = r.IsGameAsset;
        asset.AiStatus = AiStatus.Enriched;
        asset.AiInputMode = mode;
        asset.AiError = null;
        asset.EmbeddingDirty = true;
    }

    /// <summary>The AI's sprite-sheet answer as a detector hint; calling an image a sprite sheet counts as "likely".</summary>
    public static SpriteSheetHint? SpriteHint(ItemResult r) =>
        r.SpriteSheet is { Likely: true } s ? new SpriteSheetHint(true, Positive(s.Columns), Positive(s.Rows), Positive(s.Frames))
        : r.Type == AssetType.SpriteSheet ? new SpriteSheetHint(true)
        : null;

    private static int? Positive(int? v) => v is > 0 and <= 1024 ? v : null;

    public static void MarkFailed(IEnumerable<Asset> assets, string error)
    {
        foreach (var a in assets)
        {
            a.AiStatus = AiStatus.Failed;
            a.AiError = error;
        }
    }

    public static bool CanRefine(AssetType from, AssetType to)
    {
        if (from == to || from == AssetType.Prefab || to == AssetType.Prefab) return false; // a prefab is known by its extension
        if (from == AssetType.Other) return to is not (AssetType.Model3D or AssetType.Animation); // these need model metadata and renders
        if (from == AssetType.Model3D) return to == AssetType.Animation; // the file holds a clip; an animation never turns back into a model
        AssetType[][] families = [[AssetType.Image, AssetType.Texture, AssetType.SpriteSheet], [AssetType.Audio, AssetType.Music]];
        return families.Any(f => f.Contains(from) && f.Contains(to));
    }

    private static PreviewBlob? VisualPreview(IReadOnlyList<PreviewBlob> previews) =>
        previews.FirstOrDefault(p => p.Kind == PreviewKind.Thumbnail && p.Mime.StartsWith("image/"))
        ?? previews.FirstOrDefault(p => p.Kind == PreviewKind.UnityPreview && p.Mime.StartsWith("image/"));

    /// <summary>Four views around the primary ring, starting at the primary azimuth (front, right, back, left).</summary>
    public static IReadOnlyList<PreviewBlob> PickModelViews(IReadOnlyList<PreviewBlob> previews, PreviewOptions options)
    {
        var views = previews.Where(p => p.Kind == PreviewKind.ModelView).ToDictionary(p => p.Index);
        var az = options.AzimuthsPerRing;
        var picked = Enumerable.Range(0, ModelViews)
            .Select(k => options.PrimaryRing * az + (options.PrimaryAzimuth + k * az / ModelViews) % az)
            .Select(i => views.GetValueOrDefault(i)).OfType<PreviewBlob>().ToList();
        return picked.Count > 0 ? picked : views.Values.OrderBy(v => v.Index).Take(ModelViews).ToList();
    }

    /// <summary>Tiles images on a mid-gray sheet (transparent areas become gray), optionally numbered; JPEG.</summary>
    public static byte[] ContactSheet(IReadOnlyList<(string Label, byte[] Bytes)> images, bool labels, int columns = SheetColumns)
    {
        var cols = Math.Min(columns, images.Count);
        var rows = (images.Count + cols - 1) / cols;
        using var sheet = new MagickImage(new MagickColor(128, 128, 128), (uint)(cols * Tile), (uint)(rows * Tile));
        for (var i = 0; i < images.Count; i++)
        {
            using var tile = new MagickImage(images[i].Bytes);
            tile.Resize(new MagickGeometry(Tile - 8, Tile - 8));
            var x = i % cols * Tile + (Tile - (int)tile.Width) / 2;
            var y = i / cols * Tile + (Tile - (int)tile.Height) / 2;
            sheet.Composite(tile, x, y, CompositeOperator.Over);
            if (labels)
            {
                var (lx, ly) = (i % cols * Tile, i / cols * Tile);
                new Drawables()
                    .FillColor(MagickColors.Black).Rectangle(lx, ly, lx + 44, ly + 36)
                    .FillColor(MagickColors.White).FontPointSize(28).Text(lx + 6, ly + 29, images[i].Label)
                    .Draw(sheet);
            }
        }
        sheet.Quality = 88;
        return sheet.ToByteArray(MagickFormat.Jpeg);
    }

    /// <summary>The stored clip is Ogg/Opus; llama.cpp decodes WAV/MP3/FLAC, so convert to 16 kHz mono WAV.</summary>
    public static async Task<byte[]> ToWavAsync(byte[] clip, CancellationToken ct)
    {
        using var temp = Ffmpeg.CreateTemp();
        var input = temp.File("clip.ogg");
        var output = temp.File("clip.wav");
        await File.WriteAllBytesAsync(input, clip, ct);
        await Ffmpeg.RunAsync(["-i", input, "-ac", "1", "-ar", "16000", "-y", output], ct);
        return await File.ReadAllBytesAsync(output, ct);
    }
}
