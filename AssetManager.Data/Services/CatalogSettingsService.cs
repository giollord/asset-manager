using System.Text.Json;
using AssetManager.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace AssetManager.Data.Services;

/// <summary>Settings that belong to the catalog, stored as JSON values in the <c>Settings</c> table.</summary>
public sealed class CatalogSettingsService(CatalogStore catalog)
{
    public async Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
    {
        await using var db = catalog.CreateDbContext();
        var row = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key, ct);
        if (row is null) return default;
        try { return JsonSerializer.Deserialize<T>(row.Value, CatalogDbContext.JsonOptions); }
        catch (JsonException) { return default; }
    }

    public async Task SetAsync<T>(string key, T value, CancellationToken ct = default)
    {
        await using var db = catalog.CreateDbContext();
        var json = JsonSerializer.Serialize(value, CatalogDbContext.JsonOptions);
        var row = await db.Settings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (row is null) db.Settings.Add(new Setting { Key = key, Value = json });
        else row.Value = json;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Removes a setting, so its default applies again.</summary>
    public async Task RemoveAsync(string key, CancellationToken ct = default)
    {
        await using var db = catalog.CreateDbContext();
        await db.Settings.Where(s => s.Key == key).ExecuteDeleteAsync(ct);
    }

    // ---- suggested tag vocabulary (design D15) ----

    /// <summary>The catalog's suggested tags; the built-in list when none were saved.</summary>
    public async Task<IReadOnlyList<string>> GetSuggestedTagsAsync(CancellationToken ct = default) =>
        await GetAsync<List<string>>(SuggestedTags.SettingKey, ct) ?? SuggestedTags.Default;

    /// <summary>Saves the suggested tags, normalized like other tags. Existing assets are not changed.</summary>
    public Task SetSuggestedTagsAsync(IEnumerable<string> tags, CancellationToken ct = default) =>
        SetAsync(SuggestedTags.SettingKey, TagService.NormalizeAll(tags).ToList(), ct);

    /// <summary>Back to the built-in list.</summary>
    public Task ResetSuggestedTagsAsync(CancellationToken ct = default) => RemoveAsync(SuggestedTags.SettingKey, ct);
}
