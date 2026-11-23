using AssetManager.Core.Domain;
using AssetManager.Core.Platform;
using AssetManager.Data;
using AssetManager.Processing.Cache;
using AssetManager.Processing.Containers;
using Microsoft.EntityFrameworkCore;

namespace AssetManager.Processing.Locations;

/// <summary>Creates volume-relative locations for paths and remembers the volumes they refer to.</summary>
public sealed class VolumeRegistry(IVolumeService volumes, CatalogStore catalog)
{
    /// <summary>The catalog location of an on-disk file or folder.</summary>
    public Location FromPath(string path)
    {
        var full = Path.GetFullPath(path);
        var v = volumes.GetVolumeForPath(full);
        return v is null ? new Location(null, full) : new Location(v.Id, Path.GetRelativePath(v.RootPath, full));
    }

    /// <summary>Records label and current root of mounted volumes so "unavailable" messages can name them later.</summary>
    public async Task RememberAsync(IEnumerable<string?> volumeIds, CancellationToken ct = default)
    {
        var ids = volumeIds.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (ids.Count == 0) return;
        await using var db = catalog.CreateDbContext();
        var known = await db.Volumes.Where(v => ids.Contains(v.Id)).ToDictionaryAsync(v => v.Id, StringComparer.OrdinalIgnoreCase, ct);
        foreach (var id in ids)
        {
            var mounted = volumes.GetVolumes().FirstOrDefault(v => string.Equals(v.Id, id, StringComparison.OrdinalIgnoreCase));
            if (mounted is null) continue;
            if (!known.TryGetValue(id, out var row)) db.Volumes.Add(row = new Volume { Id = mounted.Id });
            row.Label = mounted.Label;
            row.LastRoot = mounted.RootPath;
            row.LastSeen = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>
/// Resolves catalog locations to readable local files: stored copy → live path on the (possibly re-lettered)
/// volume → extraction from the containing archive/package chain through the cache.
/// </summary>
public sealed class LocationResolver(IVolumeService volumes, CatalogStore catalog, ExtractionCache cache, IEnumerable<IContainerReader> readers)
    : ILocationResolver
{
    private readonly IReadOnlyList<IContainerReader> _readers = readers.ToList();

    public string? GetOuterPath(Location location)
    {
        if (location.VolumeId is null) return location.Path;
        var root = volumes.GetRoot(location.VolumeId);
        return root is null ? null : Path.Combine(root, location.Path);
    }

    public async Task<string> DescribeMissingAsync(Location location, CancellationToken ct = default)
    {
        if (location.VolumeId is null) return $"path \"{location.Path}\"";
        if (volumes.GetRoot(location.VolumeId) is { } root) return $"file \"{Path.Combine(root, location.Path)}\"";
        Volume? known = null;
        if (catalog.Path is not null)
        {
            await using var db = catalog.CreateDbContext();
            known = await db.Volumes.AsNoTracking().FirstOrDefaultAsync(v => v.Id == location.VolumeId, ct);
        }
        return AvailabilityService.DescribeVolume(location.VolumeId, known);
    }

    public async Task<ResolvedFile> ResolveAsync(AssetFile file, CancellationToken ct = default)
    {
        var stored = file.Stored;
        if (stored is null && file.Id > 0 && catalog.Path is not null)
        {
            await using var db = catalog.CreateDbContext();
            stored = await db.StoredFiles.AsNoTracking().FirstOrDefaultAsync(s => s.AssetFileId == file.Id, ct);
        }
        if (stored is not null) return await FromStoredAsync(stored, file.FileName, ct);
        return await ResolveAsync(file.Location, ct);
    }

    public async Task<ResolvedFile> ResolveAsync(Location location, CancellationToken ct = default)
    {
        var outer = GetOuterPath(location);
        if (outer is null || !File.Exists(outer)) throw new OriginalUnavailableException(await DescribeMissingAsync(location.OuterFile, ct));
        if (!location.IsInContainer) return new ResolvedFile(outer, ResolvedFrom.Live);

        var leases = new List<IDisposable>();
        try
        {
            var current = outer;
            var currentName = Path.GetFileName(outer);
            foreach (var key in location.Inner)
            {
                var reader = _readers.FirstOrDefault(r => r.CanRead(currentName))
                    ?? throw new InvalidOperationException($"No reader for container '{currentName}'.");
                ResolvedFile entry;
                try
                {
                    entry = await reader.ExtractEntryAsync(current, key, ct);
                }
                catch (Exception e) when (e is FileNotFoundException or ContainerReadException)
                {
                    throw new OriginalUnavailableException($"entry \"{key}\" in \"{currentName}\" ({e.Message})");
                }
                leases.Add(entry);
                current = entry.Path;
                currentName = Path.GetFileName(key);
            }
            return new ResolvedFile(current, ResolvedFrom.Container, new CompositeLease(leases));
        }
        catch
        {
            foreach (var l in leases) l.Dispose();
            throw;
        }
    }

    private async Task<ResolvedFile> FromStoredAsync(StoredFile stored, string fileName, CancellationToken ct)
    {
        var name = string.IsNullOrWhiteSpace(fileName) ? "file" : string.Concat(fileName.Split(Path.GetInvalidFileNameChars()));
        var (lease, path) = await cache.GetOrAddFileAsync($"s-{stored.Id}-{stored.Size:x}", name, async (temp, c) =>
        {
            await using var db = catalog.CreateDbContext();
            await using var fs = File.Create(temp);
            for (var seq = 0; seq < stored.ChunkCount; seq++)
            {
                var bytes = await db.StoredFileChunks.AsNoTracking()
                    .Where(x => x.StoredFileId == stored.Id && x.Seq == seq).Select(x => x.Bytes).FirstOrDefaultAsync(c)
                    ?? throw new InvalidDataException($"Stored copy {stored.Id} is missing chunk {seq}.");
                await fs.WriteAsync(bytes, c);
            }
        }, ct);
        return new ResolvedFile(path, ResolvedFrom.StoredCopy, lease);
    }

    private sealed class CompositeLease(List<IDisposable> leases) : IDisposable
    {
        public void Dispose()
        {
            for (var i = leases.Count - 1; i >= 0; i--) leases[i].Dispose();
        }
    }
}
