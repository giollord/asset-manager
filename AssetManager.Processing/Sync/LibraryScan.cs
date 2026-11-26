using System.Text.RegularExpressions;
using AssetManager.Core;
using AssetManager.Core.Domain;
using AssetManager.Core.Jobs;
using AssetManager.Core.Settings;
using AssetManager.Data;
using AssetManager.Processing.Containers;
using AssetManager.Processing.Import;
using AssetManager.Processing.Settings;
using Microsoft.EntityFrameworkCore;

namespace AssetManager.Processing.Sync;

/// <summary>
/// A pack in a library folder that is not imported yet (spec "Library folders"). <see cref="VersionOfSourceId"/> is
/// set when it looks like a new version of a cataloged source; importing it then proposes the replacement.
/// </summary>
public sealed record LibraryEntry(string Path, string Name, SourceKind Kind, string Folder, long? VersionOfSourceId = null, string? VersionOfName = null);

/// <summary>The job summary of a library scan; <see cref="Unavailable"/> lists folders whose drive is detached.</summary>
public sealed record LibraryScanSummary(IReadOnlyList<LibraryEntry> Entries, IReadOnlyList<string> Unavailable)
{
    public int NewPacks => Entries.Count;
}

/// <summary>
/// Lists the immediate entries of the library folders (folders, archives, packages) that are not the location of a
/// cataloged source (design D11). New versions are recognized by name (version numbers ignored) or, for folders, by
/// the file-path overlap <see cref="SourceVersions"/> uses. Reads only.
/// </summary>
public sealed class LibraryScanService(
    CatalogStore catalog, ILocationResolver resolver, IAppSettingsStore settings, ClassificationSettings classification,
    IEnumerable<IContainerReader> readers, SourceVersions versions)
{
    /// <summary>Folders with more files are matched by name only (listing them is not "cheap").</summary>
    public const int MaxListedFiles = 5000;

    public async Task<LibraryScanSummary> ScanAsync(IJobProgress? progress = null, CancellationToken ct = default)
    {
        var folders = settings.Current.LibraryFolders.Select(Location.Parse).ToList();
        progress?.SetStage("Scanning library folders", 0, 1, folders.Count);
        var ignore = new FolderIgnoreRules((await classification.GetAsync(ct)).IgnoredFolders);
        List<(long Id, string Name, Location Location, SourceKind Kind)> sources;
        await using (var db = catalog.CreateDbContext())
            sources = (await db.Sources.AsNoTracking().Select(s => new { s.Id, s.DisplayName, s.Location, s.Kind }).ToListAsync(ct))
                .Select(s => (s.Id, s.DisplayName, s.Location, s.Kind)).ToList();
        var imported = sources.Where(s => !s.Location.IsInContainer).Select(s => resolver.GetOuterPath(s.Location)).OfType<string>()
            .Select(Full).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var byName = sources.Where(s => s.Kind != SourceKind.File && NameKey(s.Name).Length > 0).ToLookup(s => NameKey(s.Name));

        var entries = new List<LibraryEntry>();
        var unavailable = new List<string>();
        foreach (var location in folders)
        {
            ct.ThrowIfCancellationRequested();
            var path = resolver.GetOuterPath(location);
            if (path is null || !Directory.Exists(path))
            {
                unavailable.Add(path ?? await resolver.DescribeMissingAsync(location, ct));
                progress?.Advance();
                continue;
            }
            foreach (var entry in Children(path, ignore))
            {
                ct.ThrowIfCancellationRequested();
                if (imported.Contains(Full(entry.Path))) continue;
                var key = NameKey(entry.Name);
                var match = key.Length > 0 ? byName[key].FirstOrDefault() : default;
                (long Id, string Name)? version = match.Id != 0 ? (match.Id, match.Name) : null;
                if (version is null && entry.Kind == SourceKind.Folder && Listing(entry.Path, ignore) is { } files
                    && await versions.MatchListingAsync(files, ct) is { } byFiles)
                    version = (byFiles.Id, sources.First(s => s.Id == byFiles.Id).Name);
                entries.Add(new LibraryEntry(entry.Path, entry.Name, entry.Kind, path, version?.Id, version?.Name));
            }
            progress?.Advance(1, path);
        }
        return new LibraryScanSummary(entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList(), unavailable);
    }

    private IEnumerable<(string Path, string Name, SourceKind Kind)> Children(string folder, FolderIgnoreRules ignore)
    {
        string[] dirs, files;
        try
        {
            dirs = Directory.GetDirectories(folder);
            files = Directory.GetFiles(folder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            yield break;
        }
        foreach (var d in dirs.Order(StringComparer.OrdinalIgnoreCase))
        {
            var name = System.IO.Path.GetFileName(d);
            if (ignore.Match(name) is null) yield return (d, name, SourceKind.Folder);
        }
        foreach (var f in files.Order(StringComparer.OrdinalIgnoreCase))
            if (readers.FirstOrDefault(r => r.CanRead(f)) is { } reader) yield return (f, System.IO.Path.GetFileName(f), reader.Kind);
    }

    /// <summary>The folder's files relative to it, or null when there are too many to compare cheaply.</summary>
    private static List<string>? Listing(string folder, FolderIgnoreRules ignore)
    {
        var result = new List<string>();
        var pending = new Stack<string>([folder]);
        while (pending.TryPop(out var dir))
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir))
                {
                    if (result.Count == MaxListedFiles) return null;
                    result.Add(System.IO.Path.GetRelativePath(folder, f).Replace('\\', '/'));
                }
                foreach (var d in Directory.EnumerateDirectories(dir))
                    if (ignore.Match(System.IO.Path.GetRelativePath(folder, d)) is null) pending.Push(d);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return result;
    }

    private static string Full(string path) => System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));

    private static readonly string[] ContainerExtensions = [".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".unitypackage"];

    /// <summary>
    /// A pack name without container extension, version numbers and punctuation, so "Forest Pack v1.2.zip" and
    /// "ForestPack_2.0" compare equal.
    /// </summary>
    public static string NameKey(string name)
    {
        var n = name.Trim();
        foreach (var ext in ContainerExtensions)
            if (n.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) n = n[..^ext.Length];
        n = n.ToLowerInvariant();
        n = Regex.Replace(n, @"(^|[\s_\-.(\[])v?\d+([._]\d+)*(?=$|[\s_\-.)\]])", " ");
        return Regex.Replace(n, "[^a-z0-9]", "");
    }
}

