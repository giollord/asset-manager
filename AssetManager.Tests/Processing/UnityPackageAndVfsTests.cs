using System.Text;
using AssetManager.Core.Domain;
using AssetManager.Processing.Containers;
using AssetManager.Tests.Fixtures;

namespace AssetManager.Tests.Processing;

public class UnityPackageAndVfsTests
{
    private static readonly string WoodGuid = FixtureBuilder.Guid("wood");
    private static readonly string MatGuid = FixtureBuilder.Guid("mat");

    private static byte[] Package(AssetStoreHeader? header = null) => FixtureBuilder.UnityPackage(
    [
        new UnityPackageEntry(FixtureBuilder.Guid("folder"), "Assets/Props", null),
        new UnityPackageEntry(WoodGuid, "Assets/Props/Textures/wood.png", Encoding.UTF8.GetBytes("WOOD"), Preview: Encoding.UTF8.GetBytes("PREVIEW")),
        new UnityPackageEntry(MatGuid, "Assets/Props/Wood.mat\n00", Encoding.UTF8.GetBytes("%YAML 1.1")),
    ], header);

    [Fact]
    public async Task Restores_original_paths_guids_meta_and_preview()
    {
        using var kit = new ProcessingKit();
        var path = kit.Dir.Write("props.unitypackage", Package());
        using var contents = await kit.Packages.OpenAsync(path);

        Assert.Equal(["Assets/Props/Textures/wood.png", "Assets/Props/Wood.mat"], contents.Entries.Select(e => e.Key));
        var wood = contents.Entries[0];
        Assert.Equal(WoodGuid, wood.UnityGuid);
        Assert.Equal("wood.png", Path.GetFileName(wood.LocalPath));
        Assert.Equal("WOOD", File.ReadAllText(wood.LocalPath));
        Assert.Contains($"guid: {WoodGuid}", File.ReadAllText(wood.MetaPath!));
        Assert.Equal("PREVIEW", File.ReadAllText(wood.PreviewPath!));
        Assert.Null(contents.Entries[1].PreviewPath);
        Assert.Null(contents.Store);
    }

    [Fact]
    public async Task Reads_asset_store_header()
    {
        using var kit = new ProcessingKit();
        var path = kit.Dir.Write("store.unitypackage", Package(new AssetStoreHeader("123456", "Medieval Props", "Some Studio", "1.2.0")));
        using var contents = await kit.Packages.OpenAsync(path);
        var store = Assert.IsType<StoreInfo>(contents.Store);
        Assert.Equal("123456", store.StoreId);
        Assert.Equal("Medieval Props", store.Title);
        Assert.Equal("Some Studio", store.Publisher);
        Assert.Equal("1.2.0", store.Version);
        Assert.Equal("https://assetstore.unity.com/packages/slug/123456", store.Url);
        Assert.Equal(2, contents.Entries.Count);
    }

    [Theory]
    [InlineData(0.3)]
    [InlineData(0.5)]
    [InlineData(0.7)]
    [InlineData(0.9)]
    [InlineData(0.99)]
    public async Task Truncated_package_is_an_error(double keep)
    {
        // A cut can land where the reader sees a clean end of the tar; the gzip size check must still catch it.
        using var kit = new ProcessingKit();
        var bytes = Package();
        var path = kit.Dir.Write("broken.unitypackage", bytes[..(int)(bytes.Length * keep)]);
        await Assert.ThrowsAsync<ContainerReadException>(() => kit.Packages.OpenAsync(path));
    }

    [Fact]
    public async Task Package_inside_zip_yields_three_level_chain()
    {
        using var kit = new ProcessingKit();
        var zip = kit.Dir.Write("bundle.zip", FixtureBuilder.Zip(
            new FixtureFile("Unity/props.unitypackage", Package()),
            FixtureFile.Text("License.txt", "CC0")));

        var nodes = await kit.WalkAsync(zip);

        var tex = Assert.Single(nodes, n => n.Location.FileName == "wood.png");
        Assert.Equal(VfsNodeKind.File, tex.Kind);
        Assert.Equal(new Location(null, zip, ["Unity/props.unitypackage", "Assets/Props/Textures/wood.png"]), tex.Location);
        Assert.Equal(2, tex.Depth);
        Assert.Equal(WoodGuid, tex.UnityGuid);
        Assert.Equal($@"{zip} → Unity/props.unitypackage → Assets/Props/Textures/wood.png", tex.Location.Display());

        var containers = nodes.Where(n => n.Kind == VfsNodeKind.Container).ToList();
        Assert.Equal([SourceKind.Archive, SourceKind.UnityPackage], containers.Select(c => c.ContainerKind!.Value));
        Assert.Contains(nodes, n => n.RelativePath == "License.txt" && n.Depth == 1);
    }

    [Fact]
    public async Task Opening_callback_names_each_container_before_its_entries()
    {
        using var kit = new ProcessingKit();
        var zip = kit.Dir.Write("bundle.zip", FixtureBuilder.Zip(
            new FixtureFile("Unity/props.unitypackage", Package()),
            FixtureFile.Text("License.txt", "CC0")));

        // Each event: either "open:<location>" from the callback or "node:<location>" from the walk.
        var events = new List<string>();
        await foreach (var n in kit.Vfs.WalkAsync(zip, new(null, zip), opening: loc => events.Add("open:" + loc.Display())))
            events.Add("node:" + n.Location.Display());

        var openZip = events.IndexOf("open:" + zip);
        var openPackage = events.IndexOf($"open:{zip} → Unity/props.unitypackage");
        Assert.True(openZip >= 0 && openPackage > openZip, string.Join(" | ", events));
        Assert.True(openZip < events.FindIndex(e => e.StartsWith($"node:{zip} → ")));
        Assert.True(openPackage < events.FindIndex(e => e.StartsWith($"node:{zip} → Unity/props.unitypackage → ")));
    }

