using AssetManager.AI.Enrichment;
using AssetManager.AI.Import;
using AssetManager.Core.Domain;
using AssetManager.Core.Jobs;
using AssetManager.Core.Settings;
using AssetManager.Data.Services;
using AssetManager.Processing.Import;
using AssetManager.Tests.Data;
using AssetManager.Tests.Import;
using ImageMagick;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace AssetManager.Tests.Ai;

/// <summary>Spec "Source tag consensus decision" and "When consensus runs" (design D2).</summary>
[Collection(AiModelCollection.Name)]
public class ConsensusPassTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A folder pack of <paramref name="count"/> small images.</summary>
    private static string Pack(ImportKit kit, string name, int count)
    {
        string? first = null;
        for (var i = 0; i < count; i++)
            first ??= kit.Catalog.Dir.Write($"{name}/img{i}.png", ImportStagingTests.Png(new MagickColor((byte)(i * 30), 90, 160), 24));
        for (var i = 1; i < count; i++) kit.Catalog.Dir.Write($"{name}/img{i}.png", ImportStagingTests.Png(new MagickColor((byte)(i * 30), 90, 160), 24));
        return Path.GetDirectoryName(first)!;
    }

    private static async Task<List<(string Name, TagOrigin Origin)>> SourceTagsAsync(ImportKit kit)
    {
        await using var db = kit.Catalog.Db();
        return (await db.SourceTags.Select(t => new { t.Tag.Name, t.Origin }).ToListAsync(Ct)).Select(t => (t.Name, t.Origin)).OrderBy(t => t.Name).ToList();
    }

    [Fact]
    public async Task Import_runs_consensus_after_the_item_pass_and_applies_only_accepted_candidates()
    {
        // The fake gives every item the tag "ai-tag" and the style "stylized"; the pack pass adds "pack-tag" to all.
        var client = new SchemaAwareChatClient { AcceptConsensus = c => c == "stylized" };
        await using var kit = await ImportKit.CreateAsync(client);

        await kit.CatalogAsync(new ImportRequest([Pack(kit, "Stylized", 6)], Depth: AiDepth.Full));

        var prompt = Assert.Single(client.Prompts, p => p.Contains("Decide which of these tags describe a whole game asset pack"));
        Assert.Contains("Candidate \"stylized\" (style): 6 of 6 game assets (100", prompt);
        Assert.Contains("Candidate \"ai-tag\" (tag)", prompt);
        Assert.Equal([("stylized", TagOrigin.AI)], await SourceTagsAsync(kit)); // rejected candidates change nothing
        await using var db = kit.Catalog.Db();
        Assert.Equal(6, await db.AssetTags.CountAsync(t => t.Tag.Name == "stylized" && t.Origin == TagOrigin.Source, Ct));
    }

    [Fact]
    public async Task Small_sources_and_a_disabled_step_send_no_request()
    {
        var client = new SchemaAwareChatClient();
        await using var kit = await ImportKit.CreateAsync(client);
        await kit.CatalogAsync(new ImportRequest([Pack(kit, "Tiny", 3)], Depth: AiDepth.Full)); // below the minimum of 5
        kit.AppSettings.ConsensusEnabled = false;
        await kit.CatalogAsync(new ImportRequest([Pack(kit, "Off", 6)], Depth: AiDepth.Full));

        Assert.DoesNotContain(client.Prompts, p => p.Contains("Decide which of these tags"));
        Assert.Empty(await SourceTagsAsync(kit));
    }

    [Fact]
    public async Task Suggest_source_tags_job_runs_on_request()
    {
        var client = new SchemaAwareChatClient();
        await using var kit = await ImportKit.CreateAsync(client);
        kit.AppSettings.ConsensusEnabled = false; // the automatic step is off; the manual one still runs
        await kit.CatalogAsync(new ImportRequest([Pack(kit, "Manual", 5)], Depth: AiDepth.Full));
        Assert.Empty(await SourceTagsAsync(kit));
        long sourceId;
        await using (var db = kit.Catalog.Db()) sourceId = await db.Sources.Select(s => s.Id).SingleAsync(Ct);

        var job = await kit.Jobs.WaitAsync(await kit.Jobs.EnqueueAsync(ConsensusJob.JobType, "Suggest", new SourcesPayload([sourceId]), ct: Ct), Ct);

        Assert.True(job.State == JobState.Completed, job.Error);
        Assert.Contains(await SourceTagsAsync(kit), t => t == ("stylized", TagOrigin.AI));
    }

    [Fact]
    public async Task Answer_must_decide_every_candidate()
    {
        await using var c = await TestCatalog.CreateAsync();
        var src = await TestData.SourceAsync(c, 5, make: i => new Asset { Name = $"a{i}", Style = ["low poly"] });
        var client = new FakeChatClient(
            """{"decisions":[{"tag":"lowpoly","apply_to_source":true,"reason":"x"}]}""",
            """{"decisions":[{"tag":"low poly","apply_to_source":false,"reason":"Mixed pack."}]}""");

        var accepted = await ConsensusPass.RunForSourceAsync(c.Store, new StructuredChat(client), src.Id, new AppSettings(), vision: false, Ct);

        Assert.Empty(accepted); // the corrected answer rejects it
        Assert.Equal(2, client.Requests.Count);
        Assert.Contains("unknown candidates", client.Requests[1].Messages.Last().Text);
        await using var db = c.Db();
        Assert.Equal(0, await db.SourceTags.CountAsync(Ct));
    }

    /// <summary>Spec "Mixed pack" against the real model: low-poly props plus realistic photos; "low poly" must not cover the source.</summary>
    [AiModelFact]
    public async Task Real_model_rejects_low_poly_for_a_mixed_pack()
    {
        await using var c = await TestCatalog.CreateAsync();
        var src = await TestData.SourceAsync(c, 10, s => { s.DisplayName = "Props and Skies"; s.Description = "Low-poly props and realistic HDR sky photos."; },
            i => i < 6
                ? new Asset { Name = $"prop{i}", Type = AssetType.Model3D, Style = ["low poly"], Description = "A flat-shaded low-poly crate." }
                : new Asset { Name = $"sky{i}", Type = AssetType.Texture, Style = ["realistic"], Description = "A realistic HDR photo of a cloudy sky." });
        await using (var db = c.Db())
        {
            foreach (var (a, i) in src.Assets.Select((a, i) => (a, i)))
                db.Previews.Add(new PreviewBlob { AssetId = a.Id, Kind = PreviewKind.Thumbnail, Mime = "image/png", Bytes = i < 6 ? LowPoly(i) : Sky(i) });
            await db.SaveChangesAsync(Ct);
        }
        await using var server = await AiTestServer.StartAsync(vision: AiModelFactAttribute.Mmproj is not null);

        var accepted = await ConsensusPass.RunForSourceAsync(c.Store, new StructuredChat(server.Client), src.Id, new AppSettings(), server.Vision, Ct);

        Assert.DoesNotContain("low poly", accepted);
    }

    private static byte[] LowPoly(int i)
    {
        using var img = new MagickImage(MagickColors.LightGray, 128, 128);
        new ImageMagick.Drawing.Drawables().FillColor(new MagickColor((byte)(120 + i * 20), 80, 40))
            .Polygon(new PointD(20, 100), new PointD(64, 20 + i * 3), new PointD(108, 100)).Draw(img);
        return img.ToByteArray(MagickFormat.Png);
    }

    private static byte[] Sky(int i)
    {
        using var img = new MagickImage($"gradient:skyblue-white", 128, 128);
        img.AddNoise(NoiseType.Gaussian, 0.3 + i * 0.05);
        img.Blur(0, 2);
        return img.ToByteArray(MagickFormat.Png);
    }
}
