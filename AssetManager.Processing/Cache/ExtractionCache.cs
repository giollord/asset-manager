using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AssetManager.Processing.Cache;

public sealed record CacheLimits(long MaxBytes, TimeSpan MaxAge);

public sealed record EvictionResult(int Removed, long FreedBytes, long RemainingBytes, int SkippedLocked);

/// <summary>
/// App-managed temporary cache for extracted container contents (<c>%LOCALAPPDATA%\AssetManager\cache</c>).
/// Entries are folders named by a content key. A folder is filled in a temp folder and renamed into place,
/// so it is either complete or absent. Entries in use are protected by an in-process reference count and a
/// <c>.lock</c> file (held open, so other processes skip them too). Eviction removes entries older than the
/// age limit, then least-recently-used entries until the cache is under the size limit.
/// The cache is never the only copy of data the catalog depends on.
/// </summary>
public sealed class ExtractionCache
{
    private const string LockFile = ".lock";
    private const string UsedFile = ".used";
    private const string SizeFile = ".size";
    private const string TempMarker = ".tmp-";

    private readonly Func<CacheLimits> _limits;
    private readonly ILogger _log;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _fillLocks = new();
    private readonly Dictionary<string, (int Count, FileStream Lock)> _leases = new();
    private readonly Lock _sync = new();
    private long _lastUsedTicks;

    public ExtractionCache(string root, Func<CacheLimits> limits, ILogger<ExtractionCache>? logger = null)
    {
        Root = root;
        _limits = limits;
        _log = logger ?? (ILogger)NullLogger.Instance;
        Directory.CreateDirectory(root);
    }

    public string Root { get; }

    public string PathOf(string key) => Path.Combine(Root, key);

    /// <summary>Leases a complete entry, or null when it is not cached.</summary>
    public CacheLease? TryOpen(string key)
    {
        lock (_sync)
        {
            if (!Directory.Exists(PathOf(key))) return null;
            return LeaseLocked(key);
        }
    }

    /// <summary>
    /// Leases the entry for <paramref name="key"/>, creating it with <paramref name="fill"/> (which writes into
    /// the folder it is given) when it is not cached. Concurrent callers for the same key fill it once.
    /// </summary>
    public async Task<CacheLease> GetOrCreateAsync(string key, Func<string, CancellationToken, Task> fill, CancellationToken ct = default)
    {
        if (TryOpen(key) is { } hit) return hit;
        var gate = _fillLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (TryOpen(key) is { } raced) return raced;
            var temp = PathOf(key + TempMarker + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            try
            {
                await fill(temp, ct);
                File.WriteAllText(Path.Combine(temp, SizeFile), DirectorySize(temp).ToString());
                Directory.Move(temp, PathOf(key));
            }
            catch
            {
                TryDeleteDirectory(temp);
                throw;
            }
        }
        finally
        {
            gate.Release();
        }
        var lease = TryOpen(key) ?? throw new IOException($"Cache entry '{key}' vanished after it was filled.");
        Evict();
        return lease;
    }

