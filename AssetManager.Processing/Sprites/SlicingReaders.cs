using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using AssetManager.Core.Domain;
using YamlDotNet.RepresentationModel;

namespace AssetManager.Processing.Sprites;

/// <summary>
/// Slicing data that comes with an image (design D3 step 1, spec "Slicing data first"): a Unity <c>.meta</c> with sprite
/// mode Multiple, a TexturePacker JSON (hash or array) or XML (Starling/Sparrow, generic) atlas, or an Aseprite JSON,
/// with the same base name next to the image. Frames are in original pixels with y from the top.
/// </summary>
public static class SlicingReaders
{
    /// <summary>Looks for slicing data next to <paramref name="imagePath"/>; null when there is none or it does not fit the image.</summary>
    public static SpriteSheetMetadata? Find(string imagePath, int width, int height)
    {
        var dir = Path.GetDirectoryName(imagePath) ?? ".";
        var stem = Path.GetFileNameWithoutExtension(imagePath);
        var candidates = new (string Path, Func<string, List<SpriteFrame>?> Read)[]
        {
            (imagePath + ".meta", p => ReadUnityMeta(File.ReadAllText(p), height)),
            (Path.Combine(dir, stem + ".json"), p => ReadJson(File.ReadAllText(p))),
            (Path.Combine(dir, stem + ".xml"), p => ReadXml(File.ReadAllText(p))),
        };
        foreach (var (path, read) in candidates)
        {
            if (!File.Exists(path)) continue;
            List<SpriteFrame>? frames;
            try { frames = read(path); }
            catch (Exception e) when (e is IOException or JsonException or System.Xml.XmlException or YamlDotNet.Core.YamlException or FormatException
                                          or InvalidOperationException or KeyNotFoundException) { continue; }
            if (frames is { Count: > 0 } && frames.All(f => f.W > 0 && f.H > 0 && f.X >= 0 && f.Y >= 0 && f.X + f.W <= width && f.Y + f.H <= height))
                return FromFrames(frames);
        }
        return null;
    }

    /// <summary>Metadata from slicing frames, with the grid form when the frames lie on a regular grid.</summary>
    public static SpriteSheetMetadata FromFrames(List<SpriteFrame> frames) => new()
    {
        Frames = frames,
        Grid = GridOf(frames),
        FrameCount = frames.Count,
        Confidence = 1,
        DetectedBy = GridDetectedBy.SlicingData,
    };