    /// <summary>A folder with plain files, an open zip, an ignored folder and a zip kept closed by a rule.</summary>
    private static string MixedFolder(ProcessingKit kit)
    {
        var root = Path.Combine(kit.Dir.Path, "mixed");
        kit.Dir.Write("mixed/a.png", "A");
        kit.Dir.Write("mixed/Sub/b.wav", "B");
        kit.Dir.Write("mixed/Pack.zip", FixtureBuilder.Zip(FixtureFile.Text("inner/c.txt", "C"), FixtureFile.Text("d.txt", "D")));
        kit.Dir.Write("mixed/node_modules/x.png", "X");
        kit.Dir.Write("mixed/Old.zip", FixtureBuilder.Zip(FixtureFile.Text("e.txt", "E")));
        return root;
    }

    [Fact]
    public void Listing_counts_the_top_level_files_the_walk_visits()
    {
        using var kit = new ProcessingKit();
        var root = MixedFolder(kit);

        var listing = kit.Vfs.List(root, new FolderIgnoreRules(["node_modules", "Old.zip"]));

        // Spec "Import scan counts files processed out of total": the ignored folder and the closed zip are left out.
        Assert.Equal(["a.png", "Pack.zip", @"Sub\b.wav"], listing.Files.Order(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Walking_a_listing_yields_the_same_nodes_as_walking_the_folder()
    {
        using var kit = new ProcessingKit();
        var root = MixedFolder(kit);
        var ignore = new FolderIgnoreRules(["node_modules", "Old.zip"]);

        var fromPath = new List<string>();
        await foreach (var n in kit.Vfs.WalkAsync(root, new(null, root), ignore: ignore)) fromPath.Add($"{n.Kind}:{n.Location.Display()}");
        var fromListing = new List<string>();
        await foreach (var n in kit.Vfs.WalkAsync(kit.Vfs.List(root, ignore), new(null, root))) fromListing.Add($"{n.Kind}:{n.Location.Display()}");

        Assert.Equal(fromPath, fromListing);
        Assert.Contains(fromListing, e => e.StartsWith("File:") && e.EndsWith("inner/c.txt"));
        Assert.Equal(2, fromListing.Count(e => e.StartsWith("IgnoredFolder:")));
    }

    [Fact]
    public async Task Depth_limit_stops_recursion()
    {
        using var kit = new ProcessingKit();
        var zip = kit.Dir.Write("bundle.zip", FixtureBuilder.Zip(new FixtureFile("Unity/props.unitypackage", Package())));

        var nodes = await kit.WalkAsync(zip, maxDepth: 1);

        var package = Assert.Single(nodes, n => n.Kind == VfsNodeKind.File);
        Assert.Equal("props.unitypackage", package.Location.FileName);
        Assert.True(package.DepthLimited);
        Assert.DoesNotContain(nodes, n => n.Location.FileName == "wood.png");

        var none = await kit.WalkAsync(zip, maxDepth: 0);
        Assert.True(Assert.Single(none).DepthLimited);
    }

    /// <summary>Packages downloaded from the Asset Store carry their store metadata (ASSETMANAGER_SAMPLES).</summary>
    [SamplesFact]
    public void Sample_store_package_has_header()
    {
        var folder = Path.Combine(SamplesFactAttribute.SamplesRoot!, "Unity Asset Store Downloaded");
        Assert.SkipUnless(Directory.Exists(folder), "No Asset Store downloads in the samples.");
        var headers = Directory.EnumerateFiles(folder, "*.unitypackage", SearchOption.AllDirectories)
            .Take(20)
            .Select(UnityPackageReader.ReadStoreHeader)
            .OfType<StoreInfo>()
            .ToList();
        Assert.NotEmpty(headers);
        Assert.All(headers, h =>
        {
            Assert.False(string.IsNullOrWhiteSpace(h.StoreId));
            Assert.False(string.IsNullOrWhiteSpace(h.Title));
            Assert.False(string.IsNullOrWhiteSpace(h.Publisher));
            Assert.StartsWith("https://assetstore.unity.com/packages/", h.Url);
        });
    }

    /// <summary>The GKC sample packages list their original Assets/... paths (ASSETMANAGER_SAMPLES).</summary>
    [SamplesFact]
    public async Task Sample_packages_list_original_paths()
    {
        var packages = Directory.EnumerateFiles(SamplesFactAttribute.SamplesRoot!, "*.unitypackage", SearchOption.AllDirectories)
            .Where(p => p.Contains("GKC", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => new FileInfo(p).Length)
            .Take(3)
            .ToList();
        Assert.SkipWhen(packages.Count == 0, "No GKC packages in the samples.");
        using var kit = new ProcessingKit();
        foreach (var p in packages)
        {
            using var contents = await kit.Packages.OpenAsync(p);
            Assert.NotEmpty(contents.Entries);
            Assert.All(contents.Entries, e =>
            {
                Assert.StartsWith("Assets/", e.Key);
                Assert.Matches("^[0-9a-f]{32}$", e.UnityGuid!);
                Assert.True(File.Exists(e.LocalPath));
            });
        }
    }
}
