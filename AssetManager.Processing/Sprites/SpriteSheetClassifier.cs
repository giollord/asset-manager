using AssetManager.Core.Domain;
using AssetManager.Processing.Handlers;
using AssetManager.Processing.Settings;
using ImageMagick;

namespace AssetManager.Processing.Sprites;

/// <summary>
/// The sprite-sheet decision (design D3, spec "Sprite sheet detection"): slicing data first, then the grid detector;
/// the AI's answer is only a candidate for the detector. The type becomes sprite sheet only with frames, and the
/// user's type or grid edits are never changed.
/// </summary>
public static class SpriteSheetClassifier
{
    public static bool IsImage(Asset asset) => asset.Type is AssetType.Image or AssetType.Texture or AssetType.SpriteSheet;

    public static void Decide(Asset asset, SpriteSheetMetadata? slicing, GridResult grid)
    {
        if (asset.UserEdited.HasFlag(EditedFields.Grid)) return;
        var sheet = slicing is { FrameCount: >= 2 } ? slicing : grid.Sheet;
        asset.SpriteSheet = sheet;
        // A texture is only "uncertain" when the pixels show several sprites, not on the AI's word alone.
        asset.GridUncertain = sheet is null && grid.Uncertain && (grid.SeveralSprites || asset.Type != AssetType.Texture);
        if (asset.UserEdited.HasFlag(EditedFields.Type)) return;
        if (sheet is not null) asset.Type = AssetType.SpriteSheet;
        else if (asset.Type == AssetType.SpriteSheet) asset.Type = AssetType.Image;
    }

    /// <summary>Runs slicing lookup and grid detection on a loaded image and decides.</summary>
    public static void Detect(Asset asset, string localPath, MagickImage image, PreviewOptions options, SpriteSheetHint? hint = null)
    {
        if (asset.UserEdited.HasFlag(EditedFields.Grid)) return;
        var slicing = SlicingReaders.Find(localPath, (int)image.Width, (int)image.Height);
        var grid = slicing is { FrameCount: >= 2 } ? GridResult.None : GridDetector.Detect(image, options.GridConfidenceThreshold, hint);
        Decide(asset, slicing, grid);
    }

    /// <summary>
    /// After the item pass, when the AI thinks an image is a sprite sheet that detection did not confirm: detect again
    /// on the original with the AI's frame counts as an extra candidate. Returns false when the original is unavailable.
    /// </summary>
    public static async Task<bool> RedetectWithHintAsync(ILocationResolver resolver, Asset asset, SpriteSheetHint hint, PreviewOptions options, CancellationToken ct)
    {
        if (!hint.Likely || !IsImage(asset) || asset.SpriteSheet is not null || asset.UserEdited.HasFlag(EditedFields.Grid) || asset.PrimaryFile is null)
            return false;
        try
        {
            using var file = await resolver.ResolveAsync(asset.PrimaryFile, ct);
            using var image = ImageHandler.Load(file.Path, Path.GetExtension(asset.PrimaryFile.FileName).ToLowerInvariant());
            Detect(asset, file.Path, image, options, hint);
            return true;
        }
        catch (Exception e) when (e is OriginalUnavailableException or IOException or UnauthorizedAccessException or MagickException
                                      or Containers.ContainerReadException)
        {
            return false;
        }
    }
}
