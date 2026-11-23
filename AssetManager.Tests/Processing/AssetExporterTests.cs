using System.Text;
using AssetManager.Core.Domain;
using AssetManager.Processing.Export;
using AssetManager.Processing.Import;
using AssetManager.Processing.Storage;
using AssetManager.Tests.Data;
using AssetManager.Tests.Fixtures;
using AssetManager.Tests.Import;
using ImageMagick;
using Microsoft.EntityFrameworkCore;

namespace AssetManager.Tests.Processing;

/// <summary>Spec "Extract to folder", "Export when unavailable".</summary>
public class AssetExporterTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static UnityPackageEntry Texture(string name, MagickColor color) =>
        new(FixtureBuilder.Guid(name), $"Assets/Kit/Textures/{name}.png", ImportStagingTests.Png(color));

    /// <summary>Catalogs a generated package of 10 textures; returns the asset ids.</summary>
    private static async Task<List<long>> CatalogPackageAsync(ImportKit kit)
    {
        var colors = new[] { MagickColors.Red, MagickColors.Green, MagickColors.Blue, MagickColors.Gray, MagickColors.Gold,
            MagickColors.Pink, MagickColors.Teal, MagickColors.Navy, MagickColors.Olive, MagickColors.Maroon };
        var package = kit.Catalog.Dir.Write("Kit.unitypackage", FixtureBuilder.UnityPackage(colors.Select((c, i) => Texture($"tex{i}", c))));
        await kit.CatalogAsync(new ImportRequest([package], Depth: AiDepth.None));
        await using var db = kit.Catalog.Db();
        return await db.Assets.OrderBy(a => a.Name).Select(a => a.Id).ToListAsync(Ct);
    }

    [Fact]
    public async Task Extract_preserving_structure()
    {
        await using var kit = await ImportKit.CreateAsync();
        var ids = await CatalogPackageAsync(kit);
        Assert.Equal(10, ids.Count);
        var exporter = new AssetExporter(kit.Store, kit.Processing.Resolver);
        var target = Path.Combine(kit.Catalog.Dir.Path, "Proj", "Assets", "Imported");

        var result = await exporter.ExtractAsync(ids, new ExportOptions(target, PreserveStructure: true), ct: Ct);

        Assert.Equal((10, 10, 0), (result.Exported, result.Written.Count, result.Unavailable.Count));
        for (var i = 0; i < 10; i++)
        {
            var file = Path.Combine(target, "Assets", "Kit", "Textures", $"tex{i}.png");
            Assert.True(File.Exists(file), file);
        }
        // The bytes are the original file's, not a preview.
        using var image = new MagickImage(Path.Combine(target, "Assets", "Kit", "Textures", "tex0.png"));
        Assert.Equal(32u, image.Width);

        // Flat: the file names only.
        var flat = Path.Combine(kit.Catalog.Dir.Path, "Flat");
        await exporter.ExtractAsync(ids.Take(2).ToList(), new ExportOptions(flat, PreserveStructure: false), ct: Ct);
        Assert.Equal(["tex0.png", "tex1.png"], Directory.GetFiles(flat).Select(Path.GetFileName).Order().ToArray());
    }

    [Fact]
    public async Task Name_conflict()
    {
        await using var kit = await ImportKit.CreateAsync();
        var ids = await CatalogPackageAsync(kit);
        var exporter = new AssetExporter(kit.Store, kit.Processing.Resolver);
        var target = Path.Combine(kit.Catalog.Dir.Path, "Out");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "tex0.png"), "mine");
        File.WriteAllText(Path.Combine(target, "tex1.png"), "mine");
        File.WriteAllText(Path.Combine(target, "tex2.png"), "mine");
        var three = ids.Take(3).ToList();

        // The plan lists the conflicts before anything is written.
        var plan = await exporter.PlanAsync(three, target, preserveStructure: false, Ct);
        Assert.Equal(["tex0.png", "tex1.png", "tex2.png"], plan.Conflicts.Order().ToArray());
        Assert.Equal("mine", File.ReadAllText(Path.Combine(target, "tex0.png")));

        // One choice per file: skip tex0, overwrite tex1, rename tex2.
        var choices = new Dictionary<string, ConflictChoice> { ["tex0.png"] = ConflictChoice.Skip, ["tex1.png"] = ConflictChoice.Overwrite, ["tex2.png"] = ConflictChoice.Rename };
        var result = await exporter.ExtractAsync(three, new ExportOptions(target, false, ConflictChoice.Skip, choices), ct: Ct);

        Assert.Equal(1, result.SkippedFiles);
        Assert.Equal("mine", File.ReadAllText(Path.Combine(target, "tex0.png")));
        Assert.NotEqual("mine", File.ReadAllText(Path.Combine(target, "tex1.png")));
        Assert.Equal("mine", File.ReadAllText(Path.Combine(target, "tex2.png")));
        Assert.True(File.Exists(Path.Combine(target, "tex2 (2).png")));

        // "Apply to all": the default choice covers every remaining conflict.
        result = await exporter.ExtractAsync(three, new ExportOptions(target, false, ConflictChoice.Skip), ct: Ct);
        Assert.Equal((3, 0), (result.SkippedFiles, result.Written.Count));

        // An asset listed twice is exported once, so it does not conflict with itself.
        var twice = await exporter.PlanAsync([ids[3], ids[3]], Path.Combine(kit.Catalog.Dir.Path, "Empty"), false, Ct);
        Assert.Empty(twice.Conflicts);
    }

    [Fact]
    public async Task Partial_availability()
    {
        await using var c = await TestCatalog.CreateAsync();
        using var kit = new ProcessingKit(c.Store);
        kit.Dir.Write(@"A\pack\a0.png", "A0");
        kit.Dir.Write(@"A\pack\a1.png", "A1");
        kit.Dir.Write(@"A\pack\a2.obj", "A2");
        kit.Dir.Write(@"A\pack\a2_albedo.png", "A2-TEX");
        kit.Dir.Write(@"B\pack\b0.png", "B0");
        kit.Dir.Write(@"B\pack\b1.png", "B1");
        kit.Volumes.Mount("AAAA0001", "Main", Path.Combine(kit.Dir.Path, "A"));
        kit.Volumes.Mount("BBBB0002", "Backup", Path.Combine(kit.Dir.Path, "B"));

        static AssetFile File(string vol, string name, FileRole role = FileRole.Primary) => new()
        {
            Role = role, Location = new Location(vol, $@"pack\{name}"), RelativePath = $"pack/{name}", FileName = name, Extension = Path.GetExtension(name),
        };
        var src = await TestData.SourceAsync(c, 5, make: i => i switch
        {
            0 => new Asset { Name = "a0", Files = [File("AAAA0001", "a0.png")] },
            1 => new Asset { Name = "a1", Files = [File("AAAA0001", "a1.png")] },
            2 => new Asset { Name = "a2", Type = AssetType.Model3D, Files = [File("AAAA0001", "a2.obj"), File("AAAA0001", "a2_albedo.png", FileRole.Companion)] },
            3 => new Asset { Name = "b0", Files = [File("BBBB0002", "b0.png")] },
            _ => new Asset { Name = "b1", Files = [File("BBBB0002", "b1.png")] },
        });
        var ids = src.Assets.Select(a => a.Id).ToList();

        // b1 is stored in the catalog; then the Backup drive is detached.
        await new StoredOriginalsService(c.Store, kit.Resolver).StoreAsync([ids[4]], ct: Ct);
        kit.Volumes.Detach("BBBB0002");

        var exporter = new AssetExporter(c.Store, kit.Resolver);
        var target = Path.Combine(kit.Dir.Path, "Out");
        var result = await exporter.ExtractAsync(ids, new ExportOptions(target, PreserveStructure: false), ct: Ct);

        // a0, a1, a2 (with its texture) and the stored b1 are exported; b0 is reported.
        Assert.Equal(4, result.Exported);
        Assert.Equal(["a0.png", "a1.png", "a2.obj", "a2_albedo.png", "b1.png"], Directory.GetFiles(target).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal("B1", System.IO.File.ReadAllText(Path.Combine(target, "b1.png"), Encoding.UTF8));
        var missing = Assert.Single(result.Unavailable);
        Assert.Equal(("b0", ids[3]), (missing.Name, missing.AssetId));
        Assert.Contains("missing", missing.Reason);

        // The clipboard copy takes the same files, flat, into a fresh temporary folder.
        var temp = Path.Combine(kit.Dir.Path, "Temp");
        var copy = await new AssetExporter(c.Store, kit.Resolver) { TempRoot = temp }.ExtractForClipboardAsync(ids, ct: Ct);
        Assert.Equal(5, copy.Written.Count);
        Assert.All(copy.Written, p => Assert.StartsWith(temp, p));
        Assert.Single(copy.Unavailable);
    }

    [Theory]
    [InlineData("Assets/Kit/rock.png", true, @"Assets\Kit\rock.png")]
    [InlineData("Assets/Kit/rock.png", false, "rock.png")]
    [InlineData("../../evil/rock.png", true, @"evil\rock.png")]
    [InlineData("", true, "rock.png")]
    public void Target_paths_stay_inside_the_folder(string relative, bool preserve, string expected) =>
        Assert.Equal(expected, AssetExporter.TargetFor(new AssetFile { RelativePath = relative, FileName = "rock.png" }, preserve));
}