/// <summary>Payload of the rescan and library scan jobs started by the startup check.</summary>
public sealed record LibraryScanPayload(bool Startup = false);

/// <summary>Scans the library folders (spec "Library folders"); the entries go into the job summary.</summary>
public sealed class LibraryScanJob(LibraryScanService scan) : IJobHandler
{
    public const string JobType = "library.scan";

    public string Type => JobType;

    public JobPool Pool => JobPool.Io;

    public async Task RunAsync(JobContext context, CancellationToken ct) =>
        context.Progress.SetSummary(await scan.ScanAsync(context.Progress, ct));
}

/// <summary>
/// "Check for changes at startup" (spec "When scans run"): 30 s after startup, rescans all sources and scans the
/// library folders as background jobs on the I/O pool. The results are only reported; nothing is imported.
/// </summary>
public sealed class SyncStartupTask(IAppSettingsStore settings, IJobQueue jobs) : IStartupTask
{
    public const string TitlePrefix = "Startup check: ";

    public int Order => 60;

    public TimeSpan Delay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The delayed enqueue (for tests).</summary>
    public Task Pending { get; private set; } = Task.CompletedTask;

    public Task RunAsync(CancellationToken ct)
    {
        if (!settings.Current.CheckForChangesAtStartup) return Task.CompletedTask;
        Pending = Task.Run(async () =>
        {
            await Task.Delay(Delay, ct);
            await jobs.EnqueueAsync(RescanJob.JobType, TitlePrefix + "rescan sources", new RescanPayload(null), ct: ct);
            if (settings.Current.LibraryFolders.Count > 0)
                await jobs.EnqueueAsync(LibraryScanJob.JobType, TitlePrefix + "scan library folders", new LibraryScanPayload(true), ct: ct);
        }, ct);
        return Task.CompletedTask;
    }
}
