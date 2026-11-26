namespace AssetManager.Processing.Export;

/// <summary>The files of a drag-out and the assets that could not be included.</summary>
public sealed record DragOutFiles(IReadOnlyList<string> Files, IReadOnlyList<ExportFailure> Unavailable)
{
    /// <summary>A message about the unavailable assets, or null when every asset is included.</summary>
    public string? Problem => Unavailable.Count == 0 ? null
        : $"{Unavailable.Count} of the dragged assets {(Unavailable.Count == 1 ? "is" : "are")} unavailable and left out: " +
          string.Join(", ", Unavailable.Take(5).Select(u => u.Name)) + (Unavailable.Count > 5 ? ", ..." : "") + ".";
}

/// <summary>
/// Which assets a drag-out carries and their files (design D7): the pressed card, or the whole selection when the
/// pressed card is selected. Files are copied flat into a new temporary folder per drag (containers extracted), since a
/// drop target can keep the previous drag's files open.
/// </summary>
public sealed class DragOutPlanner(AssetExporter exporter)
{
    public static IReadOnlyCollection<long> Targets(long pressed, IReadOnlyCollection<long> selection) =>
        selection.Contains(pressed) ? selection.ToList() : [pressed];

    public async Task<DragOutFiles> ResolveAsync(IReadOnlyCollection<long> assetIds, CancellationToken ct = default)
    {
        var result = await exporter.ExtractForClipboardAsync(assetIds, null, ct);
        return new DragOutFiles(result.Written, result.Unavailable);
    }
}
