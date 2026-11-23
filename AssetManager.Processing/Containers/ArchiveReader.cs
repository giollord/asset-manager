using AssetManager.Core.Domain;
using AssetManager.Processing.Cache;
using AssetManager.Processing.Files;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace AssetManager.Processing.Containers;

/// <summary>Zip, rar, 7z, tar, tar.gz and gz archives via SharpCompress.</summary>
public sealed class ArchiveReader(ExtractionCache cache) : IContainerReader
{
    private const string ContentFolder = "x";
    private static readonly string[] Extensions = [".zip", ".rar", ".7z", ".tar", ".gz", ".tgz"];

    public SourceKind Kind => SourceKind.Archive;

    public bool CanRead(string fileName) => Extensions.Contains(Path.GetExtension(fileName), StringComparer.OrdinalIgnoreCase);

    public async Task<ContainerContents> OpenAsync(string localPath, CancellationToken ct = default)
    {
        var key = "a-" + ArchiveIo.ContentKey(localPath);
        var lease = await cache.GetOrCreateAsync(key,
            (dir, c) => Task.Run(() => ExtractAll(localPath, Path.Combine(dir, ContentFolder), c), c), ct);
        try
        {
            var root = Path.Combine(lease.Directory, ContentFolder);
            var entries = Directory.Exists(root)
                ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                    .Select(f => new FileInfo(f))
                    .Select(f => new ContainerEntry(Path.GetRelativePath(root, f.FullName).Replace('\\', '/'), f.FullName, f.Length, f.LastWriteTimeUtc))
                    .OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase)
                    .ToList()
                : [];
            return new ContainerContents(entries, lease);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    public async Task<ResolvedFile> ExtractEntryAsync(string localPath, string key, CancellationToken ct = default)
    {
        var contentKey = ArchiveIo.ContentKey(localPath);
        if (cache.TryOpen("a-" + contentKey) is { } full)
        {
            var path = EntryPaths.ToLocal(Path.Combine(full.Directory, ContentFolder), key);
            if (File.Exists(path)) return new ResolvedFile(path, ResolvedFrom.Container, full);
            full.Dispose();
            throw new FileNotFoundException($"'{key}' was not found in the archive.", key);
        }

        if (!ArchiveIo.IsGzip(localPath))
        {
            // Random-access formats: extract just this entry.
            IArchive archive;
            try { archive = ArchiveFactory.OpenArchive(localPath, new ReaderOptions()); }
            catch (Exception e) when (e is not OperationCanceledException) { throw ArchiveIo.Wrap(e, "archive"); }
            using (archive)
            {
                if (archive.Type is ArchiveType.Zip or ArchiveType.Tar || (archive.Type == ArchiveType.SevenZip && !archive.IsSolid))
                {
                    var entry = archive.Entries.FirstOrDefault(e => !e.IsDirectory && EntryPaths.Normalize(e.Key) == key)
                        ?? throw new FileNotFoundException($"'{key}' was not found in the archive.", key);
                    if (entry.IsEncrypted) throw new ContainerReadException("Password-protected archive.");
                    try
                    {
                        var (lease, file) = await cache.GetOrAddFileAsync("e-" + contentKey, key, (temp, c) =>
                        {
                            using var fs = File.Create(temp);
                            entry.WriteTo(fs);
                            return Task.CompletedTask;
                        }, ct);
                        return new ResolvedFile(file, ResolvedFrom.Container, lease);
                    }
                    catch (Exception e) when (e is not (OperationCanceledException or ContainerReadException or IOException))
                    {
                        throw ArchiveIo.Wrap(e, "archive");
                    }
                }
            }
        }

        // Sequential or solid formats: extract once, then serve from the full extraction.
        var contents = await OpenAsync(localPath, ct);
        var hit = contents.Entries.FirstOrDefault(e => e.Key == key);
        if (hit is null)
        {
            contents.Dispose();
            throw new FileNotFoundException($"'{key}' was not found in the archive.", key);
        }
        return new ResolvedFile(hit.LocalPath, ResolvedFrom.Container, contents);
    }

    private void ExtractAll(string path, string dest, CancellationToken ct)
    {
        Directory.CreateDirectory(dest);
        try
        {
            if (ArchiveIo.IsGzip(path))
            {
                // tar.gz streams through the tar reader; a plain .gz yields its single member.
                using var reader = ReaderFactory.OpenReader(path, new ReaderOptions());
                var fallback = Path.GetFileNameWithoutExtension(path);
                if (fallback.Length == 0 || Path.GetExtension(path).Equals(".tgz", StringComparison.OrdinalIgnoreCase)) fallback += ".tar";
                ArchiveIo.ExtractReader(reader, dest, fallback, ct);
            }
            else
            {
                using var archive = ArchiveFactory.OpenArchive(path, new ReaderOptions());
                if (archive.IsEncrypted || archive.Entries.Any(e => e.IsEncrypted))
                    throw new ContainerReadException("Password-protected archive.");
                ArchiveIo.EnsureSpace(cache.Root, archive.TotalUncompressedSize);
                if (archive.IsSolid || archive.Type == ArchiveType.SevenZip)
                {
                    // Solid archives decompress efficiently only in order.
                    using var reader = archive.ExtractAllEntries();
                    ArchiveIo.ExtractReader(reader, dest, null, ct);
                }
                else
                {
                    foreach (var entry in archive.Entries)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (entry.IsDirectory || EntryPaths.Normalize(entry.Key) is not { } key) continue;
                        var target = EntryPaths.ToLocal(dest, key);
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        using (var fs = File.Create(target)) entry.WriteTo(fs);
                        if (entry.LastModifiedTime is { } t && t.Year > 1980) File.SetLastWriteTime(target, t);
                    }
                }
            }
        }
        catch (Exception e) when (e is not (ContainerReadException or OperationCanceledException))
        {
            throw ArchiveIo.Wrap(e, "archive");
        }
    }
}