    /// <summary>Unity <c>TextureImporter</c> with <c>spriteMode: 2</c>; Unity's y is bottom-up, so it is flipped.</summary>
    public static List<SpriteFrame>? ReadUnityMeta(string yaml, int imageHeight)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root) return null;
        if (Child(root, "TextureImporter") is not YamlMappingNode importer) return null;
        if (Scalar(importer, "spriteMode") != "2") return null;
        if (Child(importer, "spriteSheet") is not YamlMappingNode sheet || Child(sheet, "sprites") is not YamlSequenceNode sprites) return null;
        var frames = new List<SpriteFrame>();
        foreach (var node in sprites.Children.OfType<YamlMappingNode>())
        {
            if (Child(node, "rect") is not YamlMappingNode rect) continue;
            int x = Int(rect, "x"), y = Int(rect, "y"), w = Int(rect, "width"), h = Int(rect, "height");
            frames.Add(new SpriteFrame(Scalar(node, "name"), x, imageHeight - y - h, w, h));
        }
        return frames;
    }

    /// <summary>TexturePacker or Aseprite JSON, "hash" (frames as an object) or "array" (frames as a list).</summary>
    public static List<SpriteFrame>? ReadJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("frames", out var frames)) return null;
        var entries = frames.ValueKind switch
        {
            JsonValueKind.Object => frames.EnumerateObject().Select(p => (Name: (string?)p.Name, Value: p.Value)).ToList(),
            JsonValueKind.Array => frames.EnumerateArray().Select(e => (Name: e.TryGetProperty("filename", out var n) ? n.GetString() : null, Value: e)).ToList(),
            _ => null,
        };
        if (entries is null) return null;
        var result = new List<SpriteFrame>();
        foreach (var (name, value) in entries)
        {
            if (!value.TryGetProperty("frame", out var f)) continue;
            int x = f.GetProperty("x").GetInt32(), y = f.GetProperty("y").GetInt32(), w = f.GetProperty("w").GetInt32(), h = f.GetProperty("h").GetInt32();
            // A rotated frame is stored turned 90° in the sheet: its rectangle there is h × w.
            if (value.TryGetProperty("rotated", out var r) && r.ValueKind == JsonValueKind.True) (w, h) = (h, w);
            result.Add(new SpriteFrame(StripImageExtension(name), x, y, w, h));
        }
        return result;
    }

    /// <summary>Starling/Sparrow <c>&lt;SubTexture name x y width height&gt;</c> or TexturePacker's generic <c>&lt;sprite n x y w h&gt;</c>.</summary>
    public static List<SpriteFrame>? ReadXml(string xml)
    {
        var root = XDocument.Parse(xml).Root;
        if (root?.Name.LocalName != "TextureAtlas") return null;
        var result = new List<SpriteFrame>();
        foreach (var e in root.Elements())
        {
            var (name, w, h) = e.Name.LocalName switch
            {
                "SubTexture" => ((string?)e.Attribute("name"), Attr(e, "width"), Attr(e, "height")),
                "sprite" => ((string?)e.Attribute("n"), Attr(e, "w"), Attr(e, "h")),
                _ => (null, -1, -1),
            };
            if (w < 0) continue;
            if ((string?)e.Attribute("rotated") is "true" || (string?)e.Attribute("r") is "y") (w, h) = (h, w);
            result.Add(new SpriteFrame(StripImageExtension(name), Attr(e, "x"), Attr(e, "y"), w, h));
        }
        return result;
    }

    /// <summary>The regular grid the frames lie on (same size, evenly spaced), or null.</summary>
    public static SpriteGrid? GridOf(IReadOnlyList<SpriteFrame> frames)
    {
        if (frames.Count < 2) return null;
        int w = frames[0].W, h = frames[0].H;
        if (frames.Any(f => f.W != w || f.H != h)) return null;
        var xs = frames.Select(f => f.X).Distinct().Order().ToList();
        var ys = frames.Select(f => f.Y).Distinct().Order().ToList();
        if (Step(xs, w) is not { } sx || Step(ys, h) is not { } sy) return null;
        return new SpriteGrid
        {
            CellW = w, CellH = h, OffsetX = xs[0], OffsetY = ys[0], SpacingX = sx - w, SpacingY = sy - h,
            Columns = (xs[^1] - xs[0]) / sx + 1, Rows = (ys[^1] - ys[0]) / sy + 1,
        };
    }

    /// <summary>The common step of sorted positions (gaps may skip empty cells), or null when irregular.</summary>
    private static int? Step(List<int> positions, int size)
    {
        if (positions.Count == 1) return size;
        var step = Enumerable.Range(1, positions.Count - 1).Select(i => positions[i] - positions[i - 1]).Min();
        if (step < size) return null;
        return positions.All(p => (p - positions[0]) % step == 0) ? step : null;
    }

    private static string? StripImageExtension(string? name) =>
        name is null ? null : Path.GetExtension(name).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".webp" or ".gif" or ".tga" or ".bmp" ? name[..^Path.GetExtension(name).Length] : name;

    private static int Attr(XElement e, string name) => int.Parse((string?)e.Attribute(name) ?? "0", CultureInfo.InvariantCulture);

    private static YamlNode? Child(YamlMappingNode node, string key) =>
        node.Children.TryGetValue(new YamlScalarNode(key), out var v) ? v : null;

    private static string? Scalar(YamlMappingNode node, string key) => (Child(node, key) as YamlScalarNode)?.Value;

    private static int Int(YamlMappingNode node, string key) =>
        (int)Math.Round(double.Parse(Scalar(node, key) ?? "0", CultureInfo.InvariantCulture));
}
