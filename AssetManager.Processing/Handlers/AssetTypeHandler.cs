using System.Text;
using System.Text.Json;
using AssetManager.Core.Domain;
using AssetManager.Processing.Settings;

namespace AssetManager.Processing.Handlers;

/// <summary>
/// A companion file of the asset being processed (e.g. a model's texture), available locally. <see cref="Material"/>
/// and <see cref="Slot"/> ("map", "normalMap"…) are set when a reference (e.g. a Unity .mat) says where it belongs.
/// </summary>
public sealed record CompanionInput(AssetFile File, string LocalPath, string? Material = null, string? Slot = null);

/// <summary>The asset a handler fills in, plus local paths of its files.</summary>
public sealed class HandlerItem
{
    /// <summary>The asset being built or refreshed; handlers set its type-specific metadata.</summary>
    public required Asset Asset { get; init; }

    /// <summary>Readable local path of the primary file (live, cached extraction or stored copy).</summary>
    public required string PrimaryPath { get; init; }

    public IReadOnlyList<CompanionInput> Companions { get; init; } = [];

    /// <summary>Unity's embedded preview.png, when the file came from a .unitypackage.</summary>
    public string? UnityPreviewPath { get; init; }

    /// <summary>The primary file's name as catalogued (the local path may differ, e.g. a stored copy).</summary>
    public string FileName => Asset.PrimaryFile?.FileName is { Length: > 0 } n ? n : Path.GetFileName(PrimaryPath);

    public string Extension => Path.GetExtension(FileName).ToLowerInvariant();
}

/// <summary>A generated preview, stored as a <see cref="PreviewBlob"/>.</summary>
public sealed record PreviewOutput(PreviewKind Kind, int Index, string Mime, int Width, int Height, byte[] Bytes);

public sealed record AiImage(string Mime, byte[] Bytes, string Label);

/// <summary>What the AI receives for one asset: a text block and images (and optionally audio).</summary>
public sealed record AiInput(string Text, IReadOnlyList<AiImage> Images, string? AudioPath = null);

/// <summary>One handler per asset type (design D5). A new file type is one new handler class.</summary>
public interface IAssetTypeHandler
{
    string Name { get; }

    bool CanHandle(AssetType type);

    /// <summary>Reads technical metadata into the asset (<c>Image</c>, <c>Audio</c>, <c>Model</c>…), and may refine its type.</summary>
    Task ExtractMetadataAsync(HandlerItem item, PreviewOptions options, CancellationToken ct = default);

    /// <summary>Creates the previews for the asset; an empty list means "no preview" (the fallback chain applies).</summary>
    Task<IReadOnlyList<PreviewOutput>> GeneratePreviewsAsync(HandlerItem item, PreviewOptions options, CancellationToken ct = default);

    /// <summary>Builds the AI input from the stored metadata and previews.</summary>
    AiInput BuildAiInput(Asset asset, IReadOnlyList<PreviewBlob> previews);
}

/// <summary>Shared behaviour: a text summary of name, path and metadata plus the thumbnail as the AI image.</summary>
public abstract class AssetTypeHandlerBase : IAssetTypeHandler
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    public abstract string Name { get; }

    public abstract bool CanHandle(AssetType type);

    public abstract Task ExtractMetadataAsync(HandlerItem item, PreviewOptions options, CancellationToken ct = default);

    public abstract Task<IReadOnlyList<PreviewOutput>> GeneratePreviewsAsync(HandlerItem item, PreviewOptions options, CancellationToken ct = default);

    public virtual AiInput BuildAiInput(Asset asset, IReadOnlyList<PreviewBlob> previews)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Name: {asset.Name}");
        sb.AppendLine($"Type: {asset.Type}");
        foreach (var f in asset.Files) sb.AppendLine($"{f.Role} file: {(f.RelativePath.Length > 0 ? f.RelativePath : f.FileName)} ({f.Size} bytes)");
        if (Metadata(asset) is { } meta) sb.AppendLine($"Metadata: {JsonSerializer.Serialize(meta, meta.GetType(), Json)}");
        return new AiInput(sb.ToString(), AiImages(previews));
    }

    protected virtual object? Metadata(Asset asset) =>
        (object?)asset.Image ?? asset.Audio ?? (object?)asset.Model ?? asset.Font ?? (object?)asset.Video ?? asset.Document;

    protected virtual IReadOnlyList<AiImage> AiImages(IReadOnlyList<PreviewBlob> previews) =>
        previews.Where(p => p.Kind is PreviewKind.Thumbnail or PreviewKind.UnityPreview && p.Mime.StartsWith("image/"))
            .Take(1).Select(p => new AiImage(p.Mime, p.Bytes, "preview")).ToList();
}

/// <summary>Picks the handler for an asset type; the fallback handler takes everything else.</summary>
public sealed class HandlerRegistry(IEnumerable<IAssetTypeHandler> handlers)
{
    private readonly IReadOnlyList<IAssetTypeHandler> _handlers = handlers.ToList();

    public IAssetTypeHandler For(AssetType type) =>
        _handlers.FirstOrDefault(h => h is not FallbackHandler && h.CanHandle(type))
        ?? _handlers.OfType<FallbackHandler>().FirstOrDefault()
        ?? new FallbackHandler();
}
