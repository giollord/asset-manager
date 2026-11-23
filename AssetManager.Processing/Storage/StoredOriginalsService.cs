using AssetManager.Core.Domain;
using AssetManager.Data;
using AssetManager.Processing.Containers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AssetManager.Processing.Storage;

public sealed record SkippedAsset(long AssetId, string Name, string Reason);

public sealed record StoreResult(int Stored, int AlreadyStored, IReadOnlyList<SkippedAsset> Skipped);

/// <summary>
/// Copies assets' original files (primary and companions) into the catalog as 8 MB chunks, and removes those
/// copies again. Assets whose originals cannot be reached are skipped and reported; the rest are processed.
/// </summary>
public sealed class StoredOriginalsService(CatalogStore catalog, ILocationResolver resolver, ILogger<StoredOriginalsService>? logger = null)
{
    public const int DefaultChunkSize = 8 * 1024 * 1024;

    private readonly ILogger _log = logger ?? (ILogger)NullLogger.Instance;

    /// <summary>Chunk size in bytes (tests use a small value to exercise multi-chunk files).</summary>
    public int ChunkSize { get; init; } = DefaultChunkSize;

    public async Task<StoreResult> StoreAsync(IReadOnlyCollection<long> assetIds, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        int stored = 0, already = 0, done = 0;
        var skipped = new List<SkippedAsset>();
        foreach (var id in assetIds)
        {
            ct.ThrowIfCancellationRequested();
            await using var db = catalog.CreateDbContext();
            var asset = await db.AllAssets.Include(a => a.Files).ThenInclude(f => f.Stored).FirstOrDefaultAsync(a => a.Id == id, ct);
            if (asset is null) continue;
            if (asset.IsStored && asset.Files.All(f => f.Stored is not null))
            {
                already++;
            }
            else
            {
                var reason = await StoreOneAsync(db, asset, ct);
                if (reason is null) stored++;
                else skipped.Add(new SkippedAsset(asset.Id, asset.Name, reason));
            }
            progress?.Report(++done);
        }
        if (stored > 0) catalog.NotifyContentChanged();
        _log.LogInformation("Stored originals: {Stored} stored, {Already} already stored, {Skipped} skipped", stored, already, skipped.Count);
        return new StoreResult(stored, already, skipped);
    }

    /// <summary>Stores all files of one asset in one transaction; returns the skip reason or null on success.</summary>
    private async Task<string?> StoreOneAsync(CatalogDbContext db, Asset asset, CancellationToken ct)
    {
        var pending = asset.Files.Where(f => f.Stored is null).ToList();
        var resolved = new List<(AssetFile File, ResolvedFile Local)>();
        try
        {
            foreach (var f in pending)
            {
                try
                {
                    resolved.Add((f, await resolver.ResolveAsync(f.Location, ct)));
                }
                catch (OriginalUnavailableException e)
                {
                    return $"Original unavailable: {e.Missing}";
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or ContainerReadException)
                {
                    return e.Message;
                }
            }

            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var buffer = new byte[ChunkSize];
            foreach (var (file, local) in resolved)
            {
                await using var fs = new FileStream(local.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
                var sf = new StoredFile { AssetFileId = file.Id, Size = fs.Length, StoredAt = DateTime.UtcNow };
                db.StoredFiles.Add(sf);
                await db.SaveChangesAsync(ct);
                var seq = 0;
                int read;
                while ((read = await fs.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, ct)) > 0)
                {
                    var chunk = new StoredFileChunk { StoredFileId = sf.Id, Seq = seq++, Bytes = buffer[..read] };
                    db.StoredFileChunks.Add(chunk);
                    await db.SaveChangesAsync(ct);
                    db.Entry(chunk).State = EntityState.Detached; // keep memory bounded for multi-GB files
                }
                sf.ChunkCount = seq;
            }
            asset.IsStored = true;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return null;
        }
        finally
        {
            foreach (var (_, local) in resolved) local.Dispose();
        }
    }

    /// <summary>Deletes the stored copies of these assets and returns the freed pages to the OS.</summary>
    public async Task<int> RemoveAsync(IReadOnlyCollection<long> assetIds, CancellationToken ct = default)
    {
        if (assetIds.Count == 0) return 0;
        await using var db = catalog.CreateDbContext();
        var changed = 0;
        foreach (var chunk in assetIds.Chunk(500))
        {
            await db.StoredFiles.Where(s => chunk.Contains(s.AssetFile.AssetId)).ExecuteDeleteAsync(ct);
            changed += await db.AllAssets.Where(a => chunk.Contains(a.Id) && a.IsStored)
                .ExecuteUpdateAsync(u => u.SetProperty(a => a.IsStored, false), ct);
        }
        await db.Database.ExecuteSqlRawAsync("PRAGMA incremental_vacuum;", ct);
        await db.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(TRUNCATE);", ct);
        catalog.NotifyContentChanged();
        return changed;
    }

    /// <summary>"Compact catalog": a full VACUUM that rewrites the file without free pages.</summary>
    public async Task CompactAsync(CancellationToken ct = default)
    {
        await catalog.VacuumAsync(ct);
        await using var db = catalog.CreateDbContext();
        await db.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(TRUNCATE);", ct);
    }
}
