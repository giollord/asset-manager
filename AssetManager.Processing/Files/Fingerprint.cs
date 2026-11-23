using System.IO.Hashing;

namespace AssetManager.Processing.Files;

/// <summary>Cheap file identity: size plus an XxHash3 of the first and last 64 KB (no full read).</summary>
public static class Fingerprint
{
    private const int Sample = 64 * 1024;

    /// <summary>Content-based key (size + head/tail hash); independent of path and modification time.</summary>
    public static string ContentKey(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, Sample);
        return ContentKey(fs);
    }

    public static string ContentKey(Stream seekable)
    {
        var length = seekable.Length;
        var hash = new XxHash3();
        var buffer = new byte[Sample];
        seekable.Position = 0;
        hash.Append(buffer.AsSpan(0, seekable.ReadAtLeast(buffer, Sample, throwOnEndOfStream: false)));
        if (length > Sample)
        {
            seekable.Position = Math.Max(Sample, length - Sample);
            hash.Append(buffer.AsSpan(0, seekable.ReadAtLeast(buffer, Sample, throwOnEndOfStream: false)));
        }
        return $"{length:x}-{hash.GetCurrentHashAsUInt64():x16}";
    }

    /// <summary>Full-content XxHash3 (reads the whole file), to confirm a duplicate found by <see cref="ContentKey(string)"/>.</summary>
    public static async Task<string> FullHashAsync(string path, CancellationToken ct = default)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20, useAsync: true);
        var hash = new XxHash3();
        await hash.AppendAsync(fs, ct);
        return hash.GetCurrentHashAsUInt64().ToString("x16");
    }

    /// <summary>Source fingerprint: content key plus modification time, used to detect changed sources.</summary>
    public static string Compute(string path) =>
        $"{ContentKey(path)}-{File.GetLastWriteTimeUtc(path).Ticks:x}";
}
