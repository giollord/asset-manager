using AssetManager.Core.Domain;

namespace AssetManager.AI.Enrichment;

/// <summary>Applies extracted license and link facts to a source, citing the documents they came from.</summary>
public static class SourceFacts
{
    /// <summary>
    /// Records links from <c>.url</c>/<c>.webloc</c> shortcuts and, when given, the AI's license and links. Evidence names
    /// the model returns are matched to the provided documents; anything that does not match a document is dropped so a
    /// value never cites a file that does not exist. Fields the user edited are left alone.
    /// </summary>
    public static void Apply(Source source, IReadOnlyList<SourceDocument> docs, PackResult? ai)
    {
        if (!source.UserEdited.HasFlag(EditedFields.Links))
        {
            var links = new List<LinkInfo>(source.Links);
            foreach (var l in DocumentExtractor.ShortcutLinks(docs)) Add(links, l);
            if (ai is not null)
                foreach (var l in ai.Links.Where(l => AiText.IsHttpUrl(l.Url)))
                    Add(links, new LinkInfo(l.Url.Trim(), string.IsNullOrWhiteSpace(l.Label) ? null : l.Label.Trim(), Resolve(docs, l.Evidence)));
            source.Links = links;
        }

        if (ai is not null && !source.UserEdited.HasFlag(EditedFields.License))
        {
            var license = ai.License.ToLicenseInfo();
            license.Evidence = Resolve(docs, license.Evidence);
            source.License = license;
        }
    }

    /// <summary>Maps a file name or path the model cited to the relative path of a provided document.</summary>
    public static string? Resolve(IReadOnlyList<SourceDocument> docs, string? cited)
    {
        if (string.IsNullOrWhiteSpace(cited)) return null;
        var c = cited.Trim().Replace('\\', '/');
        return docs.FirstOrDefault(d => d.RelativePath.Replace('\\', '/').Equals(c, StringComparison.OrdinalIgnoreCase))?.RelativePath
               ?? docs.FirstOrDefault(d => d.Name.Equals(Path.GetFileName(c), StringComparison.OrdinalIgnoreCase))?.RelativePath
               // Models sometimes cite "Document license.pdf" or "the license.pdf file".
               ?? docs.OrderByDescending(d => d.RelativePath.Length)
                   .FirstOrDefault(d => c.Contains(d.RelativePath.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))?.RelativePath
               ?? docs.OrderByDescending(d => d.Name.Length)
                   .FirstOrDefault(d => c.Contains(d.Name, StringComparison.OrdinalIgnoreCase))?.RelativePath
               // ...or quote the document instead of naming it: accept the quote when exactly one document contains it.
               ?? (c.Length >= 8 && docs.Count(d => d.Text.Contains(c, StringComparison.OrdinalIgnoreCase)) == 1
                   ? docs.First(d => d.Text.Contains(c, StringComparison.OrdinalIgnoreCase)).RelativePath
                   : null);
    }

    private static void Add(List<LinkInfo> links, LinkInfo link)
    {
        var key = link.Url.TrimEnd('/');
        if (links.Any(l => string.Equals(l.Url.TrimEnd('/'), key, StringComparison.OrdinalIgnoreCase))) return;
        links.Add(link);
    }
}
