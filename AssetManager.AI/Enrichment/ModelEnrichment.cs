using AssetManager.AI.Runtime;
using AssetManager.Core.Domain;
using AssetManager.Data;
using AssetManager.Processing.Handlers;
using AssetManager.Processing.Settings;
using Microsoft.EntityFrameworkCore;

namespace AssetManager.AI.Enrichment;

/// <summary>Re-renders an asset's previews with its (new) orientation and returns the fresh previews.</summary>
public interface IPreviewRerenderer
{
    Task<IReadOnlyList<PreviewBlob>> RerenderAsync(Asset asset, CancellationToken ct);
}

/// <summary>
/// For one 3D model: match textures to the materials that have none (design D20), judge the orientation from the probe
/// (when there is one and the user has not decided), re-render once when either changed, then run the item pass on
/// the textured, upright views. Without vision the orientation is marked "not verified", texture choices only become
/// suggestions, and the item is enriched from text.
/// </summary>
public static class ModelEnrichment
{
    public sealed record Result(ItemOutcome Item, OrientationDecision? Orientation)
    {
        public IReadOnlyList<TextureDecision> Textures { get; init; } = [];
    }

    public static async Task<Result> EnrichAsync(StructuredChat chat, ItemInput input, ItemContext context, AiModalities modalities,
        PreviewOptions options, IPreviewRerenderer rerenderer, double threshold = OrientationJudge.DefaultThreshold, CancellationToken ct = default,
        ITextureMatcher? textures = null, TextureMatchOptions? textureOptions = null)
    {
        var asset = input.Asset;
        OrientationDecision? decision = null;
        var previews = input.Previews;
        IReadOnlyList<TextureDecision> textureDecisions = [];
        if (textures is not null)
            textureDecisions = await TextureJudge.MatchAsync(chat, asset, textures, modalities, textureOptions ?? new TextureMatchOptions(), ct);
        var rerender = textureDecisions.Any(t => t.Attached);
        if (!modalities.Vision) OrientationJudge.MarkNotVerified(asset);
        else if (OrientationJudge.NeedsJudgment(asset, previews))
        {
            // The probe was rendered before any texture was matched; the shape is what counts for "up".
            var probe = previews.First(p => p.Kind == PreviewKind.OrientationProbe);
            decision = await OrientationJudge.JudgeAsync(chat, asset, probe, threshold, ct);
            OrientationJudge.Apply(asset, decision);
            rerender |= decision.Rerender;
        }
        // The description must be based on textured, upright views, so wait for the re-render.
        if (rerender) previews = await rerenderer.RerenderAsync(asset, ct);

        var request = ItemPass.Plan([input with { Previews = previews }], modalities).Single();
        var outcome = (await ItemPass.RunAsync(chat, request, context, options, ct)).Single();
        return new Result(outcome, decision) { Textures = textureDecisions };
    }
}

/// <summary>Saves the orientation, regenerates the previews through <see cref="PreviewService"/>, and reloads them.</summary>
public sealed class CatalogPreviewRerenderer(CatalogStore catalog, PreviewService previews) : IPreviewRerenderer
{
    public async Task<IReadOnlyList<PreviewBlob>> RerenderAsync(Asset asset, CancellationToken ct)
    {
        await using (var db = catalog.CreateDbContext())
        {
            // AllAssets: during an import the asset is still staged (hidden from the catalog query filter).
            var row = await db.AllAssets.FirstAsync(a => a.Id == asset.Id, ct);
            row.Model = asset.Model;
            row.OrientationVerified = asset.OrientationVerified;
            row.OrientationUncertain = asset.OrientationUncertain;
            await db.SaveChangesAsync(ct);
        }
        var result = await previews.RegenerateAsync([asset.Id], ct: ct);
        if (result.Skipped.Count > 0) throw new InvalidOperationException($"Re-render failed: {result.Skipped[0].Reason}");
        await using var read = catalog.CreateDbContext();
        // The caller saves its (tracked) asset later: give it the metadata the re-render recomputed (missing textures…).
        var fresh = await read.AllAssets.AsNoTracking().FirstAsync(a => a.Id == asset.Id, ct);
        asset.Model = fresh.Model;
        asset.TexturesMissing = fresh.TexturesMissing;
        asset.ScaleAnomaly = fresh.ScaleAnomaly;
        return await read.Previews.AsNoTracking().Where(p => p.AssetId == asset.Id).ToListAsync(ct);
    }
}
