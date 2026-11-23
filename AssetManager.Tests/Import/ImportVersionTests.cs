using AssetManager.Core.Domain;
using AssetManager.Core.Jobs;
using AssetManager.Data.Services;
using AssetManager.Processing.Import;
using AssetManager.Tests.Fixtures;
using ImageMagick;
using Microsoft.EntityFrameworkCore;

namespace AssetManager.Tests.Import;

/// <summary>New package versions (spec "Update source from new version") on two generated package versions.</summary>
public class ImportVersionTests
{
    private static readonly AssetStoreHeader Header36 = new("40995", "Game Kit Controller", "Two Cubes", "3.6");
    private static readonly AssetStoreHeader Header377 = new("40995", "Game Kit Controller", "Two Cubes", "3.77c");

    private static UnityPackageEntry Texture(string name, MagickColor color, int size = 32) =>
        new(FixtureBuilder.Guid(name), $"Assets/GKC/Textures/{name}.png", ImportStagingTests.Png(color, size));

    /// <summary>3.6 has rock, grass and old; 3.77c changes rock, keeps grass, drops old and adds fresh.</summary>
    private static (string V1, string V2) Packages(TempDir dir, bool withHeader = true)
    {
        var v1 = dir.Write("GKC 3.6 Asset Store.unitypackage", FixtureBuilder.UnityPackage(
            [Texture("rock", MagickColors.Gray), Texture("grass", MagickColors.Green), Texture("old", MagickColors.Red)], withHeader ? Header36 : null));
        var v2 = dir.Write("GKC 3.77c Asset Store.unitypackage", FixtureBuilder.UnityPackage(
            [Texture("rock", MagickColors.DarkGray, 48), Texture("grass", MagickColors.Green), Texture("fresh", MagickColors.Blue)], withHeader ? Header377 : null));
        return (v1, v2);
    }

    /// <summary>Imports and confirms the first version, then gives "rock" a user tag and a user-edited name.</summary>
    private static async Task<(long SourceId, long RockId)> CatalogFirstVersionAsync(ImportKit kit, string v1)
    {
        var ct = TestContext.Current.CancellationToken;
        var (session, _) = await kit.ImportAsync(new ImportRequest([v1], Depth: AiDepth.None));
        var commit = await kit.Jobs.WaitAsync(await kit.Imports.ConfirmAsync(session, ct), ct);
        Assert.True(commit.State == JobState.Completed, commit.Error);
        await using var db = kit.Catalog.Db();
        var rock = await db.Assets.Include(a => a.Tags).SingleAsync(a => a.Name == "rock", ct);
        var favorite = (await TagService.GetOrCreateAsync(db, ["favorite"], ct)).Single();
        rock.Tags.Add(new AssetTag { Tag = favorite, Origin = TagOrigin.User });
        rock.Name = "Big Rock";
        rock.UserEdited |= EditedFields.Name;
        await db.SaveChangesAsync(ct);
        return (rock.SourceId, rock.Id);
    }

    [Fact]
    public async Task Renamed_package_version_is_proposed_with_counts()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var kit = await ImportKit.CreateAsync();
        var (v1, v2) = Packages(kit.Catalog.Dir);
        var (oldSource, _) = await CatalogFirstVersionAsync(kit, v1);

        var (session, job) = await kit.ImportAsync(new ImportRequest([v2], Depth: AiDepth.None));

