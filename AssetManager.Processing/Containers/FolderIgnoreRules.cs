using System.Text.RegularExpressions;

namespace AssetManager.Processing.Containers;

/// <summary>
/// Folders an import skips (spec "Ignored folders", "Ignore rule matching"). A rule without a slash matches a folder of
/// that name at any depth (<c>node_modules</c>); a rule with a slash matches a folder path from the root of the
/// imported folder, archive or package (<c>Assets/*/Demo</c>). Case-insensitive; <c>*</c> and <c>?</c> are wildcards
/// within one path segment.
/// </summary>
public sealed class FolderIgnoreRules
{
    public static readonly FolderIgnoreRules None = new([]);

    private readonly List<(string Rule, Regex Name)> _names = [];
    private readonly List<(string Rule, Regex[] Segments)> _paths = [];

    public FolderIgnoreRules(IEnumerable<string> rules)
    {
        foreach (var rule in rules.Select(Normalize).Where(r => r.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var segments = rule.Split('/');
            if (segments.Length == 1) _names.Add((rule, Glob(rule)));
            else _paths.Add((rule, segments.Select(Glob).ToArray()));
        }
    }

    public bool IsEmpty => _names.Count == 0 && _paths.Count == 0;

    /// <summary>"\Assets\Demo\" → "Assets/Demo": '/' separators, no leading or trailing slash, trimmed.</summary>
    public static string Normalize(string rule) => rule.Trim().Replace('\\', '/').Trim('/').Trim();

    /// <summary>The rule that ignores the folder at <paramref name="folderPath"/> itself (not its parents), or null.</summary>
    public string? Match(string folderPath)
    {
        var segments = Split(folderPath);
        if (segments.Length == 0) return null;
        foreach (var (rule, name) in _names)
            if (name.IsMatch(segments[^1])) return rule;
        foreach (var (rule, pattern) in _paths)
            if (pattern.Length == segments.Length && pattern.Zip(segments).All(p => p.First.IsMatch(p.Second))) return rule;
        return null;
    }

    /// <summary>
    /// For a file path, the outermost folder above it that a rule ignores, as (folder path, rule); null when the file is
    /// not in an ignored folder.
    /// </summary>
    public (string Folder, string Rule)? IgnoredAncestor(string filePath)
    {
        if (IsEmpty) return null;
        var segments = Split(filePath);
        for (var i = 1; i < segments.Length; i++)
        {
            var folder = string.Join('/', segments, 0, i);
            if (Match(folder) is { } rule) return (folder, rule);
        }
        return null;
    }

    private static string[] Split(string path) => path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);

    private static Regex Glob(string segment) =>
        new("^" + Regex.Escape(segment).Replace(@"\*", "[^/]*").Replace(@"\?", "[^/]") + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
