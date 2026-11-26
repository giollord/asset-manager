using AssetManager.Core.Domain;
using AssetManager.Data.Services;

namespace AssetManager.Tests.Data;

/// <summary>Spec "Source tag consensus candidates" (design D2).</summary>
public class ConsensusCandidateTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<ConsensusInput> FindAsync(TestCatalog c, long sourceId, double share = 0.6, int minAssets = 5)
    {
        await using var db = c.Db();
        return await ConsensusCandidates.FindAsync(db, sourceId, share, minAssets, Ct);
    }

    [Fact]
    public async Task Share_threshold_decides()
    {
        await using var c = await TestCatalog.CreateAsync();
        // 10 game assets: 3 tagged "medieval" (30 %), 8 with the style or tag "pixel art" (80 %); 1 non-game banner tagged both.
        var src = await TestData.SourceAsync(c, 11, make: i => new Asset
        {
            Name = $"a{i}", Type = AssetType.Image, IsGameAsset = i < 10,
            Style = i is >= 4 and < 10 ? ["Pixel Art"] : [],
        });
        var ids = src.Assets.Select(a => a.Id).ToList();
        var tags = new TagService(c.Store);
        await tags.AddTagsAsync(ids.Take(3).Append(ids[10]).ToList(), ["medieval"], TagOrigin.AI, Ct);
        await tags.AddTagsAsync([ids[0], ids[1], ids[10]], ["pixel art"], TagOrigin.AI, Ct);

        var input = await FindAsync(c, src.Id);

        Assert.Equal(10, input.GameAssets);
        var candidate = Assert.Single(input.Candidates);
        Assert.Equal(("pixel art", 8, 0.8), (candidate.Name, candidate.Count, candidate.Share));
        Assert.True(candidate.FromTag && candidate.FromStyle);
        Assert.Equal(new ConsensusTypeShare(AssetType.Image, 8, 10), Assert.Single(candidate.PerType));
        // Below 30 %: a threshold of 0.3 makes "medieval" a candidate too.
        Assert.Contains((await FindAsync(c, src.Id, share: 0.3)).Candidates, x => x.Name == "medieval");
    }

    [Fact]
    public async Task Small_sources_and_existing_source_tags_give_no_candidates()
    {
        await using var c = await TestCatalog.CreateAsync();
        var small = await TestData.SourceAsync(c, 4, make: i => new Asset { Name = $"s{i}", Style = ["low poly"] });
        Assert.Empty((await FindAsync(c, small.Id)).Candidates);

        var big = await TestData.SourceAsync(c, 6, make: i => new Asset { Name = $"b{i}", Style = ["low poly"] });
        Assert.Single((await FindAsync(c, big.Id)).Candidates);
        await new SourceTagService(c.Store).AddAsync(big.Id, ["low poly"], TagOrigin.AI, Ct);
        Assert.Empty((await FindAsync(c, big.Id)).Candidates);
    }

    [Fact]
    public async Task One_type_can_make_a_candidate()
    {
        await using var c = await TestCatalog.CreateAsync();
        // 6 models all low poly (100 % of models) plus 14 skyboxes without it: 30 % overall.
        var src = await TestData.SourceAsync(c, 20, make: i => i < 6
            ? new Asset { Name = $"m{i}", Type = AssetType.Model3D, Style = ["low poly"] }
            : new Asset { Name = $"t{i}", Type = AssetType.Texture });
        var candidate = Assert.Single((await FindAsync(c, src.Id)).Candidates);
        Assert.Equal(0.3, candidate.Share, 3);
        Assert.Equal(1.0, candidate.PerType.Single(t => t.Type == AssetType.Model3D).Share);
    }
}
