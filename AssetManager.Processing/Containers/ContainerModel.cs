using AssetManager.Core.Domain;
using AssetManager.Processing.Cache;

namespace AssetManager.Processing.Containers;

/// <summary>A container (archive, package) could not be read; reported per source, the import continues.</summary>
public sealed class ContainerReadException(string reason, Exception? inner = null) : Exception(reason, inner)
{
    public string Reason { get; } = reason;
}

/// <summary>One file inside an opened container, extracted to <see cref="LocalPath"/> in the cache.</summary>
public sealed record ContainerEntry(string Key, string LocalPath, long Size, DateTime? Modified)
{
    /// <summary>Unity GUID (unitypackage entries).</summary>
    public string? UnityGuid { get; init; }

    /// <summary>Extracted <c>.meta</c> file (unitypackage entries).</summary>
    public string? MetaPath { get; init; }

    /// <summary>Extracted embedded <c>preview.png</c> (unitypackage entries).</summary>
    public string? PreviewPath { get; init; }
}

/// <summary>The extracted contents of a container. Entry paths stay valid until this is disposed.</summary>
public sealed class ContainerContents(IReadOnlyList<ContainerEntry> entries, CacheLease lease, StoreInfo? store = null) : IDisposable
{
    public IReadOnlyList<ContainerEntry> Entries { get; } = entries;

    /// <summary>Asset Store metadata found in the container header, if any.</summary>
    public StoreInfo? Store { get; } = store;

    internal CacheLease Lease { get; } = lease;

    public void Dispose() => Lease.Dispose();
}

/// <summary>Reads one kind of container file.</summary>
public interface IContainerReader
{
    SourceKind Kind { get; }

    /// <summary>Whether this reader handles a file with this name (by extension).</summary>
    bool CanRead(string fileName);

    /// <summary>Extracts the whole container into the cache (or reuses a cached extraction) and lists its files.</summary>
    /// <exception cref="ContainerReadException">The container is corrupt, encrypted or unsupported.</exception>
    Task<ContainerContents> OpenAsync(string localPath, CancellationToken ct = default);

    /// <summary>Makes one entry available as a local file, extracting only that entry when the format allows it.</summary>
    /// <exception cref="ContainerReadException">The container cannot be read.</exception>
    /// <exception cref="FileNotFoundException">The entry does not exist in the container.</exception>
    Task<ResolvedFile> ExtractEntryAsync(string localPath, string key, CancellationToken ct = default);
}

/// <summary>Normalizes entry paths from containers ('/' separators, no rooted or parent segments).</summary>
public static class EntryPaths
{
    /// <summary>Returns the normalized path, or null when the entry is unsafe (for example <c>../x</c>) or empty.</summary>
    public static string? Normalize(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        var parts = new List<string>();
        foreach (var raw in key.Replace('\\', '/').Split('/'))
        {
            var p = raw.Trim();
            if (p.Length == 0 || p == ".") continue;
            if (p == ".." || p.Contains(':')) return null;
            parts.Add(p);
        }
        return parts.Count == 0 ? null : string.Join('/', parts);
    }

    /// <summary>Full local path of a normalized entry under <paramref name="root"/>.</summary>
    public static string ToLocal(string root, string key) => Path.Combine(root, key.Replace('/', Path.DirectorySeparatorChar));
}
