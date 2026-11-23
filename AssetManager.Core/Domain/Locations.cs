namespace AssetManager.Core.Domain;

/// <summary>Where a resolved file came from.</summary>
public enum ResolvedFrom { StoredCopy, Live, Container }

/// <summary>
/// A local, readable path for a catalogued file. Dispose it when done: paths inside the extraction cache
/// are protected from eviction only while the lease is held.
/// </summary>
public sealed class ResolvedFile(string path, ResolvedFrom from, IDisposable? lease = null) : IDisposable
{
    public string Path { get; } = path;
    public ResolvedFrom From { get; } = from;

    public void Dispose() => lease?.Dispose();
}

/// <summary>
/// Thrown when an action needs an original that is not reachable. <see cref="Missing"/> names the missing
/// drive or path, e.g. <c>drive "Backup" (last seen as E:\)</c>.
/// </summary>
public sealed class OriginalUnavailableException(string missing)
    : Exception($"Original unavailable: {missing} is missing.")
{
    public string Missing { get; } = missing;
}

/// <summary>
/// Turns a catalog <see cref="Location"/> into a readable local file. Order: stored copy in the catalog →
/// live path on its (possibly re-lettered) volume → extraction from the containing archive/package.
/// </summary>
public interface ILocationResolver
{
    /// <summary>Absolute path of the outermost on-disk file if its volume is mounted, else null. Does not touch the disk.</summary>
    string? GetOuterPath(Location location);

    /// <summary>Describes the drive or path a location needs, for "unavailable" messages.</summary>
    Task<string> DescribeMissingAsync(Location location, CancellationToken ct = default);

    /// <summary>Resolves a catalogued file; uses its stored copy first when one exists.</summary>
    /// <exception cref="OriginalUnavailableException">Neither a stored copy nor the original is reachable.</exception>
    Task<ResolvedFile> ResolveAsync(AssetFile file, CancellationToken ct = default);

    /// <summary>Resolves a location without considering stored copies.</summary>
    /// <exception cref="OriginalUnavailableException">The original is not reachable.</exception>
    Task<ResolvedFile> ResolveAsync(Location location, CancellationToken ct = default);
}
