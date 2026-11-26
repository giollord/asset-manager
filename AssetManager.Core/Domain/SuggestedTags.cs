using System.Text;

namespace AssetManager.Core.Domain;

/// <summary>
/// The catalog's suggested tag vocabulary (design D15): generic tags the AI is asked to use instead of near-synonyms,
/// so the same concept always gets the same tag. Stored as a catalog setting; a missing value means <see cref="Default"/>.
/// </summary>
public static class SuggestedTags
{
    /// <summary>Key of the catalog setting.</summary>
    public const string SettingKey = "SuggestedTags";

    /// <summary>The built-in list (spec "Built-in list"): dimension, style, view, kind, technical.</summary>
    public static readonly IReadOnlyList<string> Default =
    [
        "2d", "3d",
        "pixel art", "low poly", "hand-painted", "realistic", "stylized", "cartoon",
        "top-down", "isometric", "side-view",
        "ui", "icon", "tileset", "character", "environment", "prop", "vfx",
        "seamless", "pbr", "animated", "rigged",
    ];

    /// <summary>The prompt line for item and pack requests.</summary>
    public static string PromptLine(IEnumerable<string> tags) =>
        "Preferred tags (use these exact words whenever they apply, instead of synonyms; add other tags as needed): " + string.Join(", ", tags);

    /// <summary>
    /// Folds near-synonyms of suggested tags onto them: tags are compared with spaces, hyphens and underscores removed
    /// (and a trailing "style" ignored), so "lowpoly", "low-poly", "low_poly" and "low-poly style" all become "low poly".
    /// Other tags pass through. Order is kept and duplicates are dropped.
    /// </summary>
    public static List<string> Fold(IEnumerable<string> tags, IReadOnlyList<string>? suggested)
    {
        var byKey = new Dictionary<string, string>();
        foreach (var s in suggested ?? []) byKey.TryAdd(Key(s), s);
        var result = new List<string>();
        foreach (var tag in tags)
        {
            var folded = byKey.TryGetValue(Key(tag), out var hit) ? hit
                : StripStyle(tag) is { } bare && byKey.TryGetValue(Key(bare), out var hit2) ? hit2
                : tag;
            if (!result.Contains(folded)) result.Add(folded);
        }
        return result;
    }

    private static string? StripStyle(string tag)
    {
        var t = tag.Trim();
        return t.Length > 5 && t.EndsWith("style", StringComparison.OrdinalIgnoreCase) ? t[..^5] : null;
    }

    private static string Key(string tag)
    {
        var sb = new StringBuilder(tag.Length);
        foreach (var c in tag)
            if (!char.IsWhiteSpace(c) && c is not ('-' or '_')) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }
}
