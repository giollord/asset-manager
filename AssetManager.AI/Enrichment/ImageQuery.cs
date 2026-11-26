using AssetManager.AI.Runtime;
using AssetManager.Core.Domain;
using AssetManager.Processing.Handlers;
using AssetManager.Processing.Settings;
using ImageMagick;
using Microsoft.Extensions.AI;

namespace AssetManager.AI.Enrichment;

/// <summary>A search-by-image query held in memory (design D6): the image's description vector and palette.</summary>
/// <param name="Id">The id in the search token (<see cref="Core.Search.SearchQuery.ImageQueryId"/>).</param>
/// <param name="ThumbnailDataUrl">A small copy of the image for the query chip.</param>
public sealed record ImageQuery(string Id, float[] Vector, IReadOnlyList<ColorInfo> Palette, string Description, IReadOnlyList<string> Tags,
    string ThumbnailDataUrl);

/// <summary>Thrown when a search token names a query image that is no longer in memory (e.g. after a restart).</summary>
public sealed class ImageQueryExpiredException()
    : InvalidOperationException("The query image has expired (the app was restarted); drop or paste the image again.");

/// <summary>The last few image queries, in memory only and never written to the catalog (least recently used goes first).</summary>
public sealed class ImageQueryCache
{
    public const int Capacity = 8;

    private readonly LinkedList<ImageQuery> _items = new();
    private readonly Lock _lock = new();

    public void Add(ImageQuery query)
    {
        lock (_lock)
        {
            var existing = _items.FirstOrDefault(q => q.Id == query.Id);
            if (existing is not null) _items.Remove(existing);
            _items.AddFirst(query);
            while (_items.Count > Capacity) _items.RemoveLast();
        }
    }

    public ImageQuery? Get(string id)
    {
        lock (_lock)
        {
            var node = _items.First;
            while (node is not null && node.Value.Id != id) node = node.Next;
            if (node is null) return null;
            _items.Remove(node);
            _items.AddFirst(node);
            return node.Value;
        }
    }

    public int Count
    {
        get { lock (_lock) return _items.Count; }
    }
}

/// <summary>
/// Search by image (design D6): the image is resized to at most 768 px and described once by the chat model with the
/// item prompt; the description, tags and type are embedded like an asset document. Costs one chat request.
/// </summary>
public sealed class ImageQueryService(EmbeddingService embeddings, ImageQueryCache cache, AiQueue? queue = null, AiRuntime? runtime = null)
{
    public const int MaxEdge = 768;

    /// <summary>The chat client (tests replace it).</summary>
    public Func<IChatClient?> Chat { get; init; } = () => queue?.Chat;

    public Func<AiModalities> Modalities { get; init; } = () => runtime?.Chat.Modalities ?? new AiModalities(false, false);

    /// <summary>
    /// Both the chat model (with vision) and the embedding model are needed; they may not be loaded yet, since models
    /// load when first needed (design D16).
    /// </summary>
    public bool IsAvailable => (runtime is { } r ? r.VisionAvailable : Chat() is not null && Modalities().Vision) && embeddings.IsAvailable;

    /// <summary>Describes and embeds the image, keeps it in the cache and returns it.</summary>
    public async Task<ImageQuery> CreateAsync(byte[] imageBytes, CancellationToken ct = default)
    {
        if (runtime is not null && Chat() is null) await runtime.EnsureChatAsync(ct);
        if (Chat() is not { } chat || !Modalities().Vision || !embeddings.IsAvailable)
            throw new InvalidOperationException("Search by image needs the AI model with vision and the embedding model (Settings → AI model).");

        var (webp, thumb, palette) = await Task.Run(() =>
        {
            using var image = new MagickImage(imageBytes);
            image.AutoOrient();
            if (Math.Max(image.Width, image.Height) > MaxEdge) image.Resize(new MagickGeometry(MaxEdge, MaxEdge));
            var colors = ImageHandler.Palette(image, 5);
            var bytes = image.ToByteArray(MagickFormat.WebP);
            using var small = (MagickImage)image.Clone();
            small.Resize(new MagickGeometry(96, 96));
            return (bytes, "data:image/webp;base64," + Convert.ToBase64String(small.ToByteArray(MagickFormat.WebP)), colors);
        }, ct);

        // One item, described with the same prompt and schema as catalog images.
        var asset = new Asset { Name = "query image", Type = AssetType.Image };
        var input = new ItemInput(asset, "Name: query image\nType: Image\n(an image the user searches with)",
            [new PreviewBlob { Kind = PreviewKind.Thumbnail, Mime = "image/webp", Bytes = webp }]);
        var request = ItemPass.Plan([input], Modalities()).Single();
        var outcome = (await ItemPass.RunAsync(new StructuredChat(chat), request, new ItemContext("Search by image"), new PreviewOptions(), ct)).Single();

        var described = new Asset { Name = "image", Type = outcome.Result.Type, Description = outcome.Result.Description };
        var text = EmbeddingService.DocumentText(described, outcome.Tags);
        var vector = await embeddings.EmbedDocumentAsync(text, ct)
                     ?? throw new InvalidOperationException("The embedding model is not running.");
        var query = new ImageQuery(Guid.NewGuid().ToString("N")[..12], vector, palette, outcome.Result.Description, outcome.Tags, thumb);
        cache.Add(query);
        return query;
    }
}
