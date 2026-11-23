using AssetManager.Core.Domain;
using AssetManager.Data.Services;

namespace AssetManager.Data.Search;

/// <summary>
/// The one translation of a tag rule (all included tags present, no excluded tag present) to SQL.
/// Collections, search filters and stats all use it, so their counts always agree.
/// </summary>
public static class TagRuleExtensions
{
    public static IQueryable<Asset> WithTagRule(this IQueryable<Asset> q, IEnumerable<string>? include, IEnumerable<string>? exclude)
    {
        foreach (var tag in TagService.NormalizeAll(include ?? []))
            q = q.Where(a => a.Tags.Any(t => t.Tag.Name == tag));

        var excluded = TagService.NormalizeAll(exclude ?? []);
        if (excluded.Count > 0)
            q = q.Where(a => !a.Tags.Any(t => excluded.Contains(t.Tag.Name)));
        return q;
    }

    public static IQueryable<Asset> InCollection(this IQueryable<Asset> q, Collection c) => q.WithTagRule(c.IncludeTags, c.ExcludeTags);
}
