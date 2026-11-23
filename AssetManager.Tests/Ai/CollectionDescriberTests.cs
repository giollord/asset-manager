using AssetManager.AI.Enrichment;
using AssetManager.Core.Domain;
using AssetManager.Data.Services;
using AssetManager.Tests.Data;
using ImageMagick;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace AssetManager.Tests.Ai;

public class CollectionDescriberTests
{
    [Fact]
    public async Task Describe_collection_from_a_representative_sample()
    {
        await using var c = await TestCatalog.CreateAsync();
        using var thumb = new MagickImage(MagickColors.ForestGreen, 64, 64);
        var thumbBytes = thumb.ToByteArray(MagickFormat.WebP);
        var ids = new List<long>();
        await using (var db = c.Db())
        {
            // 300 assets: 250 tree models from one pack, 40 textures from another, 10 sounds from a third.
            var packs = new[] { new Source { DisplayName = "Trees" }, new Source { DisplayName = "Bark" }, new Source { DisplayName = "Forest SFX" } };
            for (var i = 0; i < 300; i++)
            {
                var (pack, type, name) = i switch
                {
                    < 250 => (packs[0], AssetType.Model3D, $"Tree_{i:000}"),
                    < 290 => (packs[1], AssetType.Texture, $"Bark_{i:000}"),
                    _ => (packs[2], AssetType.Audio, $"Birds_{i:000}"),
                };
                var asset = new Asset { Name = name, Type = type, Description = i % 2 == 0 ? $"A {type} for a forest." : null };
                pack.Assets.Add(asset);
            }
            db.AddRange(packs);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            ids.AddRange(packs.SelectMany(p => p.Assets).Select(a => a.Id));
            db.Previews.AddRange(ids.Select(id => new PreviewBlob { AssetId = id, Kind = PreviewKind.Thumbnail, Mime = "image/webp", Bytes = thumbBytes }));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        var tags = new TagService(c.Store);
        await tags.AddTagsAsync(ids, ["forest"], TagOrigin.User, TestContext.Current.CancellationToken);
        await tags.AddTagsAsync(ids.Take(250).ToList(), ["tree"], TagOrigin.AI, TestContext.Current.CancellationToken);
        var collection = await new CollectionService(c.Store).SaveAsync(new Collection { Name = "Forest", IncludeTags = ["forest"] }, TestContext.Current.CancellationToken);
        var client = new FakeChatClient("""{"description":"Stylized forest content: trees, bark textures and ambient bird sounds."}""");

        var description = await new CollectionDescriber(c.Store).DescribeAsync(new StructuredChat(client), collection.Id, vision: true, TestContext.Current.CancellationToken);

        var prompt = client.Requests.Single().Messages[^1];
        Assert.Contains("It has 300 assets from 3 sources: 250 Model3D, 40 Texture, 10 Audio.", prompt.Text);
        Assert.Contains("Most common tags: forest (300), tree (250).", prompt.Text);
        Assert.Contains("A representative sample of 40 assets:", prompt.Text);
        Assert.Contains("- Birds_", prompt.Text); // the small group is represented
        Assert.Contains("- Bark_", prompt.Text);
        Assert.Single(prompt.Contents.OfType<DataContent>());
        Assert.Equal("Stylized forest content: trees, bark textures and ambient bird sounds.", description);
        await using var read = c.Db();
        var saved = await read.Collections.SingleAsync(x => x.Id == collection.Id, TestContext.Current.CancellationToken);
        Assert.Equal((description, TextOrigin.AI), (saved.Description, saved.DescriptionOrigin));
    }

    [Fact]
    public void Sample_spreads_over_groups_and_prefers_described_assets()
    {
        var members = Enumerable.Range(1, 100).Select(i => new CollectionMember(i, $"m{i}", AssetType.Model3D, 1, i > 90 ? "described" : null))
            .Append(new CollectionMember(200, "s", AssetType.Audio, 2, null)).ToList();

        var sample = CollectionDescriber.PickSample(members, 10);

        Assert.Equal(10, sample.Count);
        Assert.Contains(sample, m => m.Id == 200);
        Assert.Equal(9, sample.Count(m => m.Description is not null));
        Assert.Equal(members.Count, CollectionDescriber.PickSample(members, 500).Count);
    }
}
