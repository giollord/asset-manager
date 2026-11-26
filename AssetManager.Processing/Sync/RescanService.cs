using AssetManager.Core.Domain;
using AssetManager.Core.Jobs;
using AssetManager.Data;
using AssetManager.Processing.Containers;
using AssetManager.Processing.Files;
using AssetManager.Processing.Handlers;
using AssetManager.Processing.Jobs;
using AssetManager.Processing.Settings;
using Microsoft.EntityFrameworkCore;

namespace AssetManager.Processing.Sync;

public enum RescanStatus { Unchanged, Changed, Skipped }

/// <summary>A file of a rescanned folder source; <see cref="AssetId"/> is set for changed and removed files.</summary>
public sealed record RescanFile(string RelativePath, long? AssetId = null);

/// <summary>
/// What changed in one source since its import (spec "Rescan imported sources"). Transient: it lives in the rescan job's
/// summary, not in the catalog.
/// </summary>
/// <param name="ContainerChanged">An archive or package source whose file changed (update it through the version flow).</param>
/// <param name="Path">The source's folder or file on disk, when available.</param>
public sealed record RescanResult(
    long SourceId, string Name, SourceKind Kind, RescanStatus Status,
    IReadOnlyList<RescanFile> Added, IReadOnlyList<RescanFile> Changed, IReadOnlyList<RescanFile> Removed,
    bool ContainerChanged = false, string? SkipReason = null, string? Path = null)
{
    public static RescanResult Skip(Source s, string reason) => new(s.Id, s.DisplayName, s.Kind, RescanStatus.Skipped, [], [], [], SkipReason: reason);
}

/// <summary>
/// Compares imported sources with their originals (design D11). Folder sources are listed again with the import's
/// rules (default ignored folders, non-asset files) and diffed against their files by path, size and content key;
/// archive and package sources compare their file's fingerprint. Sources whose drive is detached are skipped, never
/// reported as removed. Reads only; the catalog changes when the user acts on the results.
/// </summary>
public sealed class RescanService(CatalogStore catalog, ILocationResolver resolver, ClassificationSettings classification, IEnumerable<IContainerReader> readers)
{
    /// <summary>The committed top-level sources (nested ones are checked through their parent).</summary>
    public async Task<List<long>> RootSourceIdsAsync(CancellationToken ct = default)
    {
        await using var db = catalog.CreateDbContext();
        return await db.Sources.Where(s => s.ParentSourceId == null).OrderBy(s => s.DisplayName).Select(s => s.Id).ToListAsync(ct);
    }

    public async Task<RescanResult?> RescanAsync(long sourceId, CancellationToken ct = default)
    {
        await using var db = catalog.CreateDbContext();
        var source = await db.Sources.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sourceId, ct);
        if (source is null) return null;
        var path = resolver.GetOuterPath(source.Location);
        if (source.Location.IsInContainer)
            return RescanResult.Skip(source, "inside another archive or package; rescan its parent");
        if (path is null || (source.Kind == SourceKind.Folder ? !Directory.Exists(path) : !File.Exists(path)))
            return RescanResult.Skip(source, $"unavailable: {await resolver.DescribeMissingAsync(source.Location, ct)} is not attached or missing");

        if (source.Kind == SourceKind.File)
        {
            // A single imported file: re-import its asset when it changed.
            var file = await db.AssetFiles.AsNoTracking().Where(f => f.Asset.SourceId == sourceId && f.Role == FileRole.Primary)
                .Select(f => new { f.AssetId, f.RelativePath, f.Size, f.FastHash }).FirstOrDefaultAsync(ct);
            var touched = file is not null && await Task.Run(() => FileChanged(path, file.Size, file.FastHash, source.ImportedAt), ct);
            return new RescanResult(source.Id, source.DisplayName, source.Kind, touched ? RescanStatus.Changed : RescanStatus.Unchanged, [],
                touched ? [new RescanFile(file!.RelativePath, file.AssetId)] : [], [], Path: path);
        }
        if (source.Kind != SourceKind.Folder)
        {
            var changed = await Task.Run(() => ContainerChanged(source, path), ct);
            return new RescanResult(source.Id, source.DisplayName, source.Kind, changed ? RescanStatus.Changed : RescanStatus.Unchanged, [], [], [],
                ContainerChanged: changed, Path: path);
        }

        var files = await db.AssetFiles.AsNoTracking().Where(f => f.Asset.SourceId == sourceId)
            .Select(f => new { f.AssetId, f.RelativePath, f.Size, f.FastHash, f.Location }).ToListAsync(ct);
        var known = files.Where(f => !f.Location.IsInContainer)
            .GroupBy(f => Key(f.RelativePath)).ToDictionary(g => g.Key, g => g.First());
        // Nested archives and packages are sources of their own: compare their fingerprints, never list them as added.
        var children = await db.Sources.AsNoTracking().Where(s => s.ParentSourceId == sourceId).ToListAsync(ct);
        var childPaths = children.Select(c => (Child: c, Path: resolver.GetOuterPath(c.Location)))
            .Where(c => c.Path is not null && !c.Child.Location.IsInContainer)
            .ToDictionary(c => Key(System.IO.Path.GetRelativePath(path, c.Path!)), c => c.Child);
        var classes = await classification.GetAsync(ct);
        var classifier = new FileClassifier(classes);
        var ignore = new FolderIgnoreRules(classes.IgnoredFolders);

