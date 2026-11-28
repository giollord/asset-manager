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

    /// <summary>Imports the first version, then gives "rock" a user tag and a user-edited name.</summary>
    private static async Task<(long SourceId, long RockId)> CatalogFirstVersionAsync(ImportKit kit, string v1)
    {
        var ct = TestContext.Current.CancellationToken;
        await kit.CatalogAsync(new ImportRequest([v1], Depth: AiDepth.None));
        await using var db = kit.Catalog.Db();
        var rock = await db.Assets.Include(a => a.Tags).SingleAsync(a => a.Name == "rock", ct);
        var favorite = (await TagService.GetOrCreateAsync(db, ["favorite"], ct)).Single();
        rock.Tags.Add(new AssetTag { Tag = favorite, Origin = TagOrigin.User });
        rock.Name = "Big Rock";
        rock.UserEdited |= EditedFields.Name;
        (rock.Favorite, rock.Rating, rock.Notes) = (true, 4, "use for level 2 rocks");
        await db.SaveChangesAsync(ct);
        return (rock.SourceId, rock.Id);
    }

    [Fact]
    public async Task Renamed_package_version_replaces_the_source_with_counts()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var kit = await ImportKit.CreateAsync();
        var (v1, v2) = Packages(kit.Catalog.Dir);
        var (oldSource, _) = await CatalogFirstVersionAsync(kit, v1);

        var (session, job) = await kit.ImportAsync(new ImportRequest([v2], Depth: AiDepth.None));

        Assert.True(job.State == JobState.Completed, job.Error);
        Assert.Contains("\"replaced\":1", job.SummaryJson);
        // The import results show the counts ("Version update counts"); the unchanged texture is an update, not a duplicate.
        var results = (await kit.Results.LoadAsync(session, ct))!;
        var replaced = results.Sources.Single();
        Assert.Equal((oldSource, "Game Kit Controller", true, 2, 1, 1), (replaced.SourceId, replaced.Name, replaced.Replaced, replaced.Updated, replaced.Added, replaced.Removed));
        Assert.DoesNotContain(replaced.Skips, s => s.Kind == SkipKind.Duplicate);
        await using var db = kit.Catalog.Db();
        Assert.Equal(oldSource, (await db.Sources.SingleAsync(ct)).Id);
    }

    [Fact]
    public async Task Removed_asset_in_new_version()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var kit = await ImportKit.CreateAsync();
        var (v1, v2) = Packages(kit.Catalog.Dir);
        var (oldSource, rockId) = await CatalogFirstVersionAsync(kit, v1);
        await new SourceTagService(kit.Store).AddAsync(oldSource, ["low poly"], ct: ct);

        var (_, job) = await kit.ImportAsync(new ImportRequest([v2], Depth: AiDepth.None));

        Assert.True(job.State == JobState.Completed, job.Error);
        Assert.Contains("\"assets\":3", job.SummaryJson);
        Assert.Contains("\"replaced\":1", job.SummaryJson);
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
        Assert.Equal((true, 4, "use for level 2 rocks"), (rock.Favorite, rock.Rating, rock.Notes)); // user-owned fields kept
        Assert.Equal(48, rock.Image!.Width);                                  // the new version's content
        Assert.EndsWith("GKC 3.77c Asset Store.unitypackage", rock.PrimaryFile!.Location.Path);
        Assert.True(await db.Previews.AnyAsync(p => p.AssetId == rock.Id, ct));
        // Spec "New assets inherit": the new version's assets carry the source tag.
        Assert.All(assets, a => Assert.Contains(a.Tags, t => t.Tag.Name == "low poly"));
        Assert.Equal(0, await db.AllAssets.CountAsync(a => a.ImportSessionId != null, ct));
        Assert.Equal(0, await db.AllSources.CountAsync(s => s.ImportSessionId != null, ct));
    }

    /// <summary>Spec scenario "User declines replacement": an overlap without a store id replaces nothing.</summary>
    [Fact]
    public async Task Overlap_without_store_id_is_imported_as_a_new_source()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var kit = await ImportKit.CreateAsync();
        var (v1, v2) = Packages(kit.Catalog.Dir, withHeader: false); // GUID overlap only: 2 shared of 4 = 0.5
        var (oldSource, _) = await CatalogFirstVersionAsync(kit, v1);

        await kit.CatalogAsync(new ImportRequest([v2], Depth: AiDepth.None));

        await using var check = kit.Catalog.Db();
        Assert.Equal(2, await check.Sources.CountAsync(ct));
        Assert.Equal(["Big Rock", "grass", "old"], (await check.Assets.Where(a => a.SourceId == oldSource).Select(a => a.Name).ToListAsync(ct)).Order(StringComparer.OrdinalIgnoreCase));
        // The unchanged grass is a duplicate of the cataloged version, so the new source gets rock and fresh.
        Assert.Equal(["fresh", "rock"], (await check.Assets.Where(a => a.SourceId != oldSource).Select(a => a.Name).ToListAsync(ct)).Order(StringComparer.OrdinalIgnoreCase));
        Assert.All(await check.Sources.ToListAsync(ct), s => Assert.Null(s.ReplacesSourceId));
    }

    [Fact]
    public async Task Explicit_update_replaces_without_a_store_id()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var kit = await ImportKit.CreateAsync();
        var (v1, v2) = Packages(kit.Catalog.Dir, withHeader: false);
        var (oldSource, rockId) = await CatalogFirstVersionAsync(kit, v1);

        await kit.CatalogAsync(new ImportRequest([v2], Depth: AiDepth.None, ReplaceSourceId: oldSource));

        await using var db = kit.Catalog.Db();
        Assert.Equal(oldSource, (await db.Sources.SingleAsync(ct)).Id);
        Assert.Equal(["Big Rock", "fresh", "grass"], (await db.Assets.Select(a => a.Name).ToListAsync(ct)).Order(StringComparer.OrdinalIgnoreCase));
        Assert.True(await db.Assets.AnyAsync(a => a.Id == rockId, ct));
    }

    [Fact]
    public async Task Explicit_update_needs_a_single_input_and_an_existing_source()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var kit = await ImportKit.CreateAsync();
        var (v1, v2) = Packages(kit.Catalog.Dir);
        var (oldSource, _) = await CatalogFirstVersionAsync(kit, v1);

        await Assert.ThrowsAsync<ArgumentException>(() => kit.Stager.CreateSessionAsync(new ImportRequest([v1, v2], ReplaceSourceId: oldSource), ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => kit.Stager.CreateSessionAsync(new ImportRequest([v2], ReplaceSourceId: oldSource + 100), ct));
    }

    [Fact]
    public async Task Replacement_is_committed_before_ai_and_enriched_in_place()
    {
        var ct = TestContext.Current.CancellationToken;
        var chat = new SchemaAwareChatClient();
        await using var kit = await ImportKit.CreateAsync(chat);
        var (v1, v2) = Packages(kit.Catalog.Dir);
        var (oldSource, rockId) = await CatalogFirstVersionAsync(kit, v1);
        kit.Ai.Pause();
        var session = await kit.Imports.StartAsync(new ImportRequest([v2]), ct);
        await kit.WaitCommittedAsync(session.Id);

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
