using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using AssetManager.Core.Domain;
using AssetManager.Processing.Handlers;
using UglyToad.PdfPig;

namespace AssetManager.AI.Enrichment;

/// <summary>Text and links read from one license/readme/credits/link document.</summary>
/// <param name="RelativePath">Path inside the source (or the file name for documents next to it); used as license evidence.</param>
public sealed partial record SourceDocument(string RelativePath, string Text, IReadOnlyList<string> Urls, string? Error = null)
{
    public string Name => Path.GetFileName(RelativePath);

    /// <summary>True for <c>.url</c>/<c>.webloc</c> shortcuts: their URL is a link of the source by itself.</summary>
    public bool IsShortcut => Path.GetExtension(RelativePath).ToLowerInvariant() is ".url" or ".webloc";

    /// <summary>License, licence, EULA, terms, copying, credits/attribution: ranked first in prompts.</summary>
    public bool LooksLikeLicense =>
        LicenseName().IsMatch(Path.GetFileNameWithoutExtension(RelativePath));

    [GeneratedRegex("licen[cs]e|eula|terms|copying|credit|attribution", RegexOptions.IgnoreCase)]
    private static partial Regex LicenseName();
}

/// <summary>
/// Reads source documents for the AI (design D7): txt/md/rtf directly, HTML via AngleSharp, PDF via PdfPig, and
/// <c>.url</c> (INI) / <c>.webloc</c> (plist) shortcuts. Text is capped; <see cref="Truncate"/> keeps license passages.
/// </summary>
public static partial class DocumentExtractor
{
    private const int MaxReadChars = 512 * 1024;
    private const int MaxPdfPages = 40;

