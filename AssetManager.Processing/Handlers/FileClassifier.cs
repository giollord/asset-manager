using AssetManager.Core.Domain;
using AssetManager.Processing.Settings;

namespace AssetManager.Processing.Handlers;

public enum FileKind
{
    /// <summary>Becomes (part of) an asset.</summary>
    Asset,

    /// <summary>Code or project file: listed in the source's script manifest, not imported.</summary>
    Noted,

    /// <summary>System or temp file: skipped silently.</summary>
    Ignored,

    /// <summary>License/readme/credits or link shortcut: read for license discovery, not imported as an asset.</summary>
    SourceDocument,
}

public sealed record FileClass(FileKind Kind, AssetType Type, string? Reason = null)
{
    public static FileClass Of(AssetType type) => new(FileKind.Asset, type);
}

/// <summary>Classifies files by extension, falling back to a content signature for unknown extensions.</summary>
public sealed class FileClassifier(ClassificationOptions options)
{
    private static readonly Dictionary<string, AssetType> ByExtension = Build(
        (AssetType.Image, ".png .jpg .jpeg .gif .bmp .webp .tga .psd .psb .exr .hdr .dds .tif .tiff .svg .ico .ai .eps .kra .xcf .pcx .heic .avif"),
        (AssetType.Model3D, ".fbx .obj .dae .gltf .glb .3ds .ply .stl .blend .max .ma .mb .c4d .x .lwo .lws .3mf .usd .usda .usdc .usdz .off .ase .ms3d .md2 .md3 .ifc .vox"),
        (AssetType.Audio, ".wav .ogg .mp3 .flac .aif .aiff .m4a .wma .opus .mid .midi .mod .xm .it .s3m"),
        (AssetType.Font, ".ttf .otf .woff .woff2 .fnt .ttc .bdf"),
        (AssetType.Video, ".mp4 .mov .webm .avi .mkv .wmv .m4v .mpg .mpeg .ogv .flv"),
        (AssetType.Material, ".mat .mtl .sbsar .mtlx"),
        // Unity prefabs are cataloged so they can be exported with their references (design D8).
        (AssetType.Prefab, ".prefab"),
        (AssetType.Document, ".txt .md .pdf .html .htm .rtf .doc .docx .odt .url .webloc"));

    private static readonly string[] SourceDocumentNames = ["license", "licence", "readme", "read me", "read_me", "credits", "copying", "attribution", "eula", "terms"];

