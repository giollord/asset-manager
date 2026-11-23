using AssetManager.AI.Import;
using AssetManager.Core.Jobs;
using AssetManager.Processing.Jobs;

namespace AssetManager.AI.Enrichment;

/// <summary>
/// "ai.describe-collection": writes a collection's description with the AI (spec "Tag-based collections"). It runs in
/// the AI pool, so it goes through the AI queue's pause gate like every other request.
/// </summary>
public sealed class DescribeCollectionJob(ImportEnricher enricher, CollectionDescriber describer) : IJobHandler
{
    public string Type => JobTypes.DescribeCollection;

    public JobPool Pool => JobPool.Ai;

    public async Task RunAsync(JobContext context, CancellationToken ct)
    {
        var id = context.Payload<CollectionPayload>().CollectionId;
        context.Progress.SetStage("Describing the collection", 0, 1, 1);
        await enricher.WaitForModelAsync(context.Progress, ct);
        if (enricher.Chat() is not { } client)
            throw new InvalidOperationException("No AI model is running. Install or start a model in Settings to describe collections.");
        context.Progress.SetCurrentItem("Asking the AI model...");
        var description = await describer.DescribeAsync(new StructuredChat(client), id, enricher.Modalities().Vision, ct);
        context.Progress.Advance(1);
        context.Progress.SetSummary(new { characters = description.Length });
    }
}
