using AssetManager.Core.Domain;
using AssetManager.Core.Platform;
using AssetManager.Data;
using Microsoft.EntityFrameworkCore;

namespace AssetManager.Processing.Locations;

/// <summary>Whether an asset's original can be reached now; <see cref="Missing"/> names the missing drive or path.</summary>
public sealed record AssetAvailability(long AssetId, bool IsAvailable, bool UsesStoredCopy, string? Missing);

/// <summary>
/// Computes original availability per asset. It is checked per volume (is the drive mounted?), not per file,
/// so it is cheap for large result lists; a stored copy always counts as available. Only absolute paths that
/// are not on a known volume are checked for existence.
/// </summary>
public sealed class AvailabilityService(IVolumeService volumes, CatalogStore catalog)
{
    public async Task<IReadOnlyDictionary<long, AssetAvailability>> GetAsync(IReadOnlyCollection<long> assetIds, CancellationToken ct = default)
    {
        var result = new Dictionary<long, AssetAvailability>();
        if (assetIds.Count == 0) return result;
        await using var db = catalog.CreateDbContext();
        var rows = new List<(long Id, bool IsStored, Location? Location)>();
        foreach (var chunk in assetIds.Chunk(500))
        {
            var part = await db.AllAssets.AsNoTracking()
                .Where(a => chunk.Contains(a.Id))
                .Select(a => new
                {
                    a.Id,
                    a.IsStored,
                    Location = a.Files.Where(f => f.Role == FileRole.Primary).Select(f => (Location?)f.Location).FirstOrDefault(),
                })
                .ToListAsync(ct);
            rows.AddRange(part.Select(p => (p.Id, p.IsStored, p.Location)));
        }

        var volumeIds = rows.Select(r => r.Location?.VolumeId).OfType<string>().Distinct().ToList();
        var known = await db.Volumes.AsNoTracking().Where(v => volumeIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, ct);
        var mounted = volumeIds.ToDictionary(id => id, id => volumes.GetRoot(id) is not null);

        foreach (var (id, isStored, loc) in rows)
        {
            result[id] = isStored ? new(id, true, true, null)
                : loc is null ? new(id, false, false, "no files")
                : loc.VolumeId is null
                    ? File.Exists(loc.Path) ? new(id, true, false, null) : new(id, false, false, $"path \"{loc.Path}\"")
                : mounted[loc.VolumeId] ? new(id, true, false, null)
                : new(id, false, false, DescribeVolume(loc.VolumeId, known.GetValueOrDefault(loc.VolumeId)));
        }
        return result;
    }

    public async Task<AssetAvailability> GetAsync(long assetId, CancellationToken ct = default) =>
        (await GetAsync([assetId], ct)).GetValueOrDefault(assetId) ?? new(assetId, false, false, "asset not found");

    internal static string DescribeVolume(string volumeId, Volume? known)
    {
        var name = string.IsNullOrWhiteSpace(known?.Label) ? $"drive {volumeId}" : $"drive \"{known.Label}\"";
        return known?.LastRoot is { } last ? $"{name} (last seen as {last})" : name;
    }
}
