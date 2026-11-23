using AssetManager.Core.Domain;
using AssetManager.Processing.Settings;

namespace AssetManager.Processing.Handlers;

/// <summary>Catalogs any other file with basic information only; the UI shows the type placeholder.</summary>
public sealed class FallbackHandler : AssetTypeHandlerBase
{
    public override string Name => "fallback";

    public override bool CanHandle(AssetType type) => true;

    public override Task ExtractMetadataAsync(HandlerItem item, PreviewOptions options, CancellationToken ct = default) => Task.CompletedTask;

    public override Task<IReadOnlyList<PreviewOutput>> GeneratePreviewsAsync(HandlerItem item, PreviewOptions options, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<PreviewOutput>>([]);
}
