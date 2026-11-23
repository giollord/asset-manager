using AssetManager.Core.Domain;

namespace AssetManager.Data.Search;

/// <summary>Filters on the effective license (asset override if present, else the source's license).</summary>
public static class LicenseFilters
{
    public static IQueryable<Asset> WhereCommercial(this IQueryable<Asset> q, TriState value) =>
        q.Where(a => (a.HasLicenseOverride ? a.LicenseOverride.Commercial : a.Source.License.Commercial) == value);

    public static IQueryable<Asset> WhereAttribution(this IQueryable<Asset> q, TriState value) =>
        q.Where(a => (a.HasLicenseOverride ? a.LicenseOverride.Attribution : a.Source.License.Attribution) == value);
}
