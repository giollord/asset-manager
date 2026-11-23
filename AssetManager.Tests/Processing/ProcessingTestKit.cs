using AssetManager.Core.Platform;
using AssetManager.Data;
using AssetManager.Processing.Cache;
using AssetManager.Processing.Containers;
using AssetManager.Processing.Locations;
using AssetManager.Tests.Fixtures;

namespace AssetManager.Tests.Processing;

/// <summary>A volume service whose drives the test mounts, re-letters and detaches.</summary>
public sealed class FakeVolumeService : IVolumeService
{
    private readonly List<VolumeInfo> _volumes = [];

    public event Action? VolumesChanged;

    public void Mount(string id, string label, string root)
    {
        _volumes.RemoveAll(v => v.Id == id);
        _volumes.Add(new VolumeInfo(id, label, root.EndsWith('\\') ? root : root + "\\"));
        VolumesChanged?.Invoke();
    }

    public void Detach(string id)
    {
        _volumes.RemoveAll(v => v.Id == id);
        VolumesChanged?.Invoke();
    }

    public IReadOnlyList<VolumeInfo> GetVolumes() => _volumes.ToList();

    public VolumeInfo? GetVolumeForPath(string path) =>
        _volumes.Where(v => Path.GetFullPath(path).StartsWith(v.RootPath, StringComparison.OrdinalIgnoreCase)).MaxBy(v => v.RootPath.Length);

    public string? GetRoot(string volumeId) => _volumes.FirstOrDefault(v => v.Id == volumeId)?.RootPath;
}

/// <summary>Cache, readers, VFS and resolver wired over a temp folder.</summary>
public sealed class ProcessingKit : IDisposable
{
    public ProcessingKit(CatalogStore? catalog = null, long maxCacheBytes = long.MaxValue)
    {
        Cache = new ExtractionCache(Path.Combine(Dir.Path, "cache"), () => new CacheLimits(maxCacheBytes, TimeSpan.FromDays(14)));
        Archives = new ArchiveReader(Cache);
        Packages = new UnityPackageReader(Cache);
        Readers = [Packages, Archives];
        Vfs = new VirtualFileSystem(Readers);
        Catalog = catalog ?? new CatalogStore();
        Resolver = new LocationResolver(Volumes, Catalog, Cache, Readers);
    }

    public TempDir Dir { get; } = FixtureBuilder.TempDir();
    public ExtractionCache Cache { get; }
    public ArchiveReader Archives { get; }
    public UnityPackageReader Packages { get; }
    public IContainerReader[] Readers { get; }
    public VirtualFileSystem Vfs { get; }
    public FakeVolumeService Volumes { get; } = new();
    public CatalogStore Catalog { get; }
    public LocationResolver Resolver { get; }

    public async Task<List<VfsNode>> WalkAsync(string path, int? maxDepth = null)
    {
        var list = new List<VfsNode>();
        await foreach (var n in Vfs.WalkAsync(path, new(null, path), maxDepth)) list.Add(n);
        return list;
    }

    public void Dispose() => Dir.Dispose();
}
