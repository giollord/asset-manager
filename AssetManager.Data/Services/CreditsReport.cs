using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AssetManager.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace AssetManager.Data.Services;

public enum CreditsFormat { Markdown, Text, Csv }

/// <summary>One source in a credits report, with the assets that use its license and those with their own.</summary>
public sealed record CreditsSource(Source Source, string Author, IReadOnlyList<Asset> Assets, IReadOnlyList<Asset> Overrides)
{
    public bool LicenseUnknown => CreditsReport.IsUnknown(Source.License);
}

/// <summary>A credits report (spec "Credits report"): sources with an unknown license first, then by name.</summary>
public sealed record CreditsModel(string Title, DateTime CreatedAt, IReadOnlyList<CreditsSource> Sources)
{
    public int AssetCount => Sources.Sum(s => s.Assets.Count + s.Overrides.Count);

    /// <summary>Sources whose license is unknown, and assets with an unknown license of their own.</summary>
    public IReadOnlyList<(CreditsSource Source, IReadOnlyList<Asset> Assets)> Unknown =>
        Sources.Select(s => (s, (IReadOnlyList<Asset>)[.. s.LicenseUnknown ? s.Assets : [], .. s.Overrides.Where(a => CreditsReport.IsUnknown(a.LicenseOverride))]))
            .Where(x => x.Item2.Count > 0).ToList();
}

/// <summary>
/// Builds credits from the catalog only (design D9), so it works with every drive detached. Groups by source; assets
/// with a license override are listed with that license. Renders Markdown, plain text or CSV (one row per asset).
/// </summary>
public static partial class CreditsReport
{
    public static bool IsUnknown(LicenseInfo license) =>
        string.IsNullOrWhiteSpace(license.Kind) || license.Kind.Trim().Equals("unknown", StringComparison.OrdinalIgnoreCase);

