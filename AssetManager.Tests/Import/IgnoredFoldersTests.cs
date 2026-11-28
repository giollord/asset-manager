using AssetManager.Core.Domain;
using AssetManager.Core.Jobs;
using AssetManager.Processing.Containers;
using AssetManager.Processing.Import;
using AssetManager.Tests.Fixtures;
using ImageMagick;
using Microsoft.EntityFrameworkCore;

namespace AssetManager.Tests.Import;

/// <summary>Spec "Ignored folders" and "Ignore rule matching".</summary>
public class IgnoredFoldersTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(List<string> Staged, List<StagedSkip> Skips, List<string> Sources)> StagedAsync(ImportKit kit, long session)
    {
        await using var db = kit.Catalog.Db();
        var staged = await db.AllAssets.Where(a => a.Source.CommittedFromSessionId == session).SelectMany(a => a.Files).Select(f => f.RelativePath).ToListAsync(Ct);
        var skips = await db.StagedSkips.Where(s => s.SessionId == session).ToListAsync(Ct);
        var sources = await db.AllSources.Where(s => s.CommittedFromSessionId == session).Select(s => s.DisplayName).ToListAsync(Ct);
        return (staged.Select(p => p.Replace('\\', '/')).ToList(), skips, sources);
    }

    [Theory]
    [InlineData("node_modules", "tools/node_modules", true)]
    [InlineData("node_modules", "NODE_MODULES", true)]
    [InlineData("node_modules", "tools/node_modules_old", false)]
    [InlineData("*.bak", "Models/old.bak", true)]
    [InlineData("Assets/*/Demo", "Assets/Kit/Demo", true)]
    [InlineData("Assets/*/Demo", "Assets/Demo", false)]
    [InlineData("Assets/*/Demo", "Assets/Kit/Sub/Demo", false)]
    [InlineData(@"\Assets\Demo\", "assets/demo", true)]
    public void Rules_match_folder_names_and_paths(string rule, string folder, bool ignored) =>
        Assert.Equal(ignored, new FolderIgnoreRules([rule]).Match(folder) is not null);

    [Fact]
    public void A_file_is_ignored_by_its_outermost_ignored_folder()
    {
        var rules = new FolderIgnoreRules(["node_modules", ".git"]);
        Assert.Equal(("web/node_modules", "node_modules"), rules.IgnoredAncestor("web/node_modules/a/.git/x.png"));
        Assert.Null(rules.IgnoredAncestor("web/node_modules.png"));
    }

    [Fact]
    public async Task Default_ignore()
    {
        await using var kit = await ImportKit.CreateAsync();
        var dir = kit.Catalog.Dir;
        var folder = Path.Combine(dir.Path, "Project");
        dir.Write("Project/Models/rock.png", ImportStagingTests.Png(MagickColors.Gray));
        for (var i = 0; i < 30; i++) dir.Write($"Project/tools/node_modules/pkg{i}/icon.png", ImportStagingTests.Png(MagickColors.Red));
        dir.Write("Project/tools/node_modules/pkg0/bundle.zip", FixtureBuilder.Zip(new FixtureFile("a.png", ImportStagingTests.Png(MagickColors.Blue))));

        var (session, job) = await kit.ImportAsync(new ImportRequest([folder], Depth: AiDepth.None));

        Assert.True(job.State == JobState.Completed, job.Error);
        var (staged, skips, sources) = await StagedAsync(kit, session);
        Assert.Equal(["Models/rock.png"], staged);
        var skip = Assert.Single(skips, s => s.Kind == SkipKind.Ignored);
        Assert.Equal(("tools/node_modules", "ignored folder (node_modules)"), (skip.Path, skip.Reason));
        Assert.DoesNotContain("bundle", sources); // the zip inside was never opened

        // The default list can be turned off for one import.
        var (all, _) = await kit.ImportAsync(new ImportRequest([folder], Depth: AiDepth.None, IncludeDuplicates: true, UseDefaultIgnores: false));
        Assert.Contains("tools/node_modules/pkg5/icon.png", (await StagedAsync(kit, all)).Staged);
    }

    [Fact]
    public async Task Extra_ignore_for_one_import()
    {
        await using var kit = await ImportKit.CreateAsync();
        UnityPackageEntry Png(string path, MagickColor color) => new(FixtureBuilder.Guid(path), path, ImportStagingTests.Png(color));
        var package = kit.Catalog.Dir.Write("Kit.unitypackage", FixtureBuilder.UnityPackage(
        [
            Png("Assets/Kit/Textures/wood.png", MagickColors.Brown),
            Png("Assets/Kit/Demo/demo_floor.png", MagickColors.Gray),
            Png("Assets/Props/Demo/demo_sky.png", MagickColors.SkyBlue),
            Png("Assets/Demo/readme_image.png", MagickColors.White),
        ]));

        var (session, job) = await kit.ImportAsync(new ImportRequest([package], Depth: AiDepth.None, IgnoredFolders: ["Assets/*/Demo"]));

        Assert.True(job.State == JobState.Completed, job.Error);
        var (staged, skips, _) = await StagedAsync(kit, session);
        Assert.Equal(["Assets/Demo/readme_image.png", "Assets/Kit/Textures/wood.png"], staged.Order(StringComparer.Ordinal));
        Assert.Equal(["Assets/Kit/Demo", "Assets/Props/Demo"], skips.Where(s => s.Kind == SkipKind.Ignored).Select(s => s.Path).Order(StringComparer.Ordinal));
        Assert.All(skips.Where(s => s.Kind == SkipKind.Ignored), s => Assert.Equal("ignored folder (Assets/*/Demo)", s.Reason));

        // A later import does not use the rule.
        var (later, _) = await kit.ImportAsync(new ImportRequest([package], Depth: AiDepth.None, IncludeDuplicates: true));
        Assert.Equal(4, (await StagedAsync(kit, later)).Staged.Count);
    }

    [Fact]
    public async Task Ignored_archive()
    {
        await using var kit = await ImportKit.CreateAsync();
        var dir = kit.Catalog.Dir;
        dir.Write("Lib/Sounds/beep.png", ImportStagingTests.Png(MagickColors.Teal));
        dir.Write("Lib/Sounds.zip", FixtureBuilder.Zip(new FixtureFile("Sounds/beep2.png", ImportStagingTests.Png(MagickColors.Coral))));

        var (session, job) = await kit.ImportAsync(new ImportRequest([Path.Combine(dir.Path, "Lib")], Depth: AiDepth.None, IgnoredFolders: ["Sounds.zip"]));

        Assert.True(job.State == JobState.Completed, job.Error);
        var (staged, skips, sources) = await StagedAsync(kit, session);
        Assert.Equal(["Sounds/beep.png"], staged);
        var skip = Assert.Single(skips, s => s.Kind == SkipKind.Ignored);
        Assert.Equal(("Sounds.zip", "ignored folder (Sounds.zip)"), (skip.Path, skip.Reason));
        Assert.Equal(["Lib"], sources);
    }

    [Fact]
    public async Task Ignored_folder_inside_a_package()
    {
        await using var kit = await ImportKit.CreateAsync();
        var inner = FixtureBuilder.Zip(new FixtureFile("hidden.png", ImportStagingTests.Png(MagickColors.Pink)));
        var zip = kit.Catalog.Dir.Write("Pack.zip", FixtureBuilder.Zip(
            new FixtureFile("Pack/.git/objects/pack.zip", inner),
            new FixtureFile("Pack/.git/config.png", ImportStagingTests.Png(MagickColors.Black)),
            new FixtureFile("Pack/Models/crate.png", ImportStagingTests.Png(MagickColors.Olive))));

        var (session, job) = await kit.ImportAsync(new ImportRequest([zip], Depth: AiDepth.None));

        Assert.True(job.State == JobState.Completed, job.Error);
        var (staged, skips, sources) = await StagedAsync(kit, session);
        Assert.Equal(["Pack/Models/crate.png"], staged);
        Assert.Equal("Pack/.git", Assert.Single(skips, s => s.Kind == SkipKind.Ignored).Path);
        Assert.Equal(["Pack"], sources); // no source for the zip inside .git: it was not opened
    }
}
