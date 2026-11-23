using AssetManager.Core.Domain;
using AssetManager.Data;
using AssetManager.Processing.Files;
using AssetManager.Processing.Locations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AssetManager.Processing.Import;

/// <summary>
/// Finds staged assets that are already cataloged (design D8): primary files are matched by size and fast hash
/// (head/tail XxHash3); a match is confirmed by the same location, by the fast hash already covering the whole file
/// (128 KB or less), or by a full XxHash3 of both files. Duplicates are excluded unless the session includes them.
/// </summary>
public sealed class DuplicateDetector(CatalogStore catalog, ILocationResolver resolver, ILogger<DuplicateDetector>? logger = null)
{
    /// <summary>Files up to this size are fully covered by the fast hash (two 64 KB samples).</summary>
    public const long FullyHashedSize = 128 * 1024;

    private readonly ILogger _log = logger ?? (ILogger)NullLogger.Instance;

    /// <returns>The number of staged assets marked as duplicates.</returns>
    public async Task<int> MarkAsync(long sessionId, bool includeDuplicates, CancellationToken ct = default)
    {
        await using var db = catalog.CreateDbContext();
        var staged = await db.AssetFiles.IgnoreQueryFilters().Include(f => f.Stored)
            .Where(f => f.Role == FileRole.Primary && f.FastHash != null && f.Asset.ImportSessionId == sessionId).ToListAsync(ct);
        if (staged.Count == 0) return 0;

        var candidates = new List<AssetFile>();
        foreach (var chunk in staged.Select(f => f.FastHash!).Distinct().Chunk(500))
            candidates.AddRange(await db.AssetFiles.IgnoreQueryFilters().Include(f => f.Stored)
                .Where(f => f.Role == FileRole.Primary && chunk.Contains(f.FastHash) && f.Asset.ImportSessionId == null).ToListAsync(ct));
        var byHash = candidates.ToLookup(f => f.FastHash!);

        var marked = 0;
        foreach (var file in staged)
        {
            ct.ThrowIfCancellationRequested();
            AssetFile? original = null;
            foreach (var candidate in byHash[file.FastHash!])
            {
                if (await SameContentAsync(file, candidate, ct))
                {
                    original = candidate;
                    break;
                }
            }
            if (original is null) continue;
            await db.AllAssets.Where(a => a.Id == file.AssetId).ExecuteUpdateAsync(s => s
                .SetProperty(a => a.DuplicateOfAssetId, original.AssetId)
                .SetProperty(a => a.Included, includeDuplicates), ct);
            marked++;
        }
        await db.SaveChangesAsync(ct); // content hashes computed along the way
        if (marked > 0) _log.LogInformation("Import {Session}: {Count} files are already cataloged", sessionId, marked);
        return marked;
    }

    private async Task<bool> SameContentAsync(AssetFile staged, AssetFile existing, CancellationToken ct)
    {
        if (staged.Location == existing.Location || staged.Size <= FullyHashedSize) return true;
        var a = await FullHashAsync(staged, ct);
        var b = await FullHashAsync(existing, ct);
        // When the cataloged original cannot be read any more, the fast hash (size + 128 KB of samples) decides.
        return a is null || b is null || a == b;
    }

    private async Task<string?> FullHashAsync(AssetFile file, CancellationToken ct)
    {
        if (file.ContentHash is { } known) return known;
        try
        {
            using var local = await resolver.ResolveAsync(file, ct);
            return file.ContentHash = await Fingerprint.FullHashAsync(local.Path, ct);
        }
        catch (Exception e) when (e is OriginalUnavailableException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
