using System.Text.RegularExpressions;

namespace AssetManager.Processing.Handlers;

/// <summary>What a texture is for, judged from its name.</summary>
public enum TextureMapKind { BaseColor, Normal, Emissive, Other }

/// <summary>
/// Texture and material name rules for matching textures to models (design D20). Names are compared as "keys": lower
/// case, without an image extension, a common prefix (<c>T_</c>, <c>SM_</c>, <c>SK_</c>…) or a map suffix
/// (<c>_Albedo</c>, <c>_Normal</c>…), and without separators, so <c>SK_Orc</c>, material <c>Orc</c>,
/// <c>T_Orc_Albedo.png</c> and <c>orc.jpg</c> all have the key <c>orc</c>.
/// </summary>
public static partial class TextureNames
{
    private static readonly HashSet<string> Prefixes = ["t", "tx", "tex", "sm", "sk", "m", "mi", "mat"];

    private static readonly HashSet<string> ImageExtensions =
        [".png", ".jpg", ".jpeg", ".tga", ".psd", ".tif", ".tiff", ".bmp", ".dds", ".webp", ".exr"];

    /// <summary>The name's tokens: lower case, split at separators, without the image extension.</summary>
    public static List<string> Tokens(string name)
    {
        var file = name.Replace('\\', '/');
        file = file[(file.LastIndexOf('/') + 1)..];
        if (ImageExtensions.Contains(Path.GetExtension(file).ToLowerInvariant())) file = Path.GetFileNameWithoutExtension(file);
        return Separator().Split(file.ToLowerInvariant()).Where(t => t.Length > 0).ToList();
    }

    /// <summary>The tokens without a leading prefix and trailing map suffixes (kept whole when nothing would be left).</summary>
    public static List<string> CoreTokens(string name)
    {
        var tokens = Tokens(name);
        var core = tokens.ToList();
        if (core.Count > 1 && Prefixes.Contains(core[0])) core.RemoveAt(0);
        // "_Normal", "_Base_Color", "_Ambient_Occlusion", "_Texture_Diff"…: drop suffix words from the end.
        while (core.Count > 1 && (SuffixSlot(core[^1]) is not null || SuffixWords.Contains(core[^1]) && core.Count < tokens.Count))
            core.RemoveAt(core.Count - 1);
        return core.Count > 0 ? core : tokens;
    }

    /// <summary>The comparison key (see the class summary).</summary>
    public static string Key(string name) => string.Concat(CoreTokens(name));

    /// <summary>What a texture is for: a map suffix decides; a name without one is a base colour.</summary>
    public static TextureMapKind KindOf(string name)
    {
        var tokens = Tokens(name);
        if (tokens.Count < 2) return TextureMapKind.BaseColor;
        var slot = SuffixSlot(tokens[^2] + "_" + tokens[^1]) ?? SuffixSlot(tokens[^1]);
        return slot switch
        {
            null => TextureMapKind.BaseColor,
            { Kind: "map" } => TextureMapKind.BaseColor,
            { Kind: "normalMap" } => TextureMapKind.Normal,
            { Kind: "emissiveMap" } => TextureMapKind.Emissive,
            _ => TextureMapKind.Other,
        };
    }

    /// <summary>
    /// The one base-colour texture whose key equals the key of one of <paramref name="names"/> (tried in order), or null
    /// when none does or several do. The same image in several formats in one folder counts once (PNG preferred).
    /// </summary>
    public static SourceFile? UniqueMatch(IReadOnlyList<SourceFile> textures, IEnumerable<string> names)
    {
        foreach (var name in names)
        {
            var key = Key(name);
            if (key.Length < 2) continue;
            var groups = textures.Where(t => KindOf(t.FileName) == TextureMapKind.BaseColor && Key(t.FileName) == key)
                .GroupBy(t => (t.Folder.ToLowerInvariant(), t.Stem.ToLowerInvariant())).ToList();
            if (groups.Count == 1) return groups[0].OrderBy(t => t.Extension == ".png" ? 0 : 1).First();
        }
        return null;
    }

    /// <summary>The normal and emissive maps with the same key as <paramref name="baseColor"/> (nearest folder first).</summary>
    public static IEnumerable<(SourceFile File, string Slot)> CompanionMaps(IReadOnlyList<SourceFile> textures, SourceFile baseColor)
    {
        var key = Key(baseColor.FileName);
        foreach (var (kind, slot) in new[] { (TextureMapKind.Normal, "normalMap"), (TextureMapKind.Emissive, "emissiveMap") })
        {
            var map = textures.Where(t => !ReferenceEquals(t, baseColor) && KindOf(t.FileName) == kind && Key(t.FileName) == key)
                .OrderByDescending(t => string.Equals(t.Folder, baseColor.Folder, StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault();
            if (map is not null) yield return (map, slot);
        }
    }

    /// <summary>First words of two-word suffixes ("base color", "ambient occlusion"), dropped after the second.</summary>
    private static readonly HashSet<string> SuffixWords = ["base", "ambient", "albedo", "metallic", "mask"];

    private static (string Suffix, string Kind)? SuffixSlot(string token)
    {
        foreach (var (suffix, slot) in CompanionResolver.Suffixes)
            if (suffix == token) return (suffix, slot ?? "other");
        return null;
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex Separator();
}
