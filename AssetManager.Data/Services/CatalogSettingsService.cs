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
}
