using AssetManager.Core.Domain;
using AssetManager.Processing.Media;
using ImageMagick;

namespace AssetManager.Processing.Export;

/// <summary>An original as the in-app viewer shows it: the file's bytes, or a lossless/full-length conversion.</summary>
/// <param name="Converted">True when the bytes are a conversion (e.g. TGA → PNG, AIFF → Opus), not the file itself.</param>
public sealed record OriginalContent(byte[] Bytes, string Mime, string FileName, bool Converted);

/// <summary>
/// Reads an asset's original for the in-app viewer (spec "Open original"): images at full resolution, audio and video
/// at full length. Formats the browser shows natively are passed through; other images become PNG (Magick) and other
/// audio becomes Opus (FFmpeg). 3D models use <see cref="Handlers.PreviewService.ViewerRequestAsync"/> instead.
/// </summary>
public sealed class OriginalMedia(AssetExporter exporter)
{
    private static readonly Dictionary<string, string> WebImages = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".gif"] = "image/gif", [".webp"] = "image/webp",
        [".bmp"] = "image/bmp", [".svg"] = "image/svg+xml",
    };

    private static readonly Dictionary<string, string> WebAudio = new(StringComparer.OrdinalIgnoreCase)
    {
        [".wav"] = "audio/wav", [".mp3"] = "audio/mpeg", [".ogg"] = "audio/ogg", [".oga"] = "audio/ogg", [".opus"] = "audio/ogg",
        [".flac"] = "audio/flac", [".m4a"] = "audio/mp4", [".aac"] = "audio/aac", [".weba"] = "audio/webm",
    };

    private static readonly Dictionary<string, string> WebVideo = new(StringComparer.OrdinalIgnoreCase)
    {
        [".mp4"] = "video/mp4", [".m4v"] = "video/mp4", [".webm"] = "video/webm", [".ogv"] = "video/ogg",
    };

    /// <summary>What the viewer can show for an asset type: image, audio, video or a 3D model; null for "open externally" only.</summary>
    public static string? ViewKind(AssetType type) => type switch
    {
        AssetType.Image or AssetType.Texture or AssetType.SpriteSheet => "image",
        AssetType.Audio or AssetType.Music => "audio",
        AssetType.Video => "video",
        AssetType.Model3D or AssetType.Animation => "model",
        _ => null,
    };

    public async Task<OriginalContent> LoadAsync(long assetId, AssetType type, CancellationToken ct = default)
    {
        using var file = await exporter.ResolvePrimaryAsync(assetId, ct);
        var name = Path.GetFileName(file.Path);
        var ext = Path.GetExtension(file.Path);
        switch (ViewKind(type))
        {
            case "image":
                if (WebImages.TryGetValue(ext, out var imageMime)) return new(await File.ReadAllBytesAsync(file.Path, ct), imageMime, name, false);
                return await Task.Run(() =>
                {
                    // PSD and other layered formats: the first image is the flattened composite.
                    using var image = new MagickImage(file.Path);
                    image.Format = MagickFormat.Png;
                    return new OriginalContent(image.ToByteArray(), "image/png", name, true);
                }, ct);
            case "audio":
                if (WebAudio.TryGetValue(ext, out var audioMime)) return new(await File.ReadAllBytesAsync(file.Path, ct), audioMime, name, false);
                using (var tmp = Ffmpeg.CreateTemp())
                {
                    var output = tmp.File("full.ogg");
                    await Ffmpeg.RunAsync(["-i", file.Path, "-vn", "-c:a", "libopus", "-b:a", "160k", "-f", "ogg", output], ct);
                    return new(await File.ReadAllBytesAsync(output, ct), "audio/ogg", name, true);
                }
            case "video":
                if (WebVideo.TryGetValue(ext, out var videoMime)) return new(await File.ReadAllBytesAsync(file.Path, ct), videoMime, name, false);
                throw new NotSupportedException($"{ext} videos cannot be played in the app; use \"Open externally\".");
            default:
                throw new NotSupportedException("This asset type has no in-app viewer; use \"Open externally\".");
        }
    }
}
