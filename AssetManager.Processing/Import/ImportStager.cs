using System.Collections.Concurrent;
using AssetManager.Core.Domain;
using AssetManager.Core.Jobs;
using AssetManager.Data;
using AssetManager.Data.Services;
using AssetManager.Processing.Containers;
using AssetManager.Processing.Files;
using AssetManager.Processing.Handlers;
using AssetManager.Processing.Locations;
using AssetManager.Processing.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AssetManager.Processing.Import;

/// <summary>What the user started an import with (design D8).</summary>
/// <param name="IgnoredFolders">Folder ignore rules for this import only, added to the defaults.</param>
/// <param name="UseDefaultIgnores">Apply the catalog's default ignored folders.</param>
public sealed record ImportRequest(IReadOnlyList<string> Inputs, IReadOnlyList<string>? SharedTags = null, string? Instruction = null,
    AiDepth Depth = AiDepth.Full, bool IncludeDuplicates = false, IReadOnlyList<string>? IgnoredFolders = null, bool UseDefaultIgnores = true);

/// <summary>
/// Stages an import (design D8): scan the inputs through the virtual file system, group files into assets with their
/// companions, then extract metadata and render previews, all into staging rows (sources and assets whose
/// <c>ImportSessionId</c> is set, plus <see cref="StagedSkip"/> rows). Nothing is visible in the catalog until confirm.
/// Each stage can be repeated after an interruption.
/// </summary>
public sealed class ImportStager(
    CatalogStore catalog,
    VirtualFileSystem vfs,
    VolumeRegistry volumes,
    ClassificationSettings classification,
    CompanionResolver companions,
    PreviewService previews,
    PreviewSettings previewSettings,
    DuplicateDetector duplicates,
    SourceVersions versions,
    ILogger<ImportStager>? logger = null)
{
    public const int StageScan = 0, StageGroup = 1, StageProcess = 2;

    private readonly ILogger _log = logger ?? (ILogger)NullLogger.Instance;

    /// <summary>Parallel metadata/preview workers (the 3D renderer serializes model renders itself).</summary>
    public int Parallelism { get; init; } = Math.Max(1, Environment.ProcessorCount - 1);

    public async Task<ImportSession> CreateSessionAsync(ImportRequest request, CancellationToken ct = default)
    {
        var session = new ImportSession
        {
            CreatedAt = DateTime.UtcNow,
            State = ImportSessionState.Scanning,
            Inputs = [.. request.Inputs],
            SharedTags = [.. TagService.NormalizeAll(request.SharedTags ?? [])],
            Instruction = string.IsNullOrWhiteSpace(request.Instruction) ? null : request.Instruction.Trim(),
            AiDepth = request.Depth,
            IncludeDuplicates = request.IncludeDuplicates,
            IgnoredFolders = (request.IgnoredFolders ?? []).Select(FolderIgnoreRules.Normalize).Where(r => r.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            UseDefaultIgnores = request.UseDefaultIgnores,
        };
        await using var db = catalog.CreateDbContext();
        db.ImportSessions.Add(session);
        await db.SaveChangesAsync(ct);
        return session;
    }

    /// <summary>A staged file before grouping.</summary>
    private sealed record Entry(SourceFile File, VfsNode Node);

    /// <summary>Stages 1 and 2: scan the inputs and group the files into staged sources and assets.</summary>
    public async Task ScanAndGroupAsync(long sessionId, IJobProgress progress, int stageCount, CancellationToken ct = default)
    {
        ImportSession session;
        await using (var db = catalog.CreateDbContext())
        {
            session = await db.ImportSessions.AsNoTracking().FirstAsync(s => s.Id == sessionId, ct);
            if (session.StagedThrough >= StageGroup + 1) return;
        }
        await ClearStagedAsync(sessionId, ct); // a scan that was interrupted starts over

        var classes = await classification.GetAsync(ct);
        var classifier = new FileClassifier(classes);
        var ignore = new FolderIgnoreRules((session.UseDefaultIgnores ? classes.IgnoredFolders : []).Concat(session.IgnoredFolders));
        var sources = new Dictionary<string, Source>(); // by container/folder location
        var roots = new List<Source>();
        var entries = new Dictionary<Source, List<Entry>>();
        var skips = new List<(StagedSkip Skip, Source? Owner)>();
        void Skip(Source? owner, string path, SkipKind kind, string reason, Location? location) =>
            skips.Add((new StagedSkip { SessionId = sessionId, Path = path, Kind = kind, Reason = reason, Location = location }, owner));
        var volumeIds = new HashSet<string?>();

        progress.SetStage("Scanning", StageScan, stageCount);
        foreach (var input in session.Inputs)
        {
            var full = Path.GetFullPath(input);
            var location = volumes.FromPath(full);
            volumeIds.Add(location.VolumeId);
            Source? root = null;
            if (Directory.Exists(full))
            {
                root = NewSource(SourceKind.Folder, location, Path.GetFileName(full.TrimEnd('\\', '/')), sessionId, null);
                sources[location.ToString()] = root;
                roots.Add(root);
            }
            else if (!File.Exists(full))
            {
                Skip(null, full, SkipKind.Error, "not found", null);
                continue;
            }

            await foreach (var node in vfs.WalkAsync(full, location, ignore: ignore, ct: ct))
            {
                ct.ThrowIfCancellationRequested();
                var parentKey = node.Location.Container?.ToString();
                var parent = parentKey is not null ? sources.GetValueOrDefault(parentKey) : root;
                switch (node.Kind)
                {
                    case VfsNodeKind.Container:
                    {
                        var kind = node.ContainerKind ?? SourceKind.Archive;
                        var source = NewSource(kind, node.Location, Path.GetFileNameWithoutExtension(node.Location.FileName), sessionId, parent);
                        source.Store = node.Store ?? new StoreInfo();
                        if (node.Store?.Title is { Length: > 0 } title) source.DisplayName = title;
                        sources[node.Location.ToString()] = source;
                        if (parent is null) roots.Add(source);
                        break;
                    }
                    case VfsNodeKind.IgnoredFolder:
                        Skip(parent, node.RelativePath.Replace('\\', '/'), SkipKind.Ignored, $"ignored folder ({node.Error})", node.Location);
                        break;
                    case VfsNodeKind.ContainerError:
                        Skip(parent, node.RelativePath, SkipKind.Error, $"cannot open archive ({node.Error ?? "unreadable"})", node.Location);
                        break;
                    case VfsNodeKind.File when node.DepthLimited:
                        Skip(parent, node.RelativePath, SkipKind.Unsupported, "nested too deep (see the nesting depth setting)", node.Location);
                        break;
                    case VfsNodeKind.File:
                    {
                        // A single plain file given as input is its own source.
                        if (parent is null)
                        {
                            parent = NewSource(SourceKind.File, node.Location, node.Location.FileName, sessionId, null);
                            roots.Add(parent);
                            sources[node.Location.ToString()] = parent;
                        }
                        var c = classifier.Classify(node.RelativePath);
                        switch (c.Kind)
                        {
                            case FileKind.Asset when c.Type == AssetType.Other:
                                Skip(parent, node.RelativePath, SkipKind.Unsupported, $"unsupported file type ({Path.GetExtension(node.RelativePath)})", node.Location);
                                break;
                            case FileKind.Asset:
                                if (!entries.TryGetValue(parent, out var list)) entries[parent] = list = [];
                                list.Add(new Entry(new SourceFile(node.RelativePath, node.LocalPath, c) { UnityGuid = node.UnityGuid }, node));
                                break;
                            case FileKind.Noted:
                                Skip(parent, node.RelativePath, SkipKind.Noted, c.Reason ?? "script (noted)", node.Location);
                                break;
                            case FileKind.SourceDocument:
                                Skip(parent, node.RelativePath, SkipKind.Document, "license/readme document (read for the source)", node.Location);
                                break;
                            default:
                                Skip(parent, node.RelativePath, SkipKind.Ignored, c.Reason ?? "ignored", node.Location);
                                break;
                        }
                        progress.Advance(1, node.RelativePath);
                        break;
                    }
                }
            }
        }
        await volumes.RememberAsync(volumeIds, ct);

        progress.SetStage("Grouping files", StageGroup, stageCount, entries.Values.Sum(l => l.Count));
        var now = DateTime.UtcNow;
        await using (var db = catalog.CreateDbContext())
        {
            var sharedTags = await TagService.GetOrCreateAsync(db, session.SharedTags, ct);
            var created = new List<(Asset Asset, VfsNode Node)>();
            foreach (var source in sources.Values.Distinct()) db.Sources.Add(source);
            foreach (var (source, list) in entries)
            {
                var counted = new HashSet<string>();
                var byPath = list.ToDictionary(e => e.File.RelativePath);
                foreach (var group in companions.Group(list.Select(e => e.File).ToList(), ct))
                {
                    var node = byPath[group.Primary.RelativePath].Node;
                    var asset = new Asset
                    {
                        Source = source,
                        Type = group.Primary.Class.Type,
                        Name = group.Primary.Stem,
                        ImportSessionId = sessionId,
                        AiStatus = session.AiDepth == AiDepth.None ? AiStatus.NotEnriched : AiStatus.Pending,
                        ImportedAt = now,
                        UpdatedAt = now,
                    };
                    asset.Files.Add(FileOf(group.Primary, node, FileRole.Primary, null, null));
                    foreach (var link in group.Companions)
                        asset.Files.Add(FileOf(link.File, byPath[link.File.RelativePath].Node, FileRole.Companion, link.Material, link.Slot));
                    foreach (var tag in sharedTags) asset.Tags.Add(new AssetTag { Tag = tag, Origin = TagOrigin.Import });
                    source.Assets.Add(asset);
                    created.Add((asset, node));
                    // A companion shared by several models counts once.
                    progress.Advance(new[] { group.Primary.RelativePath }.Concat(group.Companions.Select(c => c.File.RelativePath)).Count(counted.Add),
                        group.Primary.RelativePath);
                }
            }
            await db.SaveChangesAsync(ct);

            // Unity's embedded previews: the fallback when a file cannot be rendered.
            foreach (var (asset, node) in created)
            {
                if (node.PreviewPath is { } png && File.Exists(png))
                    db.Previews.Add(new PreviewBlob { AssetId = asset.Id, Kind = PreviewKind.UnityPreview, Mime = "image/png", Bytes = await File.ReadAllBytesAsync(png, ct) });
            }
            foreach (var (skip, owner) in skips) skip.SourceId = owner?.Id;
            db.StagedSkips.AddRange(skips.Select(x => x.Skip));
            await db.SaveChangesAsync(ct);
            progress.SetCurrentItem("Checking for files already in the catalog...");
            await duplicates.MarkAsync(sessionId, session.IncludeDuplicates, ct);
            progress.SetCurrentItem("Looking for earlier versions of these sources...");
            await versions.ProposeAsync(sessionId, ct);
            await db.ImportSessions.Where(s => s.Id == sessionId).ExecuteUpdateAsync(u => u.SetProperty(s => s.StagedThrough, StageGroup + 1), ct);
        }
        _log.LogInformation("Import {Session}: staged {Sources} sources, {Assets} assets, {Skips} skipped entries",
            sessionId, sources.Count, entries.Values.Sum(l => l.Count), skips.Count);
    }

    /// <summary>Stage 3: metadata and previews for every staged asset not processed yet, in parallel.</summary>
    public async Task ProcessAsync(long sessionId, IJobProgress progress, int stageCount, CancellationToken ct = default)
    {
        List<long> todo;
        int total;
        await using (var db = catalog.CreateDbContext())
        {
            var session = await db.ImportSessions.AsNoTracking().FirstAsync(s => s.Id == sessionId, ct);
            if (session.StagedThrough >= StageProcess + 1) return;
            var assets = db.AllAssets.Where(a => a.ImportSessionId == sessionId);
            total = await assets.CountAsync(ct);
            todo = await assets.Where(a => a.PreviewOptionsVersion == 0).OrderBy(a => a.Id).Select(a => a.Id).ToListAsync(ct);
        }
        progress.SetStage("Generating previews", StageProcess, stageCount, total);
        if (total > todo.Count) progress.Advance(total - todo.Count);

        var options = await previewSettings.GetAsync(ct);
        var copied = await CopyFromOriginalsAsync(todo, options.Version(), ct);
        if (copied.Count > 0)
        {
            progress.Advance(copied.Count);
            todo = todo.Except(copied).ToList();
        }
        await Parallel.ForEachAsync(todo, new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = ct }, async (id, token) =>
        {
            var outcome = await previews.ProcessOneAsync(id, options, token);
            if (outcome.Skipped is { } s) progress.Fail(id.ToString(), s.Name, s.Reason);
            else if (outcome.PreviewError is { } e) progress.Warn(id.ToString(), outcome.Name, e);
            progress.Advance(1, outcome.Name);
        });

        await using (var db = catalog.CreateDbContext())
            await db.ImportSessions.Where(s => s.Id == sessionId).ExecuteUpdateAsync(u => u.SetProperty(s => s.StagedThrough, StageProcess + 1), ct);
    }

    /// <summary>
    /// Duplicates of cataloged assets reuse the original's metadata and previews (same content) instead of rendering
    /// again, when the original's previews are current.
    /// </summary>
    /// <returns>The staged asset ids handled this way.</returns>
    private async Task<List<long>> CopyFromOriginalsAsync(List<long> ids, int optionsVersion, CancellationToken ct)
    {
        var done = new List<long>();
        await using var db = catalog.CreateDbContext();
        foreach (var chunk in ids.Chunk(500))
        {
            var pairs = await db.AllAssets.Where(a => chunk.Contains(a.Id) && a.DuplicateOfAssetId != null)
                .Select(a => new { a.Id, Original = a.DuplicateOfAssetId!.Value }).ToListAsync(ct);
            foreach (var pair in pairs)
            {
                var original = await db.Assets.AsNoTracking().FirstOrDefaultAsync(a => a.Id == pair.Original && a.PreviewOptionsVersion == optionsVersion, ct);
                if (original is null) continue;
                var staged = await db.AllAssets.FirstAsync(a => a.Id == pair.Id, ct);
                staged.Type = original.Type;
                staged.Image = original.Image;
                staged.Audio = original.Audio;
                staged.Model = original.Model;
                staged.Font = original.Font;
                staged.Video = original.Video;
                staged.Document = original.Document;
                staged.ScaleAnomaly = original.ScaleAnomaly;
                staged.OrientationUncertain = original.OrientationUncertain;
                staged.OrientationVerified = original.OrientationVerified;
                staged.TexturesMissing = original.TexturesMissing;
                staged.PreviewError = original.PreviewError;
                staged.PreviewOptionsVersion = original.PreviewOptionsVersion;
                if (original.AiStatus == AiStatus.Enriched && staged.AiStatus == AiStatus.Pending)
                {
                    // Same content: the original's AI results apply as they are (the user's own edits are not copied).
                    (staged.Description, staged.Style, staged.Colors, staged.IsGameAsset) = (original.Description, original.Style, original.Colors, original.IsGameAsset);
                    (staged.AiStatus, staged.AiInputMode) = (original.AiStatus, original.AiInputMode);
                    var aiTags = await db.AssetTags.AsNoTracking().Where(t => t.AssetId == pair.Original && t.Origin == TagOrigin.AI).Select(t => t.TagId).ToListAsync(ct);
                    var have = await db.AssetTags.Where(t => t.AssetId == pair.Id).Select(t => t.TagId).ToListAsync(ct);
                    db.AssetTags.AddRange(aiTags.Except(have).Select(tagId => new AssetTag { AssetId = pair.Id, TagId = tagId, Origin = TagOrigin.AI }));
                }
                await db.Previews.Where(p => p.AssetId == pair.Id && p.Kind != PreviewKind.UnityPreview).ExecuteDeleteAsync(ct);
                var blobs = await db.Previews.AsNoTracking().Where(p => p.AssetId == pair.Original && p.Kind != PreviewKind.UnityPreview).ToListAsync(ct);
                foreach (var blob in blobs)
                {
                    blob.Id = 0;
                    blob.AssetId = pair.Id;
                }
                db.Previews.AddRange(blobs);
                await db.SaveChangesAsync(ct);
                db.ChangeTracker.Clear();
                done.Add(pair.Id);
            }
        }
        return done;
    }

    /// <summary>Cancels a session: removes every staged row; the catalog is left exactly as it was.</summary>
    public async Task CancelSessionAsync(long sessionId, CancellationToken ct = default)
    {
        await ClearStagedAsync(sessionId, ct);
        await using var db = catalog.CreateDbContext();
        await db.ImportSessions.Where(s => s.Id == sessionId).ExecuteUpdateAsync(u => u.SetProperty(s => s.State, ImportSessionState.Cancelled), ct);
        await TagService.DeleteUnusedAsync(db, ct);
    }

    private async Task ClearStagedAsync(long sessionId, CancellationToken ct)
    {
        await using var db = catalog.CreateDbContext();
        await db.StagedSkips.Where(s => s.SessionId == sessionId).ExecuteDeleteAsync(ct);
        var assetIds = await db.AllAssets.Where(a => a.ImportSessionId == sessionId).Select(a => a.Id).ToListAsync(ct);
        foreach (var chunk in assetIds.Chunk(500))
            await db.AllAssets.Where(a => chunk.Contains(a.Id)).ExecuteDeleteAsync(ct);
        await db.AllSources.Where(s => s.ImportSessionId == sessionId).ExecuteDeleteAsync(ct);
        await db.ImportSessions.Where(s => s.Id == sessionId).ExecuteUpdateAsync(u => u.SetProperty(s => s.StagedThrough, 0), ct);
    }

    private static Source NewSource(SourceKind kind, Location location, string name, long sessionId, Source? parent) => new()
    {
        Kind = kind,
        Location = location,
        VolumeId = location.VolumeId,
        DisplayName = name,
        ImportSessionId = sessionId,
        ParentSource = parent,
        ImportedAt = DateTime.UtcNow,
    };

    private static AssetFile FileOf(SourceFile file, VfsNode node, FileRole role, string? material, string? slot) => new()
    {
        Role = role,
        Location = node.Location,
        RelativePath = file.RelativePath.Replace('\\', '/'),
        FileName = file.FileName,
        Extension = file.Extension,
        Size = node.Size,
        FastHash = File.Exists(node.LocalPath) ? Fingerprint.ContentKey(node.LocalPath) : null,
        UnityGuid = file.UnityGuid,
        Material = material,
        Slot = slot,
    };
}
