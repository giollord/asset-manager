using System.Runtime.CompilerServices;
using AssetManager.Core.Domain;
using AssetManager.Core.Settings;

namespace AssetManager.Processing.Containers;

/// <summary>Recursively lists the files of a folder (hidden and system files are skipped).</summary>
public static class FolderReader
{
    private static readonly EnumerationOptions Options = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
    };

    /// <summary>Files below <paramref name="folder"/> with their folder-relative paths ('' separators), sorted.</summary>
    public static IEnumerable<(string RelativePath, FileInfo File)> Enumerate(string folder) =>
        Walk(folder, FolderIgnoreRules.None).Where(x => x.File is not null).Select(x => (x.RelativePath, x.File!));

    /// <summary>
    /// Files below <paramref name="folder"/>, sorted by relative path, plus one entry (with <c>File</c> null and the
    /// matching rule) for each folder the rules ignore. Ignored folders are not entered at all.
    /// </summary>
    public static IEnumerable<(string RelativePath, FileInfo? File, string? IgnoredBy)> Walk(string folder, FolderIgnoreRules ignore)
    {
        var found = new List<(string, FileInfo?, string?)>();
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(folder));
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            foreach (var f in dir.EnumerateFiles("*", Options)) found.Add((Path.GetRelativePath(folder, f.FullName), f, null));
            foreach (var d in dir.EnumerateDirectories("*", Options))
            {
                var rel = Path.GetRelativePath(folder, d.FullName);
                if (ignore.Match(rel) is { } rule) found.Add((rel, null, rule));
                else pending.Push(d);
            }
        }
        return found.OrderBy(x => x.Item1, StringComparer.OrdinalIgnoreCase);
    }
}

public enum VfsNodeKind
{
    /// <summary>A folder skipped by an ignore rule (<see cref="VfsNode.Error"/> holds the rule); nothing below it is visited.</summary>
    IgnoredFolder,
    /// <summary>A file to catalog.</summary>
    File,

    /// <summary>A container that was opened; its files follow as nodes with a deeper location.</summary>
    Container,

    /// <summary>A container that could not be read (corrupt, encrypted…). See <see cref="VfsNode.Error"/>.</summary>
    ContainerError,
}

/// <summary>One item found while walking a source.</summary>
public sealed record VfsNode(VfsNodeKind Kind, Location Location, string LocalPath, string RelativePath, long Size, DateTime? Modified)
{
    /// <summary>Number of containers this item is inside of (0 for plain files and the outermost container).</summary>
    public int Depth => Location.Inner.Count;

    public SourceKind? ContainerKind { get; init; }
    public StoreInfo? Store { get; init; }
    public string? Error { get; init; }

    /// <summary>A container that was not opened because the nesting depth limit was reached; it is reported as a file.</summary>
    public bool DepthLimited { get; init; }

    public string? UnityGuid { get; init; }
    public string? MetaPath { get; init; }
    public string? PreviewPath { get; init; }
}

/// <summary>
/// Walks a source (file, folder, archive or package) and descends into containers found inside containers,
/// up to a nesting depth limit (default from settings, 5). Every node carries its full location chain.
/// Container contents are extracted to the cache; a node's <see cref="VfsNode.LocalPath"/> is valid only until the
/// enumeration moves past its container, so consumers process each node inside the loop.
/// </summary>
public sealed class VirtualFileSystem(IEnumerable<IContainerReader> readers, IAppSettingsStore? settings = null)
{
    private readonly IReadOnlyList<IContainerReader> _readers = readers.ToList();

    public IContainerReader? ReaderFor(string fileName) => _readers.FirstOrDefault(r => r.CanRead(fileName));

