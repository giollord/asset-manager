namespace AssetManager.Core.Search;

/// <summary>The license audit problems (spec "License audit"), judged on each asset's effective license.</summary>
public enum LicenseProblem
{
    /// <summary>No license kind, or commercial use unknown.</summary>
    UnknownLicense,
    /// <summary>Attribution required, but no attribution text.</summary>
    AttributionMissing,
    /// <summary>Commercial use not allowed.</summary>
    NonCommercial,
    /// <summary>The asset's own license differs from its source's (kind, commercial use or attribution).</summary>
    OverrideDiffers,
}

public static class LicenseProblems
{
    public static string Label(this LicenseProblem p) => p switch
    {
        LicenseProblem.UnknownLicense => "Unknown license",
        LicenseProblem.AttributionMissing => "Attribution missing",
        LicenseProblem.NonCommercial => "Non-commercial",
        _ => "Overrides differ",
    };

    public static string Explain(this LicenseProblem p) => p switch
    {
        LicenseProblem.UnknownLicense => "No license kind, or commercial use unknown",
        LicenseProblem.AttributionMissing => "Attribution is required but there is no attribution text",
        LicenseProblem.NonCommercial => "Commercial use is not allowed",
        _ => "Assets whose own license differs from the source's",
    };
}