        return await Task.Run(() =>
        {
            var added = new List<RescanFile>();
            var changed = new List<RescanFile>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Enumerate(path, ignore, ct))
            {
                var rel = System.IO.Path.GetRelativePath(path, file).Replace('\\', '/');
                var key = Key(rel);
                seen.Add(key);
                if (childPaths.TryGetValue(key, out var child))
                {
                    if (ContainerChanged(child, file)) changed.Add(new RescanFile(rel));
                    continue;
                }
                if (known.TryGetValue(key, out var k))
                {
                    if (FileChanged(file, k.Size, k.FastHash, source.ImportedAt)) changed.Add(new RescanFile(rel, k.AssetId));
                    continue;
                }
                if (Imports(classifier, rel)) added.Add(new RescanFile(rel));
            }
            var removed = known.Where(k => !seen.Contains(k.Key)).Select(k => new RescanFile(k.Value.RelativePath, k.Value.AssetId)).ToList();
            var status = added.Count + changed.Count + removed.Count > 0 ? RescanStatus.Changed : RescanStatus.Unchanged;
            return new RescanResult(source.Id, source.DisplayName, source.Kind, status, added, changed, removed, Path: path);
        }, ct);
    }

    /// <summary>Whether the import would take this file: an asset type it handles, or an archive or package.</summary>
    private bool Imports(FileClassifier classifier, string relativePath)
    {
        if (readers.Any(r => r.CanRead(relativePath))) return true;
        var c = classifier.Classify(relativePath);
        return c.Kind == FileKind.Asset && c.Type != AssetType.Other;
    }

    /// <summary>Files under <paramref name="root"/>, not descending into ignored folders.</summary>
    private static IEnumerable<string> Enumerate(string root, FolderIgnoreRules ignore, CancellationToken ct)
    {
        var pending = new Stack<string>([root]);
        while (pending.TryPop(out var dir))
        {
            ct.ThrowIfCancellationRequested();
            string[] files, dirs;
            try
            {
                files = Directory.GetFiles(dir);
                dirs = Directory.GetDirectories(dir);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var f in files) yield return f;
            foreach (var d in dirs)
                if (ignore.Match(System.IO.Path.GetRelativePath(root, d)) is null) pending.Push(d);
        }
    }

    /// <summary>Size first; a file modified after the import is compared by content key, so a touched file is not "changed".</summary>
    private static bool FileChanged(string path, long size, string? fastHash, DateTime importedAt)
    {
        var info = new FileInfo(path);
        if (info.Length != size) return true;
        if (info.LastWriteTimeUtc <= importedAt) return false;
        try
        {
            return fastHash is null || Fingerprint.ContentKey(path) != fastHash;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>The stored fingerprint, or for sources imported before fingerprints were kept, a change after the import.</summary>
    private static bool ContainerChanged(Source source, string path)
    {
        if (source.Fingerprint is null) return new FileInfo(path).LastWriteTimeUtc > source.ImportedAt;
        try
        {
            return Fingerprint.ContentKey(path) != source.Fingerprint;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static string Key(string relativePath) => relativePath.Replace('\\', '/').TrimStart('/').ToLowerInvariant();
}

/// <summary>Payload of "sources.rescan": the sources to rescan, or all top-level sources when null.</summary>
public sealed record RescanPayload(long[]? SourceIds);

/// <summary>Rescans sources (spec "Rescan imported sources"); the results go into the job summary.</summary>
public sealed class RescanJob(RescanService rescan) : IJobHandler
{
    public const string JobType = "sources.rescan";

    public string Type => JobType;

    public JobPool Pool => JobPool.Io;

    public async Task RunAsync(JobContext context, CancellationToken ct)
    {
        var ids = context.Payload<RescanPayload>().SourceIds ?? [.. await rescan.RootSourceIdsAsync(ct)];
        context.Progress.SetStage("Rescanning sources", 0, 1, ids.Length);
        var results = new List<RescanResult>();
        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            if (await rescan.RescanAsync(id, ct) is { } r)
            {
                results.Add(r);
                context.Progress.Advance(1, r.Name);
            }
            else context.Progress.Advance(1);
        }
        context.Progress.SetSummary(new RescanSummary(results));
    }
}

/// <summary>The job summary of a rescan.</summary>
public sealed record RescanSummary(IReadOnlyList<RescanResult> Results)
{
    public int Changed => Results.Count(r => r.Status == RescanStatus.Changed);

    public int Skipped => Results.Count(r => r.Status == RescanStatus.Skipped);
}