    /// <summary>
    /// Leases an entry that collects individually extracted files (for example single entries of a large zip)
    /// and makes sure <paramref name="relativePath"/> exists in it, writing it with <paramref name="write"/> when missing.
    /// Returns the lease and the file's full path.
    /// </summary>
    public async Task<(CacheLease Lease, string File)> GetOrAddFileAsync(
        string key, string relativePath, Func<string, CancellationToken, Task> write, CancellationToken ct = default)
    {
        CacheLease lease;
        lock (_sync)
        {
            Directory.CreateDirectory(PathOf(key));
            lease = LeaseLocked(key);
        }
        try
        {
            var target = Path.GetFullPath(Path.Combine(lease.Directory, relativePath));
            if (!target.StartsWith(lease.Directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException($"Entry path '{relativePath}' escapes the cache folder.");
            if (!File.Exists(target))
            {
                var gate = _fillLocks.GetOrAdd(key + "|" + relativePath, _ => new SemaphoreSlim(1, 1));
                await gate.WaitAsync(ct);
                try
                {
                    if (!File.Exists(target))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        var temp = target + TempMarker + Guid.NewGuid().ToString("N");
                        try
                        {
                            await write(temp, ct);
                            File.Move(temp, target);
                        }
                        finally
                        {
                            TryDeleteFile(temp);
                        }
                    }
                }
                finally
                {
                    gate.Release();
                }
                File.Delete(Path.Combine(lease.Directory, SizeFile));
            }
            return (lease, target);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    /// <summary>Total size of all entries in bytes.</summary>
    public long CurrentSize() => ListEntries().Sum(e => e.Size);

    /// <summary>Applies the age and size limits; entries in use are never removed.</summary>
    public EvictionResult Evict(DateTime? nowUtc = null)
    {
        var limits = _limits();
        var now = nowUtc ?? DateTime.UtcNow;
        var entries = ListEntries().OrderBy(e => e.LastUsed).ToList();
        var total = entries.Sum(e => e.Size);
        int removed = 0, skipped = 0;
        long freed = 0;
        foreach (var e in entries)
        {
            var expired = now - e.LastUsed > limits.MaxAge;
            if (!expired && total <= limits.MaxBytes) continue;
            if (!TryRemove(e.Path))
            {
                skipped++;
                continue;
            }
            removed++;
            freed += e.Size;
            total -= e.Size;
        }

        // Temp folders left by a crash.
        foreach (var dir in Directory.EnumerateDirectories(Root, "*" + TempMarker + "*"))
            if (now - Directory.GetLastWriteTimeUtc(dir) > TimeSpan.FromDays(1)) TryDeleteDirectory(dir);

        if (removed > 0) _log.LogInformation("Cache eviction removed {Count} entries ({Bytes} bytes); {Remaining} bytes remain", removed, freed, total);
        return new EvictionResult(removed, freed, total, skipped);
    }

    private CacheLease LeaseLocked(string key)
    {
        var dir = PathOf(key);
        if (_leases.TryGetValue(key, out var held))
        {
            _leases[key] = (held.Count + 1, held.Lock);
        }
        else
        {
            var fs = new FileStream(Path.Combine(dir, LockFile), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
            _leases[key] = (1, fs);
        }
        var ticks = Math.Max(DateTime.UtcNow.Ticks, Interlocked.Increment(ref _lastUsedTicks));
        Interlocked.Exchange(ref _lastUsedTicks, ticks);
        try { File.WriteAllText(Path.Combine(dir, UsedFile), ticks.ToString()); }
        catch (IOException) { } // best effort: a concurrent eviction scan may be reading it
        return new CacheLease(this, key, dir);
    }

    internal void Release(string key)
    {
        lock (_sync)
        {
            if (!_leases.TryGetValue(key, out var held)) return;
            if (held.Count > 1)
            {
                _leases[key] = (held.Count - 1, held.Lock);
                return;
            }
            _leases.Remove(key);
            held.Lock.Dispose();
        }
    }

    private bool TryRemove(string dir)
    {
        lock (_sync)
        {
            if (_leases.ContainsKey(Path.GetFileName(dir))) return false;
            FileStream exclusive;
            try
            {
                // Fails while another process holds the lock file open.
                exclusive = new FileStream(Path.Combine(dir, LockFile), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                return false;
            }
            using (exclusive)
            {
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                    if (!string.Equals(Path.GetFileName(f), LockFile, StringComparison.Ordinal) || Path.GetDirectoryName(f) != dir)
                        TryDeleteFile(f);
            }
            return TryDeleteDirectory(dir);
        }
    }

    private IEnumerable<(string Path, long Size, DateTime LastUsed)> ListEntries()
    {
        foreach (var dir in Directory.EnumerateDirectories(Root))
        {
            if (Path.GetFileName(dir).Contains(TempMarker, StringComparison.Ordinal)) continue;
            long size;
            try { size = long.Parse(File.ReadAllText(Path.Combine(dir, SizeFile))); }
            catch (Exception e) when (e is IOException or FormatException or UnauthorizedAccessException) { size = DirectorySize(dir); }
            DateTime used;
            try { used = new DateTime(long.Parse(File.ReadAllText(Path.Combine(dir, UsedFile))), DateTimeKind.Utc); }
            catch (Exception e) when (e is IOException or FormatException or UnauthorizedAccessException or ArgumentOutOfRangeException) { used = Directory.GetLastWriteTimeUtc(dir); }
            yield return (dir, size, used);
        }
    }

    private static long DirectorySize(string dir) =>
        Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);

    private static void TryDeleteFile(string f)
    {
        try { File.Delete(f); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static bool TryDeleteDirectory(string dir)
    {
        try
        {
            Directory.Delete(dir, true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return !Directory.Exists(dir);
        }
    }
}

/// <summary>Keeps a cache entry from being evicted until disposed.</summary>
public sealed class CacheLease : IDisposable
{
    private readonly ExtractionCache _cache;
    private int _disposed;

    internal CacheLease(ExtractionCache cache, string key, string directory)
    {
        _cache = cache;
        Key = key;
        Directory = directory;
    }

    public string Key { get; }
    public string Directory { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _cache.Release(Key);
    }
}