        Assert.True(job.State == JobState.Completed, job.Error);
        await using var db = kit.Catalog.Db();
        var staged = await db.AllSources.SingleAsync(s => s.ImportSessionId == session, ct);
        Assert.Equal((oldSource, 1.0), (staged.ReplacesSourceId, staged.ReplaceScore));
        var diff = await SourceVersions.DiffAsync(db, staged.Id, oldSource, ct);
        Assert.Equal((2, 1, 1), (diff.Updated.Count, diff.Added.Count, diff.Removed.Count));
        // The review step shows the proposal with these counts ("Version update counts").
        var review = (await kit.Review.LoadAsync(session, ct))!.Sources.Single(s => s.Id == staged.Id).Replacement!;
        Assert.Equal(("Game Kit Controller", 2, 1, 1), (review.OldName, review.Updated, review.Added, review.Removed));
        var grassItem = (await kit.Review.LoadAsync(session, ct))!.Sources.SelectMany(s => s.Items).Single(i => i.Name == "grass");
        Assert.True(grassItem is { Unchanged: true, Duplicate: false }, "an unchanged file of the replaced version is not shown as a duplicate");
        // The unchanged texture is not skipped as a duplicate: it is an update of the old version.
        var grass = await db.AllAssets.SingleAsync(a => a.ImportSessionId == session && a.Name == "grass", ct);
        Assert.True(grass.DuplicateOfAssetId is not null && grass.Included);
    }

    [Fact]
    public async Task Removed_asset_in_new_version()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var kit = await ImportKit.CreateAsync();
        var (v1, v2) = Packages(kit.Catalog.Dir);
        var (oldSource, rockId) = await CatalogFirstVersionAsync(kit, v1);
        var (session, _) = await kit.ImportAsync(new ImportRequest([v2], Depth: AiDepth.None));

        var commit = await kit.Jobs.WaitAsync(await kit.Imports.ConfirmAsync(session, ct), ct);

        Assert.True(commit.State == JobState.Completed, commit.Error);
        Assert.Contains("\"assets\":3", commit.SummaryJson);
        Assert.Contains("\"replaced\":1", commit.SummaryJson);
        await using var db = kit.Catalog.Db();
        var source = await db.Sources.SingleAsync(ct); // replaced in place, not added
        Assert.Equal(oldSource, source.Id);
        Assert.Equal(("Game Kit Controller", "3.77c"), (source.DisplayName, source.Store.Version));
        Assert.EndsWith("GKC 3.77c Asset Store.unitypackage", source.Location.Path);
        var assets = await db.Assets.Include(a => a.Tags).ThenInclude(t => t.Tag).Include(a => a.Files).ToListAsync(ct);
        Assert.Equal(["Big Rock", "fresh", "grass"], assets.Select(a => a.Name).Order(StringComparer.OrdinalIgnoreCase));
        var rock = assets.Single(a => a.Name == "Big Rock");
        Assert.Equal(rockId, rock.Id);                                         // updated in place
        Assert.Contains(rock.Tags, t => t.Tag.Name == "favorite" && t.Origin == TagOrigin.User);
        Assert.Equal(48, rock.Image!.Width);                                  // the new version's content
        Assert.EndsWith("GKC 3.77c Asset Store.unitypackage", rock.PrimaryFile!.Location.Path);
        Assert.True(await db.Previews.AnyAsync(p => p.AssetId == rock.Id, ct));
        Assert.Equal(0, await db.AllAssets.CountAsync(a => a.ImportSessionId != null, ct));
        Assert.Equal(0, await db.AllSources.CountAsync(s => s.ImportSessionId != null, ct));
    }

    [Fact]
    public async Task User_declines_replacement()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var kit = await ImportKit.CreateAsync();
        var (v1, v2) = Packages(kit.Catalog.Dir, withHeader: false); // matched by GUID overlap: 2 shared of 4 = 0.5
        var (oldSource, _) = await CatalogFirstVersionAsync(kit, v1);
        var (session, _) = await kit.ImportAsync(new ImportRequest([v2], Depth: AiDepth.None));
        await using (var db = kit.Catalog.Db())
        {
            var staged = await db.AllSources.SingleAsync(s => s.ImportSessionId == session, ct);
            Assert.Equal((oldSource, 0.5), (staged.ReplacesSourceId, staged.ReplaceScore));
            staged.ReplaceAccepted = false;
            await db.SaveChangesAsync(ct);
        }

        var commit = await kit.Jobs.WaitAsync(await kit.Imports.ConfirmAsync(session, ct), ct);

        Assert.True(commit.State == JobState.Completed, commit.Error);
        await using var check = kit.Catalog.Db();
        Assert.Equal(2, await check.Sources.CountAsync(ct));
        Assert.Equal(["Big Rock", "grass", "old"], (await check.Assets.Where(a => a.SourceId == oldSource).Select(a => a.Name).ToListAsync(ct)).Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(3, await check.Assets.CountAsync(a => a.SourceId != oldSource, ct));
    }
    [Fact]
    public async Task Replacement_confirmed_before_ai_finishes_is_enriched_in_place()
    {
        var ct = TestContext.Current.CancellationToken;
        var chat = new SchemaAwareChatClient();
        await using var kit = await ImportKit.CreateAsync(chat);
        var (v1, v2) = Packages(kit.Catalog.Dir);
        var (oldSource, rockId) = await CatalogFirstVersionAsync(kit, v1);
        kit.Ai.Pause();
        var session = await kit.Imports.StartAsync(new ImportRequest([v2]), ct);
        await kit.WaitReadyAsync(session.Id);

        var commit = await kit.Jobs.WaitAsync(await kit.Imports.ConfirmAsync(session.Id, ct), ct);
        Assert.True(commit.State == JobState.Completed, commit.Error);
        kit.Ai.Resume();
        var import = await kit.Jobs.WaitAsync(session.JobId!.Value, ct);

        Assert.True(import.State == JobState.Completed, import.Error);
        await using var db = kit.Catalog.Db();
        var source = await db.Sources.SingleAsync(ct);
        Assert.Equal((oldSource, "A pack of test assets."), (source.Id, source.Description)); // pack pass ran on the replaced source
        var assets = await db.Assets.ToListAsync(ct);
        Assert.All(assets, a => Assert.Equal(AiStatus.Enriched, a.AiStatus));
        var rock = assets.Single(a => a.Id == rockId);
        Assert.Equal(("Big Rock", "Described Big Rock."), (rock.Name, rock.Description)); // re-enriched in place, user name kept
    }
}
