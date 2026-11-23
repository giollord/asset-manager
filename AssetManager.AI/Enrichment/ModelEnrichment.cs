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
/// For one 3D model: judge the orientation from the probe (when there is one and the user has not decided), re-render
/// when the AI picked a different rotation, then run the item pass on the upright views. Without vision the
/// orientation is marked "not verified" and the item is enriched from text.
/// </summary>
public static class ModelEnrichment
{
    public sealed record Result(ItemOutcome Item, OrientationDecision? Orientation);

    public static async Task<Result> EnrichAsync(StructuredChat chat, ItemInput input, ItemContext context, AiModalities modalities,
        PreviewOptions options, IPreviewRerenderer rerenderer, double threshold = OrientationJudge.DefaultThreshold, CancellationToken ct = default)
    {
        var asset = input.Asset;
        OrientationDecision? decision = null;
        var previews = input.Previews;
        if (!modalities.Vision) OrientationJudge.MarkNotVerified(asset);
        else if (OrientationJudge.NeedsJudgment(asset, previews))
        {
            var probe = previews.First(p => p.Kind == PreviewKind.OrientationProbe);
            decision = await OrientationJudge.JudgeAsync(chat, asset, probe, threshold, ct);
            OrientationJudge.Apply(asset, decision);
            // The description must be based on upright views, so wait for the re-render.
            if (decision.Rerender) previews = await rerenderer.RerenderAsync(asset, ct);
        }

        var request = ItemPass.Plan([input with { Previews = previews }], modalities).Single();
        var outcome = (await ItemPass.RunAsync(chat, request, context, options, ct)).Single();
        return new Result(outcome, decision);
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
        return await read.Previews.AsNoTracking().Where(p => p.AssetId == asset.Id).ToListAsync(ct);
    }
}
