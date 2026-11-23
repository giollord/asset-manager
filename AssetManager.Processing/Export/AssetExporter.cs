using AssetManager.Core.Domain;
using AssetManager.Core.Platform;
using AssetManager.Data;
using AssetManager.Processing.Containers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AssetManager.Processing.Export;

/// <summary>What to do with a target file that already exists (spec "Name conflict").</summary>
public enum ConflictChoice { Rename, Overwrite, Skip }

/// <summary>Where one file of an asset goes, relative to the target folder.</summary>
public sealed record ExportEntry(long AssetId, long FileId, string RelativeTarget);

/// <summary>
/// An extraction planned before anything is written: every target path, and which of them collide with existing files
/// or with each other. The UI asks the user about the conflicts, then runs the export with their choices.
/// </summary>
public sealed record ExportPlan(IReadOnlyList<ExportEntry> Entries, IReadOnlyList<string> Conflicts);

/// <param name="TargetFolder">The folder to write into (created when missing).</param>
/// <param name="PreserveStructure">Keep each file's original relative path (Unity: <c>Assets/...</c>), else write flat.</param>
/// <param name="DefaultChoice">For conflicts without an entry in <paramref name="Choices"/>.</param>
/// <param name="Choices">Per-conflict choices, keyed by the relative target path as the plan lists it.</param>
public sealed record ExportOptions(
    string TargetFolder, bool PreserveStructure, ConflictChoice DefaultChoice = ConflictChoice.Rename,
    IReadOnlyDictionary<string, ConflictChoice>? Choices = null);

public sealed record ExportFailure(long AssetId, string Name, string Reason);

/// <param name="Written">Absolute paths of the files written.</param>
/// <param name="Exported">Assets fully exported.</param>
/// <param name="SkippedFiles">Files left out because the user chose "skip" for their conflict.</param>
/// <param name="Unavailable">Assets left out, with the reason (missing drive, unreadable container).</param>
public sealed record ExportResult(IReadOnlyList<string> Written, int Exported, int SkippedFiles, IReadOnlyList<ExportFailure> Unavailable);

/// <summary>
/// Copies assets' original files out of the catalog (design D9): extract to a folder, or copy to the clipboard through
/// a temporary folder. Every file is resolved through <see cref="ILocationResolver"/>, so a stored copy is used first,
/// then the live file, then the file extracted from its container. An asset is exported whole or not at all: when one
/// of its files cannot be resolved, it is reported and the other assets are still exported (spec "Partial availability").
/// </summary>
public sealed class AssetExporter(CatalogStore catalog, ILocationResolver resolver, ILogger<AssetExporter>? logger = null)
{
    private readonly ILogger _log = logger ?? (ILogger)NullLogger.Instance;

    /// <summary>Root of the temporary folders that clipboard copies are written to.</summary>
    public string TempRoot { get; init; } = Path.Combine(Path.GetTempPath(), "AssetManager", "export");

