using AssetManager.Core.Domain;

namespace AssetManager.Data.Search;

/// <summary>Filters on the effective license (asset override if present, else the source's license).</summary>
public static class LicenseFilters
{
    public static IQueryable<Asset> WhereCommercial(this IQueryable<Asset> q, TriState value) =>
        q.Where(a => (a.HasLicenseOverride ? a.LicenseOverride.Commercial : a.Source.License.Commercial) == value);

    public static IQueryable<Asset> WhereAttribution(this IQueryable<Asset> q, TriState value) =>
        q.Where(a => (a.HasLicenseOverride ? a.LicenseOverride.Attribution : a.Source.License.Attribution) == value);

    /// <summary>Assets with a license audit problem on their effective license (spec "License audit").</summary>
    public static IQueryable<Asset> WhereLicenseProblem(this IQueryable<Asset> q, Core.Search.LicenseProblem problem) => problem switch
    {
        Core.Search.LicenseProblem.UnknownLicense => q.Where(a => a.HasLicenseOverride
            ? a.LicenseOverride.Kind == null || a.LicenseOverride.Kind.Trim() == "" || a.LicenseOverride.Kind.Trim().ToLower() == "unknown" ||
              a.LicenseOverride.Commercial == TriState.Unknown
            : a.Source.License.Kind == null || a.Source.License.Kind.Trim() == "" || a.Source.License.Kind.Trim().ToLower() == "unknown" ||
              a.Source.License.Commercial == TriState.Unknown),
        Core.Search.LicenseProblem.AttributionMissing => q.Where(a => a.HasLicenseOverride
            ? a.LicenseOverride.Attribution == TriState.Yes && (a.LicenseOverride.AttributionText == null || a.LicenseOverride.AttributionText.Trim() == "")
            : a.Source.License.Attribution == TriState.Yes && (a.Source.License.AttributionText == null || a.Source.License.AttributionText.Trim() == "")),
        Core.Search.LicenseProblem.NonCommercial => q.WhereCommercial(TriState.No),
        _ => q.Where(a => a.HasLicenseOverride &&
                          ((a.LicenseOverride.Kind ?? "") != (a.Source.License.Kind ?? "") ||
                           a.LicenseOverride.Commercial != a.Source.License.Commercial ||
                           a.LicenseOverride.Attribution != a.Source.License.Attribution)),
    };
}