    /// <param name="assets">Assets with their <see cref="Asset.Source"/> loaded.</param>
    public static CreditsModel Build(string title, IEnumerable<Asset> assets, DateTime? now = null)
    {
        var sources = assets.GroupBy(a => a.SourceId)
            .Select(g =>
            {
                var source = g.First().Source;
                var ordered = g.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
                return new CreditsSource(source, Author(source), ordered.Where(a => !a.HasLicenseOverride).ToList(), ordered.Where(a => a.HasLicenseOverride).ToList());
            })
            .OrderBy(s => s.LicenseUnknown ? 0 : 1).ThenBy(s => s.Source.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new CreditsModel(title, now ?? DateTime.Now, sources);
    }

    /// <summary>The store publisher, else the name in the attribution text ("by X", "© X"), else "unknown".</summary>
    public static string Author(Source source)
    {
        if (!string.IsNullOrWhiteSpace(source.Store.Publisher)) return source.Store.Publisher.Trim();
        if (source.License.AttributionText is { } text && ByName().Match(text) is { Success: true } m) return m.Groups[1].Value.Trim();
        return "unknown";
    }

    public static string Render(CreditsModel model, CreditsFormat format) => format switch
    {
        CreditsFormat.Markdown => Markdown(model),
        CreditsFormat.Text => Text(model),
        _ => Csv(model),
    };

    public static string Extension(CreditsFormat format) => format switch
    {
        CreditsFormat.Markdown => ".md",
        CreditsFormat.Text => ".txt",
        _ => ".csv",
    };

    public static string Describe(LicenseInfo l) =>
        $"{(IsUnknown(l) ? "unknown" : l.Kind!.Trim())} · commercial use: {Tri(l.Commercial, "allowed", "not allowed")} · attribution: {Tri(l.Attribution, "required", "not required")}";

    private static string Tri(TriState t, string yes, string no) => t switch { TriState.Yes => yes, TriState.No => no, _ => "unknown" };

    private static string Names(IEnumerable<Asset> assets) => string.Join(", ", assets.Select(a => a.Name));

    private static string Markdown(CreditsModel m)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Credits: {Md(m.Title)}").AppendLine();
        sb.AppendLine($"{m.AssetCount} assets from {m.Sources.Count} sources. Created {m.CreatedAt:yyyy-MM-dd} from the Asset Manager catalog.").AppendLine();
        if (m.Unknown.Count > 0)
        {
            sb.AppendLine("## ⚠ License unknown").AppendLine();
            sb.AppendLine("Check the license of these before you use them:").AppendLine();
            foreach (var (s, assets) in m.Unknown) sb.AppendLine($"- **{Md(s.Source.DisplayName)}** ({assets.Count}): {Md(Names(assets))}");
            sb.AppendLine();
        }
        foreach (var s in m.Sources)
        {
            var l = s.Source.License;
            sb.AppendLine($"## {Md(s.Source.DisplayName)}").AppendLine();
            sb.AppendLine($"- License: {Md(Describe(l))}");
            if (!string.IsNullOrWhiteSpace(l.AttributionText)) sb.AppendLine($"- Attribution: {Md(l.AttributionText.Trim())}");
            sb.AppendLine($"- Author: {Md(s.Author)}");
            foreach (var link in s.Source.Links) sb.AppendLine($"- Link: {(link.Label is { Length: > 0 } label ? $"[{Md(label)}]({link.Url})" : $"<{link.Url}>")}");
            if (s.Assets.Count > 0) sb.AppendLine($"- Assets ({s.Assets.Count}): {Md(Names(s.Assets))}");
            if (s.Overrides.Count > 0)
            {
                sb.AppendLine($"- With their own license:");
                foreach (var a in s.Overrides)
                {
                    sb.AppendLine($"  - {Md(a.Name)}: {Md(Describe(a.LicenseOverride))}");
                    if (!string.IsNullOrWhiteSpace(a.LicenseOverride.AttributionText)) sb.AppendLine($"    - Attribution: {Md(a.LicenseOverride.AttributionText.Trim())}");
                }
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string Text(CreditsModel m)
    {
        var sb = new StringBuilder();
        void Heading(string text, char line) => sb.AppendLine(text).AppendLine(new string(line, Math.Max(3, text.Length)));
        Heading($"Credits: {m.Title}", '=');
        sb.AppendLine($"{m.AssetCount} assets from {m.Sources.Count} sources. Created {m.CreatedAt:yyyy-MM-dd} from the Asset Manager catalog.").AppendLine();
        if (m.Unknown.Count > 0)
        {
            Heading("WARNING: license unknown", '-');
            sb.AppendLine("Check the license of these before you use them:");
            foreach (var (s, assets) in m.Unknown) sb.AppendLine($"  * {s.Source.DisplayName} ({assets.Count}): {Names(assets)}");
            sb.AppendLine();
        }
        foreach (var s in m.Sources)
        {
            var l = s.Source.License;
            Heading(s.Source.DisplayName, '-');
            sb.AppendLine($"License:     {Describe(l)}");
            if (!string.IsNullOrWhiteSpace(l.AttributionText)) sb.AppendLine($"Attribution: {l.AttributionText.Trim()}");
            sb.AppendLine($"Author:      {s.Author}");
            foreach (var link in s.Source.Links) sb.AppendLine($"Link:        {link.Url}{(link.Label is { Length: > 0 } label ? $" ({label})" : "")}");
            if (s.Assets.Count > 0) sb.AppendLine($"Assets ({s.Assets.Count}): {Names(s.Assets)}");
            foreach (var a in s.Overrides)
            {
                sb.AppendLine($"Own license: {a.Name}: {Describe(a.LicenseOverride)}");
                if (!string.IsNullOrWhiteSpace(a.LicenseOverride.AttributionText)) sb.AppendLine($"             attribution: {a.LicenseOverride.AttributionText.Trim()}");
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string Csv(CreditsModel m)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Asset,Type,Source,License from,License,Commercial use,Attribution,Attribution text,Author,Links");
        foreach (var s in m.Sources)
            foreach (var a in s.Assets.Concat(s.Overrides))
            {
                var l = a.HasLicenseOverride ? a.LicenseOverride : s.Source.License;
                var links = a.LinksOverride ?? s.Source.Links;
                string[] cells =
                [
                    a.Name, a.Type.ToString(), s.Source.DisplayName, a.HasLicenseOverride ? "asset" : "source", IsUnknown(l) ? "unknown" : l.Kind!.Trim(),
                    Tri(l.Commercial, "allowed", "not allowed"), Tri(l.Attribution, "required", "not required"), l.AttributionText?.Trim() ?? "", s.Author,
                    string.Join(" ", links.Select(x => x.Url)),
                ];
                sb.AppendLine(string.Join(",", cells.Select(Cell)));
            }
        return sb.ToString();
    }

    private static string Cell(string v) => v.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;

    /// <summary>Escapes Markdown control characters in names and texts.</summary>
    private static string Md(string v) => MdSpecial().Replace(v, @"\$1");

    [GeneratedRegex(@"([\\`*_\[\]<>#|])")]
    private static partial Regex MdSpecial();

    [GeneratedRegex(@"(?:\bby|©|\(c\)|copyright)\s+([^,.;:\n()]+)", RegexOptions.IgnoreCase)]
    private static partial Regex ByName();
}

/// <summary>Creates credits reports for a set of assets or a collection, from the catalog only.</summary>
public sealed class CreditsService(CatalogStore catalog, CollectionService? collections = null)
{
    public async Task<CreditsModel> BuildAsync(string title, IReadOnlyCollection<long> assetIds, CancellationToken ct = default)
    {
        await using var db = catalog.CreateDbContext();
        var assets = new List<Asset>();
        foreach (var chunk in assetIds.Distinct().Chunk(500))
            assets.AddRange(await db.Assets.AsNoTracking().Include(a => a.Source).Where(a => chunk.Contains(a.Id)).ToListAsync(ct));
        return CreditsReport.Build(title, assets);
    }

    public async Task<IReadOnlyCollection<long>> CollectionIdsAsync(long collectionId, CancellationToken ct = default)
    {
        if (collections is null) throw new InvalidOperationException("Collections are not available.");
        await using var db = catalog.CreateDbContext();
        var c = await db.Collections.AsNoTracking().FirstAsync(x => x.Id == collectionId, ct);
        return await collections.MemberIdsAsync(c, ct);
    }

    /// <summary>Writes the report into <paramref name="folder"/> as "Credits - title.ext" (never overwriting); returns the path.</summary>
    public async Task<string> SaveAsync(string title, IReadOnlyCollection<long> assetIds, CreditsFormat format, string folder, CancellationToken ct = default)
    {
        var model = await BuildAsync(title, assetIds, ct);
        var name = string.Concat($"Credits - {title}".Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch)).Trim();
        var ext = CreditsReport.Extension(format);
        var path = Path.Combine(folder, name + ext);
        for (var i = 2; File.Exists(path); i++) path = Path.Combine(folder, $"{name} ({i}){ext}");
        Directory.CreateDirectory(folder);
        // UTF-8 with a BOM so spreadsheet apps read the CSV's accents correctly.
        await File.WriteAllTextAsync(path, CreditsReport.Render(model, format), new UTF8Encoding(format == CreditsFormat.Csv), ct);
        return path;
    }
}
