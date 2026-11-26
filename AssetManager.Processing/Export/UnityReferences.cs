using System.Text.RegularExpressions;
using AssetManager.Core.Domain;
using AssetManager.Data;
using AssetManager.Processing.Containers;
using AssetManager.Processing.Handlers;
using Microsoft.EntityFrameworkCore;

namespace AssetManager.Processing.Export;

/// <summary>A file an exported asset references by GUID, read from the asset's source (package or folder).</summary>
/// <param name="ReferencedBy">The exported asset whose files led to this one.</param>
/// <param name="RelativePath">Path within the source (Unity: <c>Assets/...</c>).</param>
/// <param name="LocalPath">Readable local copy (valid while the <see cref="UnityDependencies"/> is not disposed).</param>
public sealed record DependencyFile(long ReferencedBy, string Guid, string RelativePath, string LocalPath);

/// <summary>The referenced files of an export, with the extraction leases that keep their local copies alive.</summary>
public sealed class UnityDependencies(IReadOnlyList<DependencyFile> files, IReadOnlyList<string> problems, IReadOnlyList<IDisposable> leases) : IDisposable
{
    public static readonly UnityDependencies None = new([], [], []);

    public IReadOnlyList<DependencyFile> Files { get; } = files;

    /// <summary>Sources whose references could not be read (e.g. the package's drive is not attached).</summary>
    public IReadOnlyList<string> Problems { get; } = problems;

    public void Dispose()
    {
        foreach (var l in leases) l.Dispose();
    }
}

/// <summary>
/// "Include referenced files" for a Unity project export (design D8): maps every GUID of an asset's source (package
/// entries, or <c>.meta</c> files in a folder or archive) to its file, scans the Unity YAML of the exported files and
/// of each file found (and every <c>.meta</c>, which holds a model's remapped materials) for <c>guid: …</c>, and closes
/// the set transitively. GUIDs that are not in the source, such as Unity's built-in resources, are ignored.
/// </summary>
public sealed partial class UnityReferences(CatalogStore catalog, ILocationResolver resolver, IEnumerable<IContainerReader> readers)
{
    /// <summary>Unity YAML files that can reference other files.</summary>
    public static readonly IReadOnlySet<string> Scanned = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".mat", ".prefab", ".asset", ".controller", ".overridecontroller", ".anim", ".unity", ".mask", ".playable" };

    [GeneratedRegex(@"guid:\s*([0-9a-fA-F]{32})")]
    private static partial Regex GuidPattern();

    private sealed record Entry(string Guid, string RelativePath, string LocalPath);

    public async Task<UnityDependencies> FindAsync(IReadOnlyCollection<long> assetIds, CancellationToken ct = default)
    {
        List<Asset> assets;
        await using (var db = catalog.CreateDbContext())
        {
            assets = [];
            foreach (var chunk in assetIds.Distinct().Chunk(500))
                assets.AddRange(await db.Assets.AsNoTracking().Include(a => a.Files).Include(a => a.Source)
                    .Where(a => chunk.Contains(a.Id)).ToListAsync(ct));
        }
        var files = new List<DependencyFile>();
        var problems = new List<string>();
        var leases = new List<IDisposable>();
        try
        {
            foreach (var group in assets.GroupBy(a => a.SourceId))
            {
                var source = group.First().Source;
                var index = await IndexAsync(source, leases, problems, ct);
                if (index is null) continue;
                var byGuid = index.GroupBy(e => e.Guid, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
                var byPath = index.GroupBy(e => Normalize(e.RelativePath), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

                // The exported files themselves are not dependencies; each one seeds the scan.
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var queue = new Queue<(Entry Entry, long Asset)>();
                foreach (var asset in group)
                    foreach (var f in asset.Files)
                    {
                        var entry = (f.UnityGuid is { } g ? byGuid.GetValueOrDefault(g) : null) ?? byPath.GetValueOrDefault(Normalize(f.RelativePath));
                        if (entry is not null && seen.Add(entry.Guid)) queue.Enqueue((entry, asset.Id));
                    }
                while (queue.TryDequeue(out var item))
                {
                    ct.ThrowIfCancellationRequested();
                    foreach (var guid in References(item.Entry))
                    {
                        if (!byGuid.TryGetValue(guid, out var dep) || !seen.Add(dep.Guid)) continue;
                        files.Add(new DependencyFile(item.Asset, dep.Guid, dep.RelativePath, dep.LocalPath));
                        queue.Enqueue((dep, item.Asset));
                    }
                }
            }
            return new UnityDependencies(files, problems, leases);
        }
        catch
        {
            foreach (var l in leases) l.Dispose();
            throw;
        }
    }

    private static IEnumerable<string> References(Entry entry)
    {
        var texts = new List<string>();
        if (Scanned.Contains(Path.GetExtension(entry.LocalPath))) texts.Add(entry.LocalPath);
        if (File.Exists(entry.LocalPath + ".meta")) texts.Add(entry.LocalPath + ".meta");
        foreach (var path in texts)
        {
            string text;
            try
            {
                text = File.ReadAllText(path);
            }
            catch (IOException)
            {
                continue;
            }
            if (!text.StartsWith("%YAML", StringComparison.Ordinal) && !path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue; // binary-serialized assets
            foreach (Match m in GuidPattern().Matches(text))
                if (!m.Groups[1].Value.Equals(entry.Guid, StringComparison.OrdinalIgnoreCase)) yield return m.Groups[1].Value;
        }
    }

    /// <summary>Every file of the source that has a GUID, or null (with a problem) when the source cannot be read.</summary>
    private async Task<List<Entry>?> IndexAsync(Source source, List<IDisposable> leases, List<string> problems, CancellationToken ct)
    {
        try
        {
            if (source.Kind == SourceKind.Folder)
            {
                var root = resolver.GetOuterPath(source.Location);
                if (root is null || !Directory.Exists(root))
                    throw new OriginalUnavailableException(await resolver.DescribeMissingAsync(source.Location, ct));
                return Directory.EnumerateFiles(root, "*.meta", SearchOption.AllDirectories)
                    .Select(meta => (Meta: meta, File: meta[..^5]))
                    .Where(x => File.Exists(x.File) && UnityMaterial.GuidFromMeta(x.Meta) is not null)
                    .Select(x => new Entry(UnityMaterial.GuidFromMeta(x.Meta)!, Path.GetRelativePath(root, x.File), x.File))
                    .ToList();
            }
            if (source.Kind is not (SourceKind.UnityPackage or SourceKind.Archive)) return [];
            var container = await resolver.ResolveAsync(source.Location, ct);
            leases.Add(container);
            var reader = readers.FirstOrDefault(r => r.CanRead(Path.GetFileName(source.Location.Inner.LastOrDefault() ?? container.Path)))
                ?? readers.FirstOrDefault(r => r.CanRead(container.Path));
            if (reader is null) return [];
            var contents = await reader.OpenAsync(container.Path, ct);
            leases.Add(contents);
            return contents.Entries
                .Select(e => e.UnityGuid is { } g ? new Entry(g, e.Key, e.LocalPath)
                    : File.Exists(e.LocalPath + ".meta") && UnityMaterial.GuidFromMeta(e.LocalPath + ".meta") is { } mg ? new Entry(mg, e.Key, e.LocalPath) : null)
                .OfType<Entry>()
                .ToList();
        }
        catch (Exception e) when (e is OriginalUnavailableException or ContainerReadException or IOException or UnauthorizedAccessException)
        {
            problems.Add($"Referenced files of \"{source.DisplayName}\" could not be read: {e.Message}");
            return null;
        }
    }

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');
}