internal static class ArchiveIo
{
    public static string ContentKey(string path)
    {
        try { return Fingerprint.ContentKey(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new ContainerReadException($"Cannot open file: {e.Message}", e); }
    }

    public static bool IsGzip(string path)
    {
        using var fs = File.OpenRead(path);
        return fs.ReadByte() == 0x1f && fs.ReadByte() == 0x8b;
    }

    public static void EnsureSpace(string folder, long needed)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(folder));
        if (string.IsNullOrEmpty(root)) return;
        var free = new DriveInfo(root).AvailableFreeSpace;
        if (needed > 0 && needed + needed / 10 > free)
            throw new ContainerReadException($"Not enough free disk space to extract ({needed / 1048576} MB needed, {free / 1048576} MB free).");
    }

    /// <summary>Writes every file entry of a sequential reader below <paramref name="dest"/>.</summary>
    public static void ExtractReader(IReader reader, string dest, string? fallbackName, CancellationToken ct)
    {
        while (reader.MoveToNextEntry())
        {
            ct.ThrowIfCancellationRequested();
            var e = reader.Entry;
            if (e.IsDirectory) continue;
            if (e.IsEncrypted) throw new ContainerReadException("Password-protected archive.");
            var key = EntryPaths.Normalize(e.Key) ?? (string.IsNullOrWhiteSpace(e.Key) ? fallbackName : null);
            if (key is null) continue;
            var target = EntryPaths.ToLocal(dest, key);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using (var fs = File.Create(target)) reader.WriteEntryTo(fs);
            if (e.LastModifiedTime is { } t && t.Year > 1980) File.SetLastWriteTime(target, t);
        }
    }

    public static ContainerReadException Wrap(Exception e, string what)
    {
        var msg = e.Message;
        if (msg.Contains("password", StringComparison.OrdinalIgnoreCase) || msg.Contains("encrypt", StringComparison.OrdinalIgnoreCase))
            return new ContainerReadException("Password-protected archive.", e);
        return new ContainerReadException($"Corrupt or unsupported {what}: {msg}", e);
    }
}
