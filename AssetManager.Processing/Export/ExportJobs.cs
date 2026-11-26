using AssetManager.Core.Jobs;
using AssetManager.Core.Platform;
using AssetManager.Processing.Jobs;

namespace AssetManager.Processing.Export;

/// <summary>Payload of "assets.extract": the conflict choices were made in the UI before the job was queued.</summary>
public sealed record ExtractPayload(
    long[] AssetIds, string TargetFolder, bool PreserveStructure, ConflictChoice DefaultChoice = ConflictChoice.Rename,
    Dictionary<string, ConflictChoice>? Choices = null, bool UnityMeta = false, bool IncludeReferences = false, string[]? SkipGuids = null);

/// <summary>Extracts assets to a folder (spec "Extract to folder"); unavailable assets become failed items.</summary>
public sealed class ExtractAssetsJob(AssetExporter exporter) : IJobHandler
{
    public string Type => JobTypes.Extract;

    public JobPool Pool => JobPool.Io;

    public async Task RunAsync(JobContext context, CancellationToken ct)
    {
        var p = context.Payload<ExtractPayload>();
        var ids = p.AssetIds.Where(id => context.Includes(id.ToString())).ToArray();
        context.Progress.SetStage("Extracting", 0, 1, ids.Length);
        var done = 0;
        var result = await exporter.ExtractAsync(ids, new ExportOptions(p.TargetFolder, p.PreserveStructure, p.DefaultChoice, p.Choices, p.UnityMeta, p.IncludeReferences,
                p.SkipGuids is { Length: > 0 } skip ? skip.ToHashSet(StringComparer.OrdinalIgnoreCase) : null),
            new SyncProgress(n =>
            {
                context.Progress.Advance(n - done);
                done = n;
            }), ct);
        foreach (var f in result.Unavailable) context.Progress.Fail(f.AssetId.ToString(), f.Name, f.Reason);
        context.Progress.SetSummary(new { assets = result.Exported, files = result.Written.Count, metaFiles = result.MetaWritten.Count, referencedFiles = result.Dependencies, notes = result.Notes, skippedFiles = result.SkippedFiles, unavailable = result.Unavailable.Count, folder = p.TargetFolder });
    }
}

/// <summary>
/// Copies assets to the clipboard as files (spec "Copy to clipboard as files"): writes them to a temporary folder, then
/// puts the files that were written on the clipboard. Unavailable assets become failed items.
/// </summary>
public sealed class CopyAssetsJob(AssetExporter exporter, IClipboardFileService clipboard) : IJobHandler
{
    public string Type => JobTypes.Copy;

    public JobPool Pool => JobPool.Io;

    public async Task RunAsync(JobContext context, CancellationToken ct)
    {
        var ids = (context.Payload<AssetsPayload>().AssetIds ?? []).Where(id => context.Includes(id.ToString())).ToArray();
        context.Progress.SetStage("Preparing files", 0, 1, ids.Length);
        var done = 0;
        var result = await exporter.ExtractForClipboardAsync(ids, new SyncProgress(n =>
        {
            context.Progress.Advance(n - done);
            done = n;
        }), ct);
        foreach (var f in result.Unavailable) context.Progress.Fail(f.AssetId.ToString(), f.Name, f.Reason);
        if (result.Written.Count > 0) await clipboard.SetFilesAsync(result.Written, ct);
        context.Progress.SetSummary(new { assets = result.Exported, files = result.Written.Count, unavailable = result.Unavailable.Count });
    }
}
