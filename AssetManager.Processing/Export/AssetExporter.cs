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
/// <param name="Dependency">A file referenced by the exported assets (Unity "include referenced files"), not one of their own;
/// <paramref name="FileId"/> is then 0 and <paramref name="AssetId"/> is the asset that references it.</param>
public sealed record ExportEntry(long AssetId, long FileId, string RelativeTarget, bool Dependency = false)
{
    /// <summary>The file's Unity GUID, when known (package entry or <c>.meta</c> next to the original).</summary>
    public string? Guid { get; init; }
}

/// <summary>A file of the export whose GUID is already used in the target Unity project (spec "GUID already in project").</summary>
/// <param name="ExistingMeta">The project's <c>.meta</c> that has the GUID.</param>
public sealed record GuidConflict(string RelativeTarget, string Guid, string ExistingMeta);

/// <summary>
/// An extraction planned before anything is written: every target path, and which of them collide with existing files
/// or with each other. The UI asks the user about the conflicts, then runs the export with their choices.
/// </summary>
public sealed record ExportPlan(IReadOnlyList<ExportEntry> Entries, IReadOnlyList<string> Conflicts)
{
    /// <summary>Problems found while planning, such as referenced files that could not be read.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>Unity project export into a project: files whose GUID the project already has.</summary>
    public IReadOnlyList<GuidConflict> GuidConflicts { get; init; } = [];
}

/// <param name="TargetFolder">The folder to write into (created when missing).</param>
/// <param name="PreserveStructure">Keep each file's original relative path (Unity: <c>Assets/...</c>), else write flat.</param>
/// <param name="DefaultChoice">For conflicts without an entry in <paramref name="Choices"/>.</param>
/// <param name="Choices">Per-conflict choices, keyed by the relative target path as the plan lists it.</param>
/// <param name="UnityMeta">Unity project export (spec "Unity project export"): write each file's original <c>.meta</c> next to it.</param>
/// <param name="IncludeReferences">Also write the files the assets reference by GUID in their source (spec "Referenced files in Unity export").</param>
/// <param name="SkipGuids">"Skip conflicting": files with these GUIDs (already in the target project) are left out.</param>
public sealed record ExportOptions(
    string TargetFolder, bool PreserveStructure, ConflictChoice DefaultChoice = ConflictChoice.Rename,
    IReadOnlyDictionary<string, ConflictChoice>? Choices = null, bool UnityMeta = false, bool IncludeReferences = false,
    IReadOnlySet<string>? SkipGuids = null);

public sealed record ExportFailure(long AssetId, string Name, string Reason);

/// <param name="Written">Absolute paths of the files written.</param>
/// <param name="Exported">Assets fully exported.</param>
/// <param name="SkippedFiles">Files left out because the user chose "skip" for their conflict.</param>
/// <param name="Unavailable">Assets left out, with the reason (missing drive, unreadable container).</param>
public sealed record ExportResult(IReadOnlyList<string> Written, int Exported, int SkippedFiles, IReadOnlyList<ExportFailure> Unavailable)
{
    /// <summary>The <c>.meta</c> files written next to <see cref="Written"/> (Unity project export).</summary>
    public IReadOnlyList<string> MetaWritten { get; init; } = [];

    /// <summary>Referenced files written (included in <see cref="Written"/>).</summary>
    public int Dependencies { get; init; }

    /// <summary>Problems that did not stop the export, such as referenced files that could not be read.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
}

/// <summary>
/// Copies assets' original files out of the catalog (design D9): extract to a folder, or copy to the clipboard through
/// a temporary folder. Every file is resolved through <see cref="ILocationResolver"/>, so a stored copy is used first,
/// then the live file, then the file extracted from its container. An asset is exported whole or not at all: when one
/// of its files cannot be resolved, it is reported and the other assets are still exported (spec "Partial availability").
/// </summary>
public sealed class AssetExporter(CatalogStore catalog, ILocationResolver resolver, ILogger<AssetExporter>? logger = null, UnityReferences? references = null)
{
    private readonly ILogger _log = logger ?? (ILogger)NullLogger.Instance;

    /// <summary>Root of the temporary folders that clipboard copies are written to.</summary>
    public string TempRoot { get; init; } = AssetManager.Core.Settings.AppPaths.Export;

    /// <summary>Plans an extraction: target paths and the ones that conflict. Reads only the catalog.</summary>
    public Task<ExportPlan> PlanAsync(IReadOnlyCollection<long> assetIds, string targetFolder, bool preserveStructure, CancellationToken ct = default) =>
        PlanAsync(assetIds, new ExportOptions(targetFolder, preserveStructure), ct);

