using System.Collections;
using System.IO.Compression;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;

namespace AssetManager.Core.Platform;

/// <summary>
/// Serves the files of a zip archive, read into memory once (design D2). The portable package ships the app's web
/// content as one <c>wwwroot.zip</c>, so a start from a slow drive reads one file sequentially instead of dozens.
/// Paths are relative to the archive root and matched case-insensitively, like the Windows file system.
/// </summary>
public sealed class ZipFileProvider : IFileProvider
{
    private readonly Dictionary<string, ZipEntryFile> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _directories = new(StringComparer.OrdinalIgnoreCase) { "" };

    public ZipFileProvider(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var entry in zip.Entries)
        {
            var path = Normalize(entry.FullName);
            if (path.Length == 0 || entry.FullName.EndsWith('/')) { _directories.Add(path); continue; }
            using var stream = entry.Open();
            using var buffer = new MemoryStream((int)entry.Length);
            stream.CopyTo(buffer);
            _files[path] = new ZipEntryFile(Path.GetFileName(path), buffer.ToArray(), entry.LastWriteTime);
            for (var dir = Parent(path); dir.Length > 0; dir = Parent(dir)) _directories.Add(dir);
        }
    }

    public IFileInfo GetFileInfo(string subpath) =>
        _files.TryGetValue(Normalize(subpath), out var file) ? file : new NotFoundFileInfo(subpath);

    public IDirectoryContents GetDirectoryContents(string subpath)
    {
        var dir = Normalize(subpath);
        if (!_directories.Contains(dir)) return NotFoundDirectoryContents.Singleton;
        var prefix = dir.Length == 0 ? "" : dir + "/";
        var files = _files.Where(f => f.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && f.Key.IndexOf('/', prefix.Length) < 0).Select(f => (IFileInfo)f.Value);
        var dirs = _directories.Where(d => d.Length > prefix.Length && d.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && d.IndexOf('/', prefix.Length) < 0)
            .Select(d => (IFileInfo)new ZipDirectoryInfo(Path.GetFileName(d)));
        return new Contents([.. dirs, .. files]);
    }

    public IChangeToken Watch(string filter) => NullChangeToken.Singleton;

    private static string Normalize(string path) => path.Replace('\\', '/').Trim('/');

    private static string Parent(string path) => path.LastIndexOf('/') is var i and > 0 ? path[..i] : "";

    private sealed class ZipEntryFile(string name, byte[] bytes, DateTimeOffset modified) : IFileInfo
    {
        public bool Exists => true;
        public long Length => bytes.Length;
        public string? PhysicalPath => null;
        public string Name => name;
        public DateTimeOffset LastModified => modified;
        public bool IsDirectory => false;
        public Stream CreateReadStream() => new MemoryStream(bytes, writable: false);
    }

    private sealed class ZipDirectoryInfo(string name) : IFileInfo
    {
        public bool Exists => true;
        public long Length => -1;
        public string? PhysicalPath => null;
        public string Name => name;
        public DateTimeOffset LastModified => default;
        public bool IsDirectory => true;
        public Stream CreateReadStream() => throw new InvalidOperationException("Cannot read a directory.");
    }

    private sealed class Contents(IReadOnlyList<IFileInfo> items) : IDirectoryContents
    {
        public bool Exists => true;
        public IEnumerator<IFileInfo> GetEnumerator() => items.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