    private readonly HashSet<string> _scripts = new(options.ScriptExtensions, StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _ignoredExt = new(options.IgnoredExtensions, StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _ignoredNames = new(options.IgnoredNames, StringComparer.OrdinalIgnoreCase);

    /// <param name="relativePath">Path within the source ('/' or '\'), used for folder hints such as "Textures/".</param>
    /// <param name="head">The first bytes of the file (optional), for files with unknown extensions.</param>
    public FileClass Classify(string relativePath, ReadOnlySpan<byte> head = default)
    {
        var name = Path.GetFileName(relativePath.Replace('\\', '/').Split('/')[^1]);
        var ext = Path.GetExtension(name).ToLowerInvariant();
        var segments = relativePath.Replace('\\', '/').Split('/');

        if (_ignoredNames.Contains(name) || name.StartsWith("._", StringComparison.Ordinal) ||
            segments.Any(s => _ignoredNames.Contains(s)) || _ignoredExt.Contains(ext))
            return new FileClass(FileKind.Ignored, AssetType.Other, "ignored");
        if (_scripts.Contains(ext)) return new FileClass(FileKind.Noted, AssetType.Other, "script (noted)");

        if (ByExtension.TryGetValue(ext, out var type))
        {
            if (type == AssetType.Document && IsSourceDocument(name, ext))
                return new FileClass(FileKind.SourceDocument, AssetType.Document, "source document");
            if (type == AssetType.Image && LooksLikeTexture(name, segments)) type = AssetType.Texture;
            return FileClass.Of(type);
        }

        return Sniff(head) is { } sniffed ? FileClass.Of(sniffed) : FileClass.Of(AssetType.Other);
    }

    private static bool IsSourceDocument(string name, string ext)
    {
        if (ext is ".url" or ".webloc") return true;
        var stem = Path.GetFileNameWithoutExtension(name).ToLowerInvariant();
        return SourceDocumentNames.Any(stem.StartsWith) || stem.Contains("license") || stem.Contains("licence");
    }

    private static readonly string[] TextureSuffixes =
        ["_albedo", "_diffuse", "_basecolor", "_base_color", "_color", "_col", "_normal", "_nrm", "_norm", "_n", "_roughness", "_rough",
         "_metallic", "_metalness", "_metal", "_ao", "_occlusion", "_height", "_disp", "_displacement", "_specular", "_spec",
         "_gloss", "_emissive", "_emission", "_mask", "_orm", "_mra", "_d"];

    private static bool LooksLikeTexture(string name, string[] segments)
    {
        var stem = Path.GetFileNameWithoutExtension(name).ToLowerInvariant();
        if (TextureSuffixes.Any(s => stem.EndsWith(s, StringComparison.Ordinal))) return true;
        return segments.Take(segments.Length - 1).Any(s =>
            s.Contains("texture", StringComparison.OrdinalIgnoreCase) || s.Equals("materials", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("tex", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Content signatures for files without a known extension.</summary>
    public static AssetType? Sniff(ReadOnlySpan<byte> h)
    {
        if (h.Length < 4) return null;
        if (h.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47]) || h.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]) ||
            h.StartsWith("GIF8"u8) || h.StartsWith("BM"u8) || h.StartsWith("8BPS"u8) || h.StartsWith("DDS "u8) ||
            (h.Length >= 12 && h.StartsWith("RIFF"u8) && h[8..12].SequenceEqual("WEBP"u8)))
            return AssetType.Image;
        if ((h.Length >= 12 && h.StartsWith("RIFF"u8) && h[8..12].SequenceEqual("WAVE"u8)) || h.StartsWith("OggS"u8) ||
            h.StartsWith("ID3"u8) || h.StartsWith("fLaC"u8) || h.StartsWith("FORM"u8) || (h[0] == 0xFF && (h[1] & 0xE0) == 0xE0))
            return AssetType.Audio;
        if (h.StartsWith("glTF"u8) || h.StartsWith("Kaydara FBX Binary"u8)) return AssetType.Model3D;
        if (h.StartsWith((ReadOnlySpan<byte>)[0x00, 0x01, 0x00, 0x00]) || h.StartsWith("OTTO"u8) || h.StartsWith("wOFF"u8) || h.StartsWith("wOF2"u8))
            return AssetType.Font;
        if (h.StartsWith("%PDF"u8)) return AssetType.Document;
        if (h.Length >= 8 && h[4..8].SequenceEqual("ftyp"u8)) return AssetType.Video;
        if (h.StartsWith((ReadOnlySpan<byte>)[0x1A, 0x45, 0xDF, 0xA3])) return AssetType.Video;
        return null;
    }

    private static Dictionary<string, AssetType> Build(params (AssetType Type, string Exts)[] groups)
    {
        var d = new Dictionary<string, AssetType>(StringComparer.OrdinalIgnoreCase);
        foreach (var (t, exts) in groups)
            foreach (var e in exts.Split(' ', StringSplitOptions.RemoveEmptyEntries)) d[e] = t;
        return d;
    }

    /// <summary>Reads the first bytes of a file for <see cref="Classify"/>.</summary>
    public static byte[] ReadHead(string path, int count = 32)
    {
        using var fs = File.OpenRead(path);
        var buf = new byte[count];
        return buf[..fs.ReadAtLeast(buf, count, throwOnEndOfStream: false)];
    }

    internal static string Describe(FileClass c) => c.Kind == FileKind.Asset ? c.Type.ToString() : c.Reason ?? c.Kind.ToString();
}
