namespace AssetManager.Tests;

/// <summary>
/// Integration test over the real sample library. Runs only when the ASSETMANAGER_SAMPLES env var
/// points to an existing folder (for example E:\Assets).
/// </summary>
public sealed class SamplesFactAttribute : FactAttribute
{
    public static string? SamplesRoot
    {
        get
        {
            var p = Environment.GetEnvironmentVariable("ASSETMANAGER_SAMPLES");
            return !string.IsNullOrWhiteSpace(p) && Directory.Exists(p) ? p : null;
        }
    }

    public SamplesFactAttribute()
    {
        if (SamplesRoot is null) Skip = "Set ASSETMANAGER_SAMPLES to the sample asset folder to run.";
    }
}