    /// <summary>Plans an extraction: target paths and the ones that conflict. Reads only the catalog.</summary>
    public async Task<ExportPlan> PlanAsync(IReadOnlyCollection<long> assetIds, string targetFolder, bool preserveStructure, CancellationToken ct = default)
    {
        var entries = new List<ExportEntry>();
        foreach (var asset in await LoadAsync(assetIds, ct))
            foreach (var f in Ordered(asset))
                entries.Add(new ExportEntry(asset.Id, f.Id, TargetFor(f, preserveStructure)));

        var conflicts = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries)
        {
            // A path that is already on disk, or that an earlier file of this export also wants.
            var exists = File.Exists(Path.Combine(targetFolder, e.RelativeTarget));
            if ((!seen.Add(e.RelativeTarget) || exists) && !conflicts.Contains(e.RelativeTarget, StringComparer.OrdinalIgnoreCase))
                conflicts.Add(e.RelativeTarget);
        }
        return new ExportPlan(entries, conflicts);
    }

    /// <summary>Extracts the assets' files to a folder.</summary>
    public async Task<ExportResult> ExtractAsync(IReadOnlyCollection<long> assetIds, ExportOptions options, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(options.TargetFolder);
        var written = new List<string>();
        var failures = new List<ExportFailure>();
        int exported = 0, skipped = 0, done = 0;
        foreach (var asset in await LoadAsync(assetIds, ct))
        {
            ct.ThrowIfCancellationRequested();
            var files = Ordered(asset).ToList();
            var resolved = new List<(AssetFile File, ResolvedFile Local)>();
            try
            {
                var reason = await ResolveAllAsync(files, resolved, ct);
                if (reason is not null)
                {
                    failures.Add(new ExportFailure(asset.Id, asset.Name, reason));
                    continue;
                }
                foreach (var (file, local) in resolved)
                {
                    var relative = TargetFor(file, options.PreserveStructure);
                    var target = Path.Combine(options.TargetFolder, relative);
                    if (File.Exists(target))
                    {
                        var choice = options.Choices is not null && options.Choices.TryGetValue(relative, out var c) ? c : options.DefaultChoice;
                        if (choice == ConflictChoice.Skip)
                        {
                            skipped++;
                            continue;
                        }
                        if (choice == ConflictChoice.Rename) target = FreeName(target);
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(local.Path, target, overwrite: true);
                    written.Add(target);
                }
                exported++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                failures.Add(new ExportFailure(asset.Id, asset.Name, e.Message));
            }
            finally
            {
                foreach (var (_, local) in resolved) local.Dispose();
                progress?.Report(++done);
            }
        }
        _log.LogInformation("Exported {Exported} assets ({Files} files) to {Target}; {Skipped} files skipped, {Failed} assets unavailable",
            exported, written.Count, options.TargetFolder, skipped, failures.Count);
        return new ExportResult(written, exported, skipped, failures);
    }

    /// <summary>
    /// Writes the assets' files flat into a new temporary folder (renaming clashes) for the clipboard, and returns them.
    /// The caller puts <see cref="ExportResult.Written"/> on the clipboard. Older temporary folders are cleaned up.
    /// </summary>
    public Task<ExportResult> ExtractForClipboardAsync(IReadOnlyCollection<long> assetIds, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        CleanTemp(TimeSpan.FromDays(1));
        var folder = Path.Combine(TempRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
        return ExtractAsync(assetIds, new ExportOptions(folder, PreserveStructure: false, ConflictChoice.Rename), progress, ct);
    }

    /// <summary>
    /// A readable local path of the asset's primary file, for "open externally" and the in-app viewer: the live file,
    /// or a copy extracted to the cache. Dispose the result when done.
    /// </summary>
    public async Task<ResolvedFile> ResolvePrimaryAsync(long assetId, CancellationToken ct = default)
    {
        var asset = (await LoadAsync([assetId], ct)).SingleOrDefault() ?? throw new InvalidOperationException("The asset is no longer in the catalog.");
        var primary = Ordered(asset).First();
        return await resolver.ResolveAsync(primary, ct);
    }

    /// <summary>
    /// The on-disk file to select in Explorer ("reveal"): the primary file, or for a file inside an archive or package the
    /// outermost container file. Null when its drive is not attached.
    /// </summary>
    public async Task<string?> RevealablePathAsync(long assetId, CancellationToken ct = default)
    {
        var asset = (await LoadAsync([assetId], ct)).SingleOrDefault();
        if (asset is null) return null;
        var location = Ordered(asset).First().Location;
        return resolver.GetOuterPath(location) is { } p && File.Exists(p) ? p : null;
    }

    private async Task<string?> ResolveAllAsync(IReadOnlyList<AssetFile> files, List<(AssetFile, ResolvedFile)> resolved, CancellationToken ct)
    {
        foreach (var f in files)
        {
            try
            {
                resolved.Add((f, await resolver.ResolveAsync(f, ct)));
            }
            catch (OriginalUnavailableException e)
            {
                return $"Original unavailable: {e.Missing} is missing.";
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ContainerReadException)
            {
                return e.Message;
            }
        }
        return null;
    }

    private async Task<List<Asset>> LoadAsync(IReadOnlyCollection<long> assetIds, CancellationToken ct)
    {
        var result = new List<Asset>();
        await using var db = catalog.CreateDbContext();
        foreach (var chunk in assetIds.Distinct().Chunk(500))
            result.AddRange(await db.Assets.AsNoTracking().Include(a => a.Files).ThenInclude(f => f.Stored)
                .Where(a => chunk.Contains(a.Id)).ToListAsync(ct));
        var order = assetIds.Distinct().Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
        return result.OrderBy(a => order[a.Id]).ToList();
    }

    /// <summary>Primary file first, then companions by path.</summary>
    private static IEnumerable<AssetFile> Ordered(Asset asset) =>
        asset.Files.OrderBy(f => f.Role).ThenBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.Id);

    /// <summary>
    /// The file's path below the target folder: its original relative path (made safe) or just its name.
    /// </summary>
    public static string TargetFor(AssetFile file, bool preserveStructure)
    {
        var name = SafeSegment(string.IsNullOrEmpty(file.FileName) ? file.Location.FileName : file.FileName);
        if (!preserveStructure || string.IsNullOrWhiteSpace(file.RelativePath)) return name;
        var segments = file.RelativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(s => s is not ("." or "..")).Select(SafeSegment).Where(s => s.Length > 0).ToList();
        if (segments.Count == 0) return name;
        segments[^1] = name;
        return Path.Combine([.. segments]);
    }

    private static string SafeSegment(string segment)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(segment.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return cleaned.Length == 0 ? "_" : cleaned;
    }

    /// <summary><c>name.ext</c> → <c>name (2).ext</c>, <c>name (3).ext</c>, … the first that does not exist.</summary>
    public static string FreeName(string path)
    {
        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    private void CleanTemp(TimeSpan olderThan)
    {
        try
        {
            if (!Directory.Exists(TempRoot)) return;
            foreach (var dir in Directory.EnumerateDirectories(TempRoot))
                if (DateTime.UtcNow - Directory.GetCreationTimeUtc(dir) > olderThan)
                    Directory.Delete(dir, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.LogDebug(e, "Could not clean old clipboard exports");
        }
    }
}