    /// <summary>
    /// Plans an extraction. With <see cref="ExportOptions.IncludeReferences"/> the referenced files are added as dependency
    /// entries; with <see cref="ExportOptions.UnityMeta"/> into a Unity project, GUIDs the project already has are listed.
    /// Nothing is written.
    /// </summary>
    public async Task<ExportPlan> PlanAsync(IReadOnlyCollection<long> assetIds, ExportOptions options, CancellationToken ct = default)
    {
        var (targetFolder, preserveStructure) = (options.TargetFolder, options.PreserveStructure);
        var entries = new List<ExportEntry>();
        foreach (var asset in await LoadAsync(assetIds, ct))
            foreach (var f in Ordered(asset))
                entries.Add(new ExportEntry(asset.Id, f.Id, TargetFor(f, preserveStructure)) { Guid = options.UnityMeta ? GuidOf(f) : null });
        var notes = new List<string>();
        if (options.IncludeReferences)
        {
            using var deps = await ReferencesAsync(assetIds, ct);
            entries.AddRange(deps.Files.Select(d => new ExportEntry(d.ReferencedBy, 0, TargetFor(d.RelativePath, preserveStructure), Dependency: true) { Guid = d.Guid }));
            notes.AddRange(deps.Problems);
        }
        var guidConflicts = options.UnityMeta && UnityProject.FindRoot(targetFolder) is { } project
            ? await Task.Run(() => UnityProject.GuidConflicts(project, targetFolder, entries), ct)
            : [];

        var conflicts = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries)
        {
            // A path that is already on disk, or that an earlier file of this export also wants.
            var exists = File.Exists(Path.Combine(targetFolder, e.RelativeTarget));
            if ((!seen.Add(e.RelativeTarget) || exists) && !conflicts.Contains(e.RelativeTarget, StringComparer.OrdinalIgnoreCase))
                conflicts.Add(e.RelativeTarget);
        }
        return new ExportPlan(entries, conflicts) { Notes = notes, GuidConflicts = guidConflicts };
    }

    /// <summary>The file's Unity GUID: from the package entry, or the <c>.meta</c> next to a live original.</summary>
    private string? GuidOf(AssetFile f)
    {
        if (f.UnityGuid is { } g) return g;
        if (f.Location.IsInContainer || resolver.GetOuterPath(f.Location) is not { } path) return null;
        return UnityProject.MetaFor(path) is { } meta ? AssetManager.Processing.Handlers.UnityMaterial.GuidFromMeta(meta) : null;
    }

    private Task<UnityDependencies> ReferencesAsync(IReadOnlyCollection<long> assetIds, CancellationToken ct) =>
        references?.FindAsync(assetIds, ct) ?? throw new InvalidOperationException("Referenced files need the Unity reference scanner.");

    /// <summary>Extracts the assets' files to a folder.</summary>
    public async Task<ExportResult> ExtractAsync(IReadOnlyCollection<long> assetIds, ExportOptions options, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(options.TargetFolder);
        var written = new List<string>();
        var metas = new List<string>();
        var failures = new List<ExportFailure>();
        int exported = 0, skipped = 0, done = 0, dependencies = 0;
        using var deps = options.IncludeReferences ? await ReferencesAsync(assetIds, ct) : UnityDependencies.None;
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
                    if (!Write(local.Path, TargetFor(file, options.PreserveStructure))) skipped++;
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
        var notes = deps.Problems.ToList();
        foreach (var dep in deps.Files)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (Write(dep.LocalPath, TargetFor(dep.RelativePath, options.PreserveStructure))) dependencies++;
                else skipped++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                notes.Add($"Referenced file {dep.RelativePath} could not be written: {e.Message}");
            }
        }
        _log.LogInformation("Exported {Exported} assets ({Files} files, {Dependencies} referenced) to {Target}; {Skipped} files skipped, {Failed} assets unavailable",
            exported, written.Count, dependencies, options.TargetFolder, skipped, failures.Count);
        return new ExportResult(written, exported, skipped, failures) { MetaWritten = metas, Dependencies = dependencies, Notes = notes };

        // Copies one file (and its .meta for a Unity export) per the conflict choices; false when skipped.
        bool Write(string source, string relative)
        {
            var meta = options.UnityMeta ? UnityProject.MetaFor(source) : null;
            if (meta is not null && options.SkipGuids is { Count: > 0 } skip
                && AssetManager.Processing.Handlers.UnityMaterial.GuidFromMeta(meta) is { } guid && skip.Contains(guid))
                return false; // "Skip conflicting": the project already has this GUID
            var target = Path.Combine(options.TargetFolder, relative);
            if (File.Exists(target))
            {
                var choice = options.Choices is not null && options.Choices.TryGetValue(relative, out var c) ? c : options.DefaultChoice;
                if (choice == ConflictChoice.Skip) return false;
                if (choice == ConflictChoice.Rename) target = FreeName(target);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, overwrite: true);
            written.Add(target);
            // The .meta follows the file's final name, so a rename to "x 2.png" writes "x 2.png.meta".
            if (meta is not null)
            {
                File.Copy(meta, target + ".meta", overwrite: true);
                metas.Add(target + ".meta");
            }
            return true;
        }
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
    public static string TargetFor(AssetFile file, bool preserveStructure) =>
        TargetFor(file.RelativePath, string.IsNullOrEmpty(file.FileName) ? file.Location.FileName : file.FileName, preserveStructure);

    /// <summary>The target of a file known only by its path in the source (a referenced file).</summary>
    public static string TargetFor(string relativePath, bool preserveStructure) =>
        TargetFor(relativePath, Path.GetFileName(relativePath.Replace('\\', '/')), preserveStructure);

    private static string TargetFor(string relativePath, string fileName, bool preserveStructure)
    {
        var name = SafeSegment(fileName);
        if (!preserveStructure || string.IsNullOrWhiteSpace(relativePath)) return name;
        var segments = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)
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