    /// <param name="localPath">The folder or file on disk.</param>
    /// <param name="location">Its catalog location (volume-relative).</param>
    /// <param name="maxDepth">How many container levels may be opened; null uses the settings value.</param>
    /// <param name="ignore">Folders to skip, applied to the folder and inside every container; null skips none.</param>
    /// <param name="opening">Called with a container's location right before it is opened (extraction can take long).</param>
    public async IAsyncEnumerable<VfsNode> WalkAsync(string localPath, Location location, int? maxDepth = null,
        FolderIgnoreRules? ignore = null, [EnumeratorCancellation] CancellationToken ct = default, Action<Location>? opening = null)
    {
        var depth = Math.Max(0, maxDepth ?? settings?.Current.MaxNestingDepth ?? 5);
        ignore ??= FolderIgnoreRules.None;
        if (Directory.Exists(localPath))
        {
            foreach (var (rel, file, rule) in FolderReader.Walk(localPath, ignore))
            {
                ct.ThrowIfCancellationRequested();
                var loc = new Location(location.VolumeId, Path.Combine(location.Path, rel), location.Inner);
                if (file is null)
                {
                    yield return new VfsNode(VfsNodeKind.IgnoredFolder, loc, Path.Combine(localPath, rel), rel, 0, null) { Error = rule };
                    continue;
                }
                // An archive or package counts as a folder: a rule naming it keeps it closed.
                if (ReaderFor(file.Name) is not null && ignore.Match(rel) is { } containerRule)
                {
                    yield return new VfsNode(VfsNodeKind.IgnoredFolder, loc, file.FullName, rel, file.Length, file.LastWriteTimeUtc) { Error = containerRule };
                    continue;
                }
                var node = new VfsNode(VfsNodeKind.File, loc, file.FullName, rel, file.Length, file.LastWriteTimeUtc);
                await foreach (var n in VisitAsync(node, depth, ignore, opening, ct)) yield return n;
            }
            yield break;
        }

        var info = new FileInfo(localPath);
        var root = new VfsNode(VfsNodeKind.File, location, localPath, info.Name, info.Exists ? info.Length : 0, info.Exists ? info.LastWriteTimeUtc : null);
        await foreach (var n in VisitAsync(root, depth, ignore, opening, ct)) yield return n;
    }

    private async IAsyncEnumerable<VfsNode> VisitAsync(VfsNode node, int maxDepth, FolderIgnoreRules ignore, Action<Location>? opening,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var reader = ReaderFor(node.Location.FileName);
        if (reader is null)
        {
            yield return node;
            yield break;
        }
        if (node.Depth + 1 > maxDepth)
        {
            yield return node with { DepthLimited = true };
            yield break;
        }

        ContainerContents? contents = null;
        string? error = null;
        opening?.Invoke(node.Location);
        try
        {
            contents = await reader.OpenAsync(node.LocalPath, ct);
        }
        catch (ContainerReadException e)
        {
            error = e.Reason;
        }
        if (contents is null)
        {
            yield return node with { Kind = VfsNodeKind.ContainerError, ContainerKind = reader.Kind, Error = error };
            yield break;
        }

        using (contents)
        {
            yield return node with { Kind = VfsNodeKind.Container, ContainerKind = reader.Kind, Store = contents.Store };
            var ignoredFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in contents.Entries)
            {
                ct.ThrowIfCancellationRequested();
                // Entry paths are relative to the container, so path rules apply from its root.
                if (ignore.IgnoredAncestor(e.Key) is { } ignored)
                {
                    if (ignoredFolders.Add(ignored.Folder))
                        yield return new VfsNode(VfsNodeKind.IgnoredFolder, node.Location.Child(ignored.Folder), "", ignored.Folder, 0, null) { Error = ignored.Rule };
                    continue;
                }
                if (ReaderFor(e.Key) is not null && ignore.Match(e.Key) is { } containerRule)
                {
                    yield return new VfsNode(VfsNodeKind.IgnoredFolder, node.Location.Child(e.Key), e.LocalPath, e.Key, e.Size, e.Modified) { Error = containerRule };
                    continue;
                }
                var child = new VfsNode(VfsNodeKind.File, node.Location.Child(e.Key), e.LocalPath, e.Key, e.Size, e.Modified)
                {
                    UnityGuid = e.UnityGuid,
                    MetaPath = e.MetaPath,
                    PreviewPath = e.PreviewPath,
                };
                await foreach (var n in VisitAsync(child, maxDepth, ignore, opening, ct)) yield return n;
            }
        }
    }
}