    public static SourceDocument Extract(string localPath, string relativePath)
    {
        try
        {
            var ext = Path.GetExtension(localPath).ToLowerInvariant();
            var (text, urls) = ext switch
            {
                ".url" => FromUrlShortcut(ReadText(localPath)),
                ".webloc" => FromWebloc(ReadText(localPath)),
                ".html" or ".htm" => FromHtml(ReadText(localPath)),
                ".pdf" => FromPdf(localPath),
                ".rtf" => (StripRtf(ReadText(localPath)), []),
                _ => (ReadText(localPath), []),
            };
            text = Normalize(text);
            var all = urls.Concat(FindUrls(text)).Select(TrimUrl).Where(AiText.IsHttpUrl).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return new SourceDocument(relativePath, text, all);
        }
        catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException)
        {
            // Documents are untrusted input and PdfPig throws many exception types on damaged files;
            // a broken document must not break the import.
            return new SourceDocument(relativePath, "", [], $"Could not read {Path.GetFileName(relativePath)}: {e.Message}");
        }
    }

    /// <summary>
    /// Source documents lying next to a file or archive source (same folder, not recursive). Folder sources find
    /// theirs while walking; this covers <c>pack.zip</c> + <c>license.txt</c> side by side.
    /// </summary>
    public static IReadOnlyList<SourceDocument> FindNear(string sourcePath, FileClassifier classifier, int max = 10)
    {
        var folder = Path.GetDirectoryName(sourcePath);
        if (folder is null || !Directory.Exists(folder)) return [];
        var found = new List<SourceDocument>();
        foreach (var file in Directory.EnumerateFiles(folder).Order(StringComparer.OrdinalIgnoreCase))
        {
            if (string.Equals(file, sourcePath, StringComparison.OrdinalIgnoreCase)) continue;
            if (classifier.Classify(Path.GetFileName(file)).Kind != FileKind.SourceDocument) continue;
            found.Add(Extract(file, Path.GetFileName(file)));
            if (found.Count >= max) break;
        }
        return found;
    }

    /// <summary>
    /// Shortens text to <paramref name="budget"/> characters. The opening is kept for context; the rest of the budget
    /// goes to paragraphs that talk about licensing (license, commercial, attribution, royalty, ...), in original order.
    /// </summary>
    public static string Truncate(string text, int budget)
    {
        if (text.Length <= budget) return text;
        var paragraphs = Paragraphs().Split(text).Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
        var alloc = new int[paragraphs.Count];
        var used = 0;

        // 1. The opening, for context.
        var headBudget = budget / 4;
        for (var i = 0; i < paragraphs.Count && used < headBudget; i++)
        {
            alloc[i] = Math.Min(paragraphs[i].Length, headBudget - used);
            used += alloc[i] + 2;
        }
        // 2. License passages, most license words first.
        var ranked = paragraphs.Select((p, i) => (p, i, score: LicenseScore(p)))
            .Where(x => x.score > 0 && alloc[x.i] == 0)
            .OrderByDescending(x => x.score).ThenBy(x => x.i);
        foreach (var (p, i, _) in ranked)
        {
            var take = Math.Min(p.Length, budget - used);
            if (take < 40 && take < p.Length) break;
            alloc[i] = take;
            used += take + 2;
        }
        // 3. Whatever room is left: continue in reading order.
        for (var i = 0; i < paragraphs.Count && used < budget; i++)
        {
            var extra = Math.Min(paragraphs[i].Length - alloc[i], budget - used - (alloc[i] == 0 ? 2 : 0));
            if (extra <= 0) continue;
            if (alloc[i] == 0) used += 2;
            alloc[i] += extra;
            used += extra;
        }

        var sb = new StringBuilder();
        var last = -1;
        for (var i = 0; i < paragraphs.Count; i++)
        {
            if (alloc[i] == 0) continue;
            if (sb.Length > 0) sb.Append(i == last + 1 ? "\n\n" : "\n[...]\n");
            sb.Append(Clip(paragraphs[i], alloc[i]));
            last = i;
        }
        if (last >= 0 && last < paragraphs.Count - 1) sb.Append("\n[...]");
        return sb.ToString();
    }

    /// <summary>Renders documents for a prompt within a total character budget; license documents come first and get more room.</summary>
    public static string ToPrompt(IReadOnlyList<SourceDocument> docs, int budget = 6000)
    {
        var usable = docs.Where(d => d.Text.Length > 0 || d.Urls.Count > 0)
            .OrderByDescending(d => d.LooksLikeLicense).ThenBy(d => d.IsShortcut).ThenBy(d => d.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (usable.Count == 0) return "";
        var sb = new StringBuilder();
        var remaining = budget;
        for (var i = 0; i < usable.Count && remaining > 200; i++)
        {
            var d = usable[i];
            // License docs may use up to half of what is left; others share it evenly.
            var share = d.LooksLikeLicense ? remaining / 2 : remaining / (usable.Count - i);
            var body = d.IsShortcut ? "Link: " + string.Join(" ", d.Urls) : Truncate(d.Text, Math.Max(200, share));
            var block = $"=== {d.RelativePath} ===\n{body}\n";
            sb.Append(block);
            remaining -= block.Length;
        }
        return sb.ToString();
    }

    /// <summary>Links of shortcut files, which are recorded on the source without asking the AI ("Link shortcut").</summary>
    public static IEnumerable<LinkInfo> ShortcutLinks(IEnumerable<SourceDocument> docs) =>
        docs.Where(d => d.IsShortcut).SelectMany(d => d.Urls.Select(u => new LinkInfo(u, Path.GetFileNameWithoutExtension(d.Name), d.RelativePath)));

    private static int LicenseScore(string p)
    {
        var score = 0;
        foreach (Match _ in LicenseWords().Matches(p)) score++;
        return score;
    }

    private static string Clip(string p, int max) => p.Length <= max ? p : max <= 0 ? "" : p[..max].TrimEnd() + "...";

    private static (string, IReadOnlyList<string>) FromUrlShortcut(string ini)
    {
        var urls = IniUrl().Matches(ini).Select(m => m.Groups[1].Value.Trim()).ToList();
        return ("", urls);
    }

    private static (string, IReadOnlyList<string>) FromWebloc(string plist)
    {
        var urls = PlistString().Matches(plist).Select(m => System.Net.WebUtility.HtmlDecode(m.Groups[1].Value.Trim())).ToList();
        return ("", urls);
    }

    private static (string, IReadOnlyList<string>) FromHtml(string html)
    {
        using var doc = new HtmlParser().ParseDocument(html);
        foreach (var e in doc.QuerySelectorAll("script, style, noscript, template").ToList()) e.Remove();
        var links = doc.QuerySelectorAll("a[href]").Select(a => a.GetAttribute("href")!).Where(AiText.IsHttpUrl).ToList();
        // Put block elements on their own lines so paragraphs survive TextContent.
        foreach (var b in doc.QuerySelectorAll("p, div, br, li, h1, h2, h3, h4, h5, h6, tr, section, article"))
            b.InsertBefore(doc.CreateTextNode("\n\n"), b.FirstChild);
        var text = (doc.Body ?? doc.DocumentElement).TextContent;
        return (text, links);
    }

    private static (string, IReadOnlyList<string>) FromPdf(string path)
    {
        using var pdf = PdfDocument.Open(path);
        var sb = new StringBuilder();
        var links = new List<string>();
        foreach (var page in pdf.GetPages().Take(MaxPdfPages))
        {
            var lines = page.GetWords().GroupBy(w => Math.Round(w.BoundingBox.Bottom))
                .OrderByDescending(g => g.Key).Select(g => string.Join(" ", g.OrderBy(w => w.BoundingBox.Left).Select(w => w.Text)));
            foreach (var line in lines) sb.AppendLine(line);
            sb.AppendLine();
            links.AddRange(page.GetHyperlinks().Select(h => h.Uri).Where(u => u is not null));
            if (sb.Length > MaxReadChars) break;
        }
        return (sb.ToString(), links);
    }

    private static string StripRtf(string rtf)
    {
        var s = RtfGroups().Replace(rtf, "");
        s = RtfPar().Replace(s, "\n");
        s = RtfControl().Replace(s, "");
        return s.Replace("{", "").Replace("}", "");
    }

    private static string ReadText(string path)
    {
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var buffer = new char[MaxReadChars];
        return new string(buffer, 0, reader.ReadBlock(buffer, 0, buffer.Length));
    }

    /// <summary>Collapses runs of spaces, keeps paragraph breaks, drops control characters.</summary>
    private static string Normalize(string text)
    {
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        text = Controls().Replace(text, " ");
        text = Spaces().Replace(text, " ");
        text = SpaceAroundNewline().Replace(text, "\n");
        text = BlankLines().Replace(text, "\n\n");
        return text.Trim();
    }

    private static IEnumerable<string> FindUrls(string text) => UrlInText().Matches(text).Select(m => m.Value);

    private static string TrimUrl(string url) => url.TrimEnd('.', ',', ';', ':', ')', ']', '>', '"', '\'');

    [GeneratedRegex(@"(?im)^\s*URL\s*=\s*(\S+)\s*$")]
    private static partial Regex IniUrl();

    [GeneratedRegex(@"<string>\s*(https?://[^<]+)</string>", RegexOptions.IgnoreCase)]
    private static partial Regex PlistString();

    [GeneratedRegex(@"https?://[^\s<>""'\]\[(){}]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlInText();

    [GeneratedRegex(@"\b(licen[cs]\w*|commercial\w*|attribut\w*|credit\w*|royalt\w*|cc0|cc[- ]by\w*|creative commons|public domain|permi\w+|prohibit\w*|redistribut\w*|resell\w*|copyright|eula|terms|warrant\w*|free to use|personal use)\b", RegexOptions.IgnoreCase)]
    private static partial Regex LicenseWords();

    [GeneratedRegex(@"\n\s*\n")]
    private static partial Regex Paragraphs();

    [GeneratedRegex(@"[\x00-\x08\x0B\x0C\x0E-\x1F]")]
    private static partial Regex Controls();

    [GeneratedRegex(@"[ \t\u00A0]+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@" ?\n ?")]
    private static partial Regex SpaceAroundNewline();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankLines();

    [GeneratedRegex(@"\{\\(?:fonttbl|colortbl|stylesheet|info|\*)[^{}]*(?:\{[^{}]*\}[^{}]*)*\}")]
    private static partial Regex RtfGroups();

    [GeneratedRegex(@"\\par[d]?\b ?")]
    private static partial Regex RtfPar();

    [GeneratedRegex(@"\\[a-zA-Z]+-?\d* ?|\\'[0-9a-fA-F]{2}|\\[\\{}]")]
    private static partial Regex RtfControl();
}
