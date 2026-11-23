using AssetManager.Core.Jobs;
using AssetManager.Core.Settings;

namespace AssetManager.Data.Services;

/// <summary>
/// Switches the open catalog (spec "Change catalog location"): opens the file at the new path, creating and migrating
/// it when needed, and remembers it in the app settings. Refused while jobs run, since they write to the open catalog.
/// </summary>
public sealed class CatalogLocationService(CatalogStore catalog, IAppSettingsStore settings, IProgressHub? hub = null)
{
    public const string Extension = ".amdb";

    /// <summary>The path of a new catalog file in <paramref name="folder"/> that does not overwrite an existing one.</summary>
    public static string NewCatalogPath(string folder)
    {
        var path = Path.Combine(folder, "catalog" + Extension);
        for (var i = 2; File.Exists(path); i++) path = Path.Combine(folder, $"catalog-{i}{Extension}");
        return path;
    }

    /// <returns>True when a new, empty catalog was created.</returns>
    public async Task<bool> SwitchAsync(string path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path.Trim()))
            throw new ArgumentException("Enter the full path of a catalog file, such as D:\\Assets\\catalog.amdb.");
        path = Path.GetFullPath(path.Trim());
        if (Directory.Exists(path)) path = NewCatalogPath(path);
        if (string.Equals(path, catalog.Path, StringComparison.OrdinalIgnoreCase)) return false;
        if (hub?.Active.Count > 0)
            throw new InvalidOperationException("Jobs are running on the current catalog. Wait for them to finish or cancel them on the Jobs page first.");

        var created = !File.Exists(path) || new FileInfo(path).Length == 0;
        await catalog.OpenAsync(path, ct);
        settings.Update(s => s.CatalogPath = path);
        catalog.NotifyContentChanged();
        return created;
    }
}
