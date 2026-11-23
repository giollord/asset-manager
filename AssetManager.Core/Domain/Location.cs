using System.Text;

namespace AssetManager.Core.Domain;

/// <summary>
/// Where a file lives: a volume (by serial, not drive letter), a path on that volume, and a chain of
/// entry paths inside nested containers (archive → unitypackage → entry …).
/// Serialized as one string: <c>{volumeId}|{path}|{inner1}|{inner2}…</c>; '|' and '%' inside parts are escaped.
/// </summary>
public sealed record Location
{
    public Location(string? volumeId, string path, IReadOnlyList<string>? inner = null)
    {
        VolumeId = string.IsNullOrEmpty(volumeId) ? null : volumeId;
        Path = path;
        Inner = inner ?? [];
    }

    /// <summary>Volume serial (hex), or null for paths not tied to a known volume (then <see cref="Path"/> is absolute).</summary>
    public string? VolumeId { get; }

    /// <summary>Path relative to the volume root when <see cref="VolumeId"/> is set, else an absolute path.</summary>
    public string Path { get; }

    /// <summary>Entry paths inside nested containers, outermost first.</summary>
    public IReadOnlyList<string> Inner { get; }

    public bool IsInContainer => Inner.Count > 0;

    /// <summary>The file name of the innermost item.</summary>
    public string FileName => System.IO.Path.GetFileName((IsInContainer ? Inner[^1] : Path).Replace('/', '\\'));

    /// <summary>Location of the item one level deeper inside this one (this location must be a container).</summary>
    public Location Child(string entryPath) => new(VolumeId, Path, [.. Inner, entryPath]);

    /// <summary>The container holding this item (one level up), or null for a plain file.</summary>
    public Location? Container => IsInContainer ? new(VolumeId, Path, Inner.Take(Inner.Count - 1).ToList()) : null;

    /// <summary>The outermost on-disk file.</summary>
    public Location OuterFile => IsInContainer ? new(VolumeId, Path) : this;

    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.Append(Escape(VolumeId ?? "")).Append('|').Append(Escape(Path));
        foreach (var i in Inner) sb.Append('|').Append(Escape(i));
        return sb.ToString();
    }

    public static Location Parse(string s)
    {
        var parts = s.Split('|');
        if (parts.Length < 2) throw new FormatException($"Invalid location '{s}'.");
        return new(Unescape(parts[0]), Unescape(parts[1]), parts.Skip(2).Select(Unescape).ToList());
    }

    /// <summary>Human-readable chain, e.g. <c>E:\Packs\a.zip → b.unitypackage → Assets/x.png</c>.</summary>
    public string Display(string? root = null)
    {
        var outer = VolumeId is null ? Path : System.IO.Path.Combine(root ?? $"[{VolumeId}]\\", Path);
        return string.Join(" → ", new[] { outer }.Concat(Inner));
    }

    public bool Equals(Location? other) => other is not null && ToString() == other.ToString();

    public override int GetHashCode() => ToString().GetHashCode();

    private static string Escape(string s) => s.Replace("%", "%25").Replace("|", "%7C");

    private static string Unescape(string s) => s.Replace("%7C", "|").Replace("%25", "%");
}
