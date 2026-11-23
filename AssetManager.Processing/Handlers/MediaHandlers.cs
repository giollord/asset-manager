using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AssetManager.Core.Domain;
using AssetManager.Processing.Media;
using AssetManager.Processing.Settings;
using ImageMagick;
using UglyToad.PdfPig;

namespace AssetManager.Processing.Handlers;

/// <summary>Fonts: family/style/glyph count from the OpenType tables, and a rendered specimen as preview.</summary>
public sealed class FontHandler : AssetTypeHandlerBase
{
    public const string Specimen = "Aa Bb Cc 123\nThe quick brown fox";

    public override string Name => "font";

    public override bool CanHandle(AssetType type) => type == AssetType.Font;

    public override Task ExtractMetadataAsync(HandlerItem item, PreviewOptions options, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            item.Asset.Font = item.Extension is ".ttf" or ".otf" or ".ttc"
                ? OpenType.Read(File.ReadAllBytes(item.PrimaryPath))
                : new FontMetadata();
        }, ct);

    public override Task<IReadOnlyList<PreviewOutput>> GeneratePreviewsAsync(HandlerItem item, PreviewOptions options, CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<PreviewOutput>>(() =>
        {
            if (item.Extension is not (".ttf" or ".otf" or ".ttc" or ".woff" or ".woff2")) return [];
            var settings = new MagickReadSettings
            {
                Font = item.PrimaryPath,
                FontPointsize = 48,
                BackgroundColor = MagickColors.Transparent,
                FillColor = MagickColors.Black,
                TextGravity = Gravity.West,
            };
            using var image = new MagickImage("label:" + Specimen, settings);
            image.Trim();
            image.BorderColor = MagickColors.Transparent;
            image.Border(12);
            if (Math.Max(image.Width, image.Height) > options.ImageMaxEdge)
                image.Resize(new MagickGeometry((uint)options.ImageMaxEdge, (uint)options.ImageMaxEdge));
            return [ImageHandler.Encode(image, PreviewKind.Thumbnail, options.ImageQuality)];
        }, ct);
}

/// <summary>Minimal OpenType reader: family and subfamily from 'name', glyph count from 'maxp'.</summary>
public static class OpenType
{
    public static FontMetadata Read(byte[] data)
    {
        var span = data.AsSpan();
        var offset = 0;
        if (span.Length >= 16 && span[..4].SequenceEqual("ttcf"u8)) offset = (int)BinaryPrimitives.ReadUInt32BigEndian(span[12..]); // first font
        if (span.Length < offset + 12) throw new InvalidDataException("Not an OpenType font.");
        var numTables = BinaryPrimitives.ReadUInt16BigEndian(span[(offset + 4)..]);
        int? nameAt = null, maxpAt = null;
        for (var i = 0; i < numTables; i++)
        {
            var rec = offset + 12 + i * 16;
            if (rec + 16 > span.Length) break;
            var tag = Encoding.ASCII.GetString(span.Slice(rec, 4));
            var at = (int)BinaryPrimitives.ReadUInt32BigEndian(span[(rec + 8)..]);
            if (tag == "name") nameAt = at;
            else if (tag == "maxp") maxpAt = at;
        }
        string? family = null, style = null, typoFamily = null, typoStyle = null;
        if (nameAt is { } n && n + 6 <= span.Length)
        {
            var count = BinaryPrimitives.ReadUInt16BigEndian(span[(n + 2)..]);
            var strings = n + BinaryPrimitives.ReadUInt16BigEndian(span[(n + 4)..]);
            for (var i = 0; i < count; i++)
            {
                var r = n + 6 + i * 12;
                if (r + 12 > span.Length) break;
                var platform = BinaryPrimitives.ReadUInt16BigEndian(span[r..]);
                var nameId = BinaryPrimitives.ReadUInt16BigEndian(span[(r + 6)..]);
                var len = BinaryPrimitives.ReadUInt16BigEndian(span[(r + 8)..]);
                var off = BinaryPrimitives.ReadUInt16BigEndian(span[(r + 10)..]);
                if (nameId is not (1 or 2 or 16 or 17) || strings + off + len > span.Length) continue;
                var raw = span.Slice(strings + off, len);
                var text = platform is 0 or 3 ? Encoding.BigEndianUnicode.GetString(raw) : Encoding.Latin1.GetString(raw);
                switch (nameId)
                {
                    case 1: family ??= text; break;
                    case 2: style ??= text; break;
                    case 16: typoFamily ??= text; break;
                    case 17: typoStyle ??= text; break;
                }
            }
        }
        int? glyphs = maxpAt is { } m && m + 6 <= span.Length ? BinaryPrimitives.ReadUInt16BigEndian(span[(m + 4)..]) : null;
        return new FontMetadata { Family = typoFamily ?? family, Style = typoStyle ?? style, GlyphCount = glyphs };
    }
}

