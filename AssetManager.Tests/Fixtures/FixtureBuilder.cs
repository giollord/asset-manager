using System.Formats.Tar;
using System.IO.Compression;
using System.IO.Hashing;
using System.Text;
using System.Text.Json;

namespace AssetManager.Tests.Fixtures;

/// <summary>A file to place into a generated fixture container.</summary>
public sealed record FixtureFile(string Path, byte[] Content)
{
    public static FixtureFile Text(string path, string text) => new(path, Encoding.UTF8.GetBytes(text));
}

/// <summary>An entry of a generated <c>.unitypackage</c>.</summary>
public sealed record UnityPackageEntry(string Guid, string PathName, byte[]? Asset, string? Meta = null, byte[]? Preview = null);

/// <summary>Asset Store header metadata written to the gzip FEXTRA field of a generated package.</summary>
public sealed record AssetStoreHeader(string Id, string Title, string Publisher, string Version);

/// <summary>Builds small container fixtures (zip, 7z, tar, tar.gz, gz, unitypackage) in code.</summary>
public static class FixtureBuilder
{
    // Pre-built 7z archive (SharpCompress cannot write 7z) holding readme.txt ("hello 7z") and tex.png ("PNGDATA").
    private const string SevenZipBase64 =
        "N3q8ryccAATi1ZubewAAAAAAAAAgAAAAAAAAAOaTmW8BAA5oZWxsbyA3elBOR0RBVEEAAACBMweuD88n8IwHyEN/QbH6/eEttTvWR66qgBwEcAmQns3KaA8qW4ZRq8k3aTDZmWfoMTefbaqQ5Hk361u28RjCOZde3WSLXio+giO6UcBgxnVXMVoPE0mcmInMfbL7usvo7L/G97UXBhMBCWgABwsBAAEjAwEBBV0AEAAADH4KAeqnoVwAAA==";

    public static IReadOnlyList<FixtureFile> SevenZipContents { get; } =
    [
        FixtureFile.Text("readme.txt", "hello 7z"),
        FixtureFile.Text("tex.png", "PNGDATA"),
    ];

    public static byte[] Zip(params FixtureFile[] files)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var f in files)
            {
                using var s = zip.CreateEntry(f.Path).Open();
                s.Write(f.Content);
            }
        }
        return ms.ToArray();
    }

    public static byte[] SevenZip() => Convert.FromBase64String(SevenZipBase64);

    public static byte[] Tar(params FixtureFile[] files)
    {
        using var ms = new MemoryStream();
        using (var tar = new TarWriter(ms, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var f in files)
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, f.Path) { DataStream = new MemoryStream(f.Content) });
        }
        return ms.ToArray();
    }

    public static byte[] TarGz(params FixtureFile[] files) => Gzip(Tar(files));

    /// <summary>Writes a gzip member by hand so an optional FEXTRA field can be included.</summary>
    public static byte[] Gzip(byte[] content, byte[]? extraField = null)
    {
        using var ms = new MemoryStream();
        byte flags = extraField is null ? (byte)0 : (byte)0x04;
        ms.Write([0x1f, 0x8b, 0x08, flags, 0, 0, 0, 0, 0, 0x0b]);
        if (extraField is not null)
        {
            ms.Write(BitConverter.GetBytes((ushort)extraField.Length));
            ms.Write(extraField);
        }
        using (var deflate = new DeflateStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            deflate.Write(content);
        ms.Write(BitConverter.GetBytes(Crc32.HashToUInt32(content)));
        ms.Write(BitConverter.GetBytes((uint)content.Length));
        return ms.ToArray();
    }

    /// <summary>Builds a Unity package: a tar.gz with one folder per GUID holding asset, asset.meta, pathname and preview.png.</summary>
    public static byte[] UnityPackage(IEnumerable<UnityPackageEntry> entries, AssetStoreHeader? header = null)
    {
        var files = new List<FixtureFile>();
        foreach (var e in entries)
        {
            files.Add(FixtureFile.Text($"{e.Guid}/pathname", e.PathName));
            if (e.Asset is not null) files.Add(new($"{e.Guid}/asset", e.Asset));
            files.Add(FixtureFile.Text($"{e.Guid}/asset.meta", e.Meta ?? $"fileFormatVersion: 2\nguid: {e.Guid}\n"));
            if (e.Preview is not null) files.Add(new($"{e.Guid}/preview.png", e.Preview));
        }
        return Gzip(Tar([.. files]), header is null ? null : AssetStoreExtra(header));
    }

    // Same layout as real Asset Store downloads: subfield id "A$", 2-byte length, JSON payload.
    private static byte[] AssetStoreExtra(AssetStoreHeader h)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(new
        {
            link = new { id = h.Id, type = "content" },
            version = h.Version,
            id = h.Id,
            title = h.Title,
            publisher = new { id = "1", label = h.Publisher },
        });
        var sub = new byte[4 + json.Length];
        sub[0] = (byte)'A';
        sub[1] = (byte)'$';
        BitConverter.GetBytes((ushort)json.Length).CopyTo(sub, 2);
        json.CopyTo(sub, 4);
        return sub;
    }

    /// <summary>Deterministic 32-hex Unity-style GUID derived from a seed string.</summary>
    public static string Guid(string seed) => Convert.ToHexStringLower(XxHash128.Hash(Encoding.UTF8.GetBytes(seed)));

    /// <summary>Creates a fresh temp directory that is deleted on dispose.</summary>
    public static TempDir TempDir() => new();
}

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "am-tests", System.Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public string Write(string relative, byte[] content)
    {
        var full = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
        return full;
    }

    public string Write(string relative, string text) => Write(relative, Encoding.UTF8.GetBytes(text));

    public void Dispose()
    {
        try { Directory.Delete(Path, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
