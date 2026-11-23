using System.Text.Json;
using AssetManager.Core.Domain;
using AssetManager.Processing.Cache;
using SharpCompress.Readers;

namespace AssetManager.Processing.Containers;

/// <summary>
/// Reads <c>.unitypackage</c> files (gzip-compressed tar, one folder per GUID with <c>asset</c>, <c>asset.meta</c>,
/// <c>pathname</c> and an optional <c>preview.png</c>). The package is extracted once to the cache and rearranged
/// into its original project layout (<c>files/Assets/...</c>, with each <c>.meta</c> next to its asset), so every
/// entry has its real name and extension. Asset Store metadata is read from the gzip FEXTRA field.
/// </summary>
public sealed class UnityPackageReader(ExtractionCache cache) : IContainerReader
{
    private const string FilesFolder = "files";
    private const string PreviewFolder = "previews";
    private const string IndexFile = "index.json";

    public SourceKind Kind => SourceKind.UnityPackage;

    public bool CanRead(string fileName) => Path.GetExtension(fileName).Equals(".unitypackage", StringComparison.OrdinalIgnoreCase);

    public async Task<ContainerContents> OpenAsync(string localPath, CancellationToken ct = default)
    {
        var key = "u-" + ArchiveIo.ContentKey(localPath);
        var lease = await cache.GetOrCreateAsync(key, (dir, c) => Task.Run(() => Extract(localPath, dir, c), c), ct);
        try
        {
            var index = JsonSerializer.Deserialize<List<IndexEntry>>(File.ReadAllText(Path.Combine(lease.Directory, IndexFile))) ?? [];
            var entries = index.Select(e =>
            {
                var file = EntryPaths.ToLocal(Path.Combine(lease.Directory, FilesFolder), e.Path);
                var info = new FileInfo(file);
                var meta = file + ".meta";
                var preview = Path.Combine(lease.Directory, PreviewFolder, e.Guid + ".png");
                return new ContainerEntry(e.Path, file, info.Length, info.LastWriteTimeUtc)
                {
                    UnityGuid = e.Guid,
                    MetaPath = File.Exists(meta) ? meta : null,
                    PreviewPath = File.Exists(preview) ? preview : null,
                };
            }).ToList();
            StoreInfo? store;
            try { store = ReadStoreHeader(localPath); }
            catch (Exception e) when (e is IOException or JsonException) { store = null; }
            return new ContainerContents(entries, lease, store);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    public async Task<ResolvedFile> ExtractEntryAsync(string localPath, string key, CancellationToken ct = default)
    {
        var contents = await OpenAsync(localPath, ct);
        var hit = contents.Entries.FirstOrDefault(e => e.Key == key);
        if (hit is null)
        {
            contents.Dispose();
            throw new FileNotFoundException($"'{key}' was not found in the Unity package.", key);
        }
        return new ResolvedFile(hit.LocalPath, ResolvedFrom.Container, contents);
    }

    /// <summary>
    /// Extracts the tar inside a gzip package and checks that the gzip stream was complete: a package cut short (an
    /// interrupted copy or download) can otherwise read without error, with the last entries silently missing. The
    /// gzip trailer stores the uncompressed size (mod 2^32), which must match what was decompressed.
    /// </summary>
    private static void ExtractCheckedGzipTar(string path, string dest, CancellationToken ct)
    {
        using var file = File.OpenRead(path);
        using var gzip = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionMode.Decompress, leaveOpen: true);
        using var counted = new CountingStream(gzip);
        using (var tar = new System.Formats.Tar.TarReader(counted, leaveOpen: true))
        {
            while (tar.GetNextEntry() is { } entry)
            {
                ct.ThrowIfCancellationRequested();
                if (entry.EntryType is not (System.Formats.Tar.TarEntryType.RegularFile or System.Formats.Tar.TarEntryType.V7RegularFile)
                    || entry.DataStream is null) continue;
                if (EntryPaths.Normalize(entry.Name) is not { } key) continue;
                var target = EntryPaths.ToLocal(dest, key);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using (var output = File.Create(target)) entry.DataStream.CopyTo(output);
                if (entry.ModificationTime.Year > 1980) File.SetLastWriteTimeUtc(target, entry.ModificationTime.UtcDateTime);
            }
        }
        counted.CopyTo(Stream.Null); // the tar end blocks and padding
        Span<byte> trailer = stackalloc byte[4];
        file.Seek(-4, SeekOrigin.End);
        file.ReadExactly(trailer);
        if (System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(trailer) != (uint)counted.Count)
            throw new ContainerReadException("Corrupt or unsupported Unity package: the file is incomplete (truncated).");
    }

    /// <summary>Counts the bytes read through a read-only stream.</summary>
    private sealed class CountingStream(Stream inner) : Stream
    {
        public long Count { get; private set; }
        public override int Read(byte[] buffer, int offset, int count) => Add(inner.Read(buffer, offset, count));
        public override int Read(Span<byte> buffer) => Add(inner.Read(buffer));
        private int Add(int n) { Count += n; return n; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => Count; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static void Extract(string path, string dir, CancellationToken ct)
    {
        var raw = Path.Combine(dir, "raw");
        try
        {
            if (ArchiveIo.IsGzip(path)) ExtractCheckedGzipTar(path, raw, ct);
            else
                using (var reader = ReaderFactory.OpenReader(path, new ReaderOptions()))
                    ArchiveIo.ExtractReader(reader, raw, null, ct);
        }
        catch (Exception e) when (e is not (ContainerReadException or OperationCanceledException))
        {
            throw ArchiveIo.Wrap(e, "Unity package");
        }

        var files = Path.Combine(dir, FilesFolder);
        var previews = Path.Combine(dir, PreviewFolder);
        Directory.CreateDirectory(files);
        Directory.CreateDirectory(previews);
        var index = new List<IndexEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var guidDir in Directory.Exists(raw) ? Directory.EnumerateDirectories(raw) : [])
        {
            ct.ThrowIfCancellationRequested();
            var guid = Path.GetFileName(guidDir);
            var pathnameFile = Path.Combine(guidDir, "pathname");
            var asset = Path.Combine(guidDir, "asset");
            if (!File.Exists(pathnameFile) || !File.Exists(asset)) continue; // folders have no asset
            var firstLine = File.ReadLines(pathnameFile).FirstOrDefault();
            var rel = EntryPaths.Normalize(firstLine);
            if (rel is null || !seen.Add(rel)) continue;

            var target = EntryPaths.ToLocal(files, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(asset, target);
            var meta = Path.Combine(guidDir, "asset.meta");
            if (File.Exists(meta)) File.Move(meta, target + ".meta");
            var preview = Path.Combine(guidDir, "preview.png");
            if (File.Exists(preview)) File.Move(preview, Path.Combine(previews, guid + ".png"));
            index.Add(new IndexEntry(guid, rel));
        }
        index.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Path, b.Path));
        File.WriteAllText(Path.Combine(dir, IndexFile), JsonSerializer.Serialize(index));
        Directory.Delete(raw, true);
    }

    /// <summary>Reads the Asset Store JSON from the gzip FEXTRA field ("A$" subfield), or null when absent.</summary>
    public static StoreInfo? ReadStoreHeader(string path)
    {
        using var fs = File.OpenRead(path);
        Span<byte> head = stackalloc byte[12];
        if (fs.ReadAtLeast(head, 12, throwOnEndOfStream: false) < 12) return null;
        if (head[0] != 0x1f || head[1] != 0x8b || (head[3] & 0x04) == 0) return null;
        var xlen = head[10] | (head[11] << 8);
        var extra = new byte[xlen];
        if (fs.ReadAtLeast(extra, xlen, throwOnEndOfStream: false) < xlen) return null;
        for (var i = 0; i + 4 <= xlen;)
        {
            var len = extra[i + 2] | (extra[i + 3] << 8);
            if (i + 4 + len > xlen) break;
            if (extra[i] == 'A' && extra[i + 1] == '$') return ParseStoreJson(extra.AsSpan(i + 4, len));
            i += 4 + len;
        }
        return null;
    }

    private static StoreInfo? ParseStoreJson(ReadOnlySpan<byte> json)
    {
        using var doc = JsonDocument.Parse(json.ToArray());
        var r = doc.RootElement;
        if (r.ValueKind != JsonValueKind.Object) return null;
        var id = Text(r, "id") ?? (r.TryGetProperty("link", out var link) ? Text(link, "id") : null);
        string? publisher = null;
        if (r.TryGetProperty("publisher", out var p))
            publisher = p.ValueKind == JsonValueKind.Object ? Text(p, "label") : p.ValueKind == JsonValueKind.String ? p.GetString() : null;
        return new StoreInfo
        {
            StoreId = id,
            Title = Text(r, "title"),
            Publisher = publisher,
            Version = Text(r, "version"),
            Url = id is null ? null : $"https://assetstore.unity.com/packages/slug/{id}",
        };
    }

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Number => v.GetRawText(),
                _ => null,
            }
            : null;

    private sealed record IndexEntry(string Guid, string Path);
}