/// <summary>Videos: ffprobe metadata and one frame (at 10 % of the duration, at most 1 s in) as the preview.</summary>
public sealed class VideoHandler : AssetTypeHandlerBase
{
    public override string Name => "video";

    public override bool CanHandle(AssetType type) => type == AssetType.Video;

    public override async Task ExtractMetadataAsync(HandlerItem item, PreviewOptions options, CancellationToken ct = default)
    {
        var a = await Ffmpeg.ProbeAsync(item.PrimaryPath, ct);
        var v = a.PrimaryVideoStream ?? throw new InvalidDataException("The file has no video stream.");
        item.Asset.Video = new VideoMetadata
        {
            DurationSeconds = Math.Round(a.Duration.TotalSeconds, 3),
            Width = v.Width ?? 0,
            Height = v.Height ?? 0,
            Codec = v.CodecName ?? "",
            FrameRate = v.AverageFrameRate > 0 ? Math.Round(v.AverageFrameRate, 3) : null,
        };
    }

    public override async Task<IReadOnlyList<PreviewOutput>> GeneratePreviewsAsync(HandlerItem item, PreviewOptions options, CancellationToken ct = default)
    {
        using var tmp = Ffmpeg.CreateTemp();
        var frame = tmp.File("frame.png");
        var at = Math.Min(1.0, (item.Asset.Video?.DurationSeconds ?? 0) * 0.1).ToString("0.###", CultureInfo.InvariantCulture);
        await Ffmpeg.RunAsync(["-ss", at, "-i", item.PrimaryPath, "-frames:v", "1", "-an", frame], ct);
        using var image = new MagickImage(frame);
        if (Math.Max(image.Width, image.Height) > options.VideoFrameMaxEdge)
            image.Resize(new MagickGeometry((uint)options.VideoFrameMaxEdge, (uint)options.VideoFrameMaxEdge));
        return [ImageHandler.Encode(image, PreviewKind.Thumbnail, options.ImageQuality)];
    }
}

/// <summary>Documents: format, page count (PDF) and a short text excerpt; the UI shows a placeholder preview.</summary>
public sealed partial class DocumentHandler : AssetTypeHandlerBase
{
    private const int ExcerptLength = 400;

    public override string Name => "document";

    public override bool CanHandle(AssetType type) => type == AssetType.Document;

    public override Task ExtractMetadataAsync(HandlerItem item, PreviewOptions options, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            int? pages = null;
            string? excerpt = null;
            switch (item.Extension)
            {
                case ".pdf":
                    using (var pdf = PdfDocument.Open(item.PrimaryPath))
                    {
                        pages = pdf.NumberOfPages;
                        excerpt = pages > 0 ? Clean(pdf.GetPage(1).Text) : null;
                    }
                    break;
                case ".txt" or ".md" or ".url" or ".webloc" or ".rtf":
                    excerpt = Clean(ReadText(item.PrimaryPath));
                    break;
                case ".html" or ".htm":
                    excerpt = Clean(Tags().Replace(Scripts().Replace(ReadText(item.PrimaryPath), " "), " "));
                    break;
            }
            item.Asset.Document = new DocumentMetadata
            {
                Format = item.Extension.TrimStart('.').ToUpperInvariant(),
                Pages = pages,
                Excerpt = string.IsNullOrWhiteSpace(excerpt) ? null : excerpt,
            };
        }, ct);

    public override Task<IReadOnlyList<PreviewOutput>> GeneratePreviewsAsync(HandlerItem item, PreviewOptions options, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<PreviewOutput>>([]);

    private static string ReadText(string path)
    {
        using var reader = new StreamReader(path, detectEncodingFromByteOrderMarks: true);
        var buffer = new char[64 * 1024];
        return new string(buffer, 0, reader.ReadBlock(buffer, 0, buffer.Length));
    }

    private static string Clean(string text)
    {
        var s = Whitespace().Replace(System.Net.WebUtility.HtmlDecode(text), " ").Trim();
        return s.Length <= ExcerptLength ? s : s[..ExcerptLength].TrimEnd() + "...";
    }

    [GeneratedRegex(@"<(script|style)[^>]*>.*?</\1>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex Scripts();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
