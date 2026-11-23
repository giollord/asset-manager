using System.ComponentModel;
using AssetManager.Core.Domain;

namespace AssetManager.AI.Enrichment;

/// <summary>An AI answer that can check itself before it is persisted (design D7).</summary>
public interface IAiResult
{
    /// <summary>Problems with the answer, phrased so they can be sent back to the model; empty when valid.</summary>
    IEnumerable<string> Validate();
}

[Description("License terms found in the documents.")]
public sealed record LicenseResult
{
    [Description("Short license name in English, e.g. \"CC0\", \"CC BY 4.0\", \"Unity Asset Store EULA\", \"Royalty-free (custom)\", or \"unknown\".")]
    public string Kind { get; init; } = "unknown";

    [Description("Whether commercial use is allowed.")]
    public TriState Commercial { get; init; }

    [Description("Whether attribution/credit is required.")]
    public TriState Attribution { get; init; }

    [Description("The exact credit line to use when attribution is required, otherwise null.")]
    public string? AttributionText { get; init; }

    [Description("File name of the document the license terms come from, exactly as in its === header === line (e.g. \"license.pdf\"), or null when there is none.")]
    public string? Evidence { get; init; }

    public LicenseInfo ToLicenseInfo() => new()
    {
        Kind = string.IsNullOrWhiteSpace(Kind) || Kind.Equals("unknown", StringComparison.OrdinalIgnoreCase) ? null : Kind.Trim(),
        Commercial = Commercial,
        Attribution = Attribution,
        AttributionText = string.IsNullOrWhiteSpace(AttributionText) || Attribution == TriState.No ? null : AttributionText.Trim(),
        Evidence = string.IsNullOrWhiteSpace(Evidence) ? null : Evidence.Trim(),
    };
}

public sealed record LinkResult
{
    [Description("Absolute http(s) URL.")]
    public string Url { get; init; } = "";

    [Description("What the link is, in English, e.g. \"Publisher page\" or \"Documentation\".")]
    public string? Label { get; init; }

    [Description("File name of the document the URL was found in, as in its === header === line.")]
    public string? Evidence { get; init; }
}

[Description("Summary of a whole asset pack.")]
public sealed record PackResult : IAiResult
{
    [Description("2-4 sentence English description of what the pack contains and its visual/audio style.")]
    public string Description { get; init; } = "";

    public LicenseResult License { get; init; } = new();

    [Description("Related URLs (store page, author, documentation) found in the documents.")]
    public List<LinkResult> Links { get; init; } = [];

    [Description("English notes about code/project files in the pack (count and what they appear to do), or null when there are none.")]
    public string? ScriptNotes { get; init; }

    [Description("Style descriptors in English, e.g. \"low poly\", \"pixel art\", \"stylized\", \"realistic\".")]
    public List<string> Style { get; init; } = [];

    [Description("Up to 10 lowercase English tags that apply to every item in the pack.")]
    public List<string> Tags { get; init; } = [];

    public IEnumerable<string> Validate()
    {
        if (string.IsNullOrWhiteSpace(Description)) yield return "description must not be empty";
        foreach (var l in Links)
            if (!AiText.IsHttpUrl(l.Url)) yield return $"links: \"{l.Url}\" is not an absolute http(s) URL";
        if (Tags.Count > 10) yield return "tags: at most 10 tags";
    }
}

[Description("Enrichment of one item, identified by its label number.")]
public sealed record ItemResult
{
    [Description("The label number of the item in the request.")]
    public int Index { get; init; }

    [Description("1-2 sentence English description of what the item shows or sounds like.")]
    public string Description { get; init; } = "";

    [Description("Asset type; keep the given type unless it is clearly wrong.")]
    public AssetType Type { get; init; }

    [Description("3-12 lowercase English tags (objects, materials, theme, use); words, not file names.")]
    public List<string> Tags { get; init; } = [];

    [Description("Up to 4 named dominant colors in English, e.g. \"dark green\". Empty for audio.")]
    public List<string> Colors { get; init; } = [];

    [Description("Style descriptors in English.")]
    public List<string> Style { get; init; } = [];

    [Description("False when the item is not usable in a game (e.g. a store banner, a screenshot of an editor, a template).")]
    public bool IsGameAsset { get; init; } = true;
}

public sealed record ItemBatchResult : IAiResult
{
    /// <summary>Label numbers the request contained; set by the caller, not part of the schema.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyCollection<int>? Expected { get; set; }

    [Description("One entry per labeled item.")]
    public List<ItemResult> Items { get; init; } = [];

    public IEnumerable<string> Validate()
    {
        foreach (var i in Items)
        {
            if (string.IsNullOrWhiteSpace(i.Description)) yield return $"item {i.Index}: description must not be empty";
            if (i.Tags.Count > 12) yield return $"item {i.Index}: at most 12 tags";
        }
        foreach (var dup in Items.GroupBy(i => i.Index).Where(g => g.Count() > 1)) yield return $"item {dup.Key} appears more than once";
        if (Expected is null) yield break;
        var got = Items.Select(i => i.Index).ToHashSet();
        var missing = Expected.Where(e => !got.Contains(e)).ToList();
        var extra = got.Where(g => !Expected.Contains(g)).ToList();
        if (missing.Count > 0) yield return $"missing items: {string.Join(", ", missing)}";
        if (extra.Count > 0) yield return $"unknown items: {string.Join(", ", extra)}";
    }
}

public sealed record CollectionDescriptionResult : IAiResult
{
    [Description("2-4 sentence English description of the collection's content and style.")]
    public string Description { get; init; } = "";

    public IEnumerable<string> Validate()
    {
        if (string.IsNullOrWhiteSpace(Description)) yield return "description must not be empty";
    }
}

[Description("Which candidate rendering shows the model upright.")]
public sealed record OrientationResult : IAiResult
{
    /// <summary>Asked first so the model looks at every tile before choosing (small models choose much better this way).</summary>
    [Description("For each candidate 1-6, in a few words: which part of the object is at the top and which part touches the ground.")]
    public List<string> Observations { get; init; } = [];

    [Description("Number of the upright candidate (1-6), or 0 when the object has no natural up direction.")]
    public int Choice { get; init; }

    [Description("Extra rotation around the vertical axis so the front faces the camera: 0, 90, 180 or 270.")]
    public int YawFix { get; init; }

    [Description("Confidence from 0 to 1.")]
    public double Confidence { get; init; }

    public IEnumerable<string> Validate()
    {
        if (Observations.Count != 6) yield return "observations must have exactly 6 entries, one per candidate 1-6";
        if (Choice is < 0 or > 6) yield return "choice must be between 0 and 6";
        if (YawFix is not (0 or 90 or 180 or 270)) yield return "yaw_fix must be 0, 90, 180 or 270";
        if (Confidence is < 0 or > 1 || double.IsNaN(Confidence)) yield return "confidence must be between 0 and 1";
    }
}

public static class AiText
{
    public static bool IsHttpUrl(string? s) =>
        Uri.TryCreate(s, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);

    /// <summary>Lowercase, trimmed, de-duplicated tags.</summary>
    public static List<string> NormalizeTags(IEnumerable<string> tags) =>
        tags.Select(t => t.Trim().Trim('#').ToLowerInvariant()).Where(t => t.Length is > 0 and <= 40).Distinct().ToList();
}
