using AssetManager.Core.Domain;
using AssetManager.Processing.Models;
using AssetManager.Processing.Settings;
using Assimp;
using ImageMagick;

namespace AssetManager.Processing.Handlers;

/// <summary>
/// 3D models via AssimpNetter: stats, scale analysis, metadata-based upright orientation and texture resolution in .NET;
/// the multi-angle previews come from the WebView renderer (<see cref="IModelRenderer"/>) fed with an in-memory GLB.
/// Animation clips (<see cref="AssetType.Animation"/>) get one animated WebP from a single angle instead.
/// </summary>
public sealed class ModelHandler(IModelRenderer? renderer = null) : AssetTypeHandlerBase
{
    /// <summary>Textures larger than this are downscaled before rendering previews.</summary>
    private const int MaxRenderTexture = 1024;

    public override string Name => "model";

    public override bool CanHandle(AssetType type) => type is AssetType.Model3D or AssetType.Animation;

    public override Task ExtractMetadataAsync(HandlerItem item, PreviewOptions options, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            using var model = ModelAnalyzer.Load(item.PrimaryPath);
            var previous = item.Asset.Model?.Orientation;
            var keep = previous is { DecidedBy: OrientationDecidedBy.User or OrientationDecidedBy.AI } ? previous : null;
            var (_, missing, _) = ResolveTextures(model.Scene, item, includeBytes: false);
            var meta = ModelAnalyzer.Describe(model.Scene, item.Extension, options, keep) with
            {
                MissingTextures = missing,
                ProbeRanking = OrientationProbe.Rank(model.Scene).ToList(),
                TextureSuggestions = item.Asset.Model?.TextureSuggestions ?? [],
            };
            item.Asset.Model = meta;
            item.Asset.ScaleAnomaly = meta.Transform.Anomaly;
            item.Asset.TexturesMissing = missing.Count > 0;
            item.Asset.OrientationVerified = meta.Orientation.Verified;
            item.Asset.OrientationUncertain = meta.Orientation.Uncertain;
            // A model file that holds a motion clip (e.g. a Mixamo download with its stand-in character) is an animation.
            if (item.Asset.Type == AssetType.Model3D && !item.Asset.UserEdited.HasFlag(EditedFields.Type)
                && ModelAnalyzer.LooksLikeAnimationClip(meta, item.Asset.PrimaryFile is { RelativePath.Length: > 0 } f ? f.RelativePath : item.PrimaryPath))
                item.Asset.Type = AssetType.Animation;
        }, ct);

    public override async Task<IReadOnlyList<PreviewOutput>> GeneratePreviewsAsync(HandlerItem item, PreviewOptions options, CancellationToken ct = default)
    {
        if (renderer is not { IsAvailable: true }) throw new RendererUnavailableException();
        if (item.Asset.Type == AssetType.Animation) return await AnimationPreviewsAsync(item, options, ct);
        var request = await Task.Run(() => BuildRenderRequest(item, options), ct);
        var views = await renderer.RenderAsync(request, ct);
        var outputs = views.Select(v => new PreviewOutput(PreviewKind.ModelView, v.Index, "image/webp", v.Width, v.Height, v.Webp)).ToList();
        var primary = views.FirstOrDefault(v => v.Index == options.PrimaryViewIndex) ?? views.FirstOrDefault();
        if (primary is not null) outputs.Add(new PreviewOutput(PreviewKind.Thumbnail, 0, "image/webp", primary.Width, primary.Height, primary.Webp));

        // Without usable up-axis metadata the AI decides the orientation from the probe sheet.
        if (item.Asset.Model is { Transform.SourceUpAxis: null, Orientation.DecidedBy: not OrientationDecidedBy.User })
        {
            var probe = await renderer.RenderProbeAsync(await Task.Run(() => BuildProbeRequest(item, options), ct), ct);
            outputs.Add(new PreviewOutput(PreviewKind.OrientationProbe, 0, "image/webp", probe.Width, probe.Height, probe.Webp));
        }
        return outputs;
    }

    /// <summary>
    /// The clip as an animated WebP from the three-quarter view (<see cref="PreviewKind.Animation"/>), and its middle
    /// frame as the thumbnail. Clips are not orientation-judged: the probe and the angle views are skipped.
    /// </summary>
    private async Task<IReadOnlyList<PreviewOutput>> AnimationPreviewsAsync(HandlerItem item, PreviewOptions options, CancellationToken ct)
    {
        var model = await Task.Run(() => BuildRenderRequest(item, options, animated: true), ct);
        var frames = await renderer!.RenderAnimationAsync(new AnimationRenderRequest(
            model.Glb, model.Textures, model.Orientation, options.AnimationSize, options.AnimationFps, options.AnimationMaxSeconds, options.AnimationQuality)
        { NeutralMaterials = model.NeutralMaterials }, ct);
        if (frames.Frames.Count == 0) throw new InvalidDataException("The clip has no frames.");
        var webp = await Task.Run(() => AnimatedWebp.Encode(frames, options.AnimationQuality), ct);
        return
        [
            new PreviewOutput(PreviewKind.Animation, 0, "image/webp", frames.Width, frames.Height, webp),
            new PreviewOutput(PreviewKind.Thumbnail, 0, "image/webp", frames.Width, frames.Height, frames.Frames[frames.Frames.Count / 2]),
        ];
    }

    /// <summary>The GLB, the resolved textures and the view setup for this model.</summary>
    /// <param name="animated">Include the skin and clips (animation previews, the details viewer).</param>
    public static ModelRenderRequest BuildRenderRequest(HandlerItem item, PreviewOptions options, bool animated = false)
    {
        using var model = ModelAnalyzer.Load(item.PrimaryPath);
        var (textures, _, neutral) = ResolveTextures(model.Scene, item, includeBytes: true);
        return new ModelRenderRequest(
            ModelAnalyzer.ToGlb(model.Scene, animated), textures, item.Asset.Model?.Orientation ?? new ModelOrientation(),
            options.RenderSize, options.Elevations, options.AzimuthsPerRing, options.RenderQuality) { NeutralMaterials = neutral };
    }

    public static ProbeRequest BuildProbeRequest(HandlerItem item, PreviewOptions options)
    {
        using var model = ModelAnalyzer.Load(item.PrimaryPath);
        var (textures, _, neutral) = ResolveTextures(model.Scene, item, includeBytes: true);
        return new ProbeRequest(ModelAnalyzer.ToGlb(model.Scene), textures, options.ProbeTileSize, options.RenderQuality) { NeutralMaterials = neutral };
    }

    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".tga", ".psd", ".tif", ".tiff", ".bmp", ".dds", ".webp", ".exr"];

    /// <summary>Whether a file extension (with the dot) is an image a texture can be read from.</summary>
    internal static bool IsTextureExtension(string extension) => ImageExtensions.Contains(extension.ToLowerInvariant());

    private static readonly (TextureType Type, string Slot)[] Slots =
    [
        (TextureType.BaseColor, "map"), (TextureType.Diffuse, "map"),
        (TextureType.NormalCamera, "normalMap"), (TextureType.Normals, "normalMap"),
        (TextureType.Emissive, "emissiveMap"),
    ];

    /// <summary>
    /// Finds the texture of every material slot: embedded in the file, among the asset's companions (explicit
    /// material/slot first, then by file name), or next to the model file. Unresolved references are "missing".
    /// </summary>
    public static (List<RenderTexture> Textures, List<string> Missing, List<string> NeutralMaterials) ResolveTextures(
        Scene scene, HandlerItem item, bool includeBytes)
    {
        var textures = new List<RenderTexture>();
        var missing = new List<string>();
        var neutral = new List<string>();
        var modelDir = Path.GetDirectoryName(item.PrimaryPath) ?? "";
        var byName = item.Companions
            .GroupBy(c => Path.GetFileName(c.File.FileName.Length > 0 ? c.File.FileName : c.LocalPath), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().LocalPath, StringComparer.OrdinalIgnoreCase);

        foreach (var material in scene.Materials)
        {
            var name = material.Name ?? "";
            foreach (var (type, slot) in Slots)
            {
                if (textures.Any(t => t.Material == name && t.Slot == slot)) continue;
                var explicitCompanion = item.Companions.FirstOrDefault(c =>
                    string.Equals(c.Material, name, StringComparison.OrdinalIgnoreCase) && c.Slot == slot);
                if (explicitCompanion is not null)
                {
                    Add(name, slot, () => File.ReadAllBytes(explicitCompanion.LocalPath));
                    continue;
                }
                if (!material.GetMaterialTexture(type, 0, out var ts) || string.IsNullOrWhiteSpace(ts.FilePath)) continue;
                var reference = ts.FilePath;

                var embedded = reference.StartsWith('*') && int.TryParse(reference.AsSpan(1), out var idx) && idx < scene.TextureCount
                    ? scene.Textures[idx]
                    : scene.GetEmbeddedTexture(reference);
                if (embedded is { HasCompressedData: true })
                {
                    Add(name, slot, () => embedded.CompressedData);
                    continue;
                }

                var file = Path.GetFileName(reference.Replace('\\', '/'));
                if (byName.TryGetValue(file, out var companionPath))
                {
                    Add(name, slot, () => File.ReadAllBytes(companionPath));
                    continue;
                }
                var beside = new[] { Path.Combine(modelDir, reference), Path.Combine(modelDir, file) }.FirstOrDefault(File.Exists);
                if (beside is not null)
                {
                    Add(name, slot, () => File.ReadAllBytes(beside));
                    continue;
                }

                // Packs often ship the texture in another format than the model references (Bear.jpg → Bear.png).
                var stem = Path.GetFileNameWithoutExtension(file);
                var other = ImageExtensions.Select(e => stem + e).Select(n => byName.GetValueOrDefault(n)).FirstOrDefault(p => p is not null)
                    ?? ImageExtensions.Select(e => Path.Combine(modelDir, stem + e)).FirstOrDefault(File.Exists);
                if (other is not null)
                {
                    Add(name, slot, () => File.ReadAllBytes(other));
                    continue;
                }
                if (!missing.Contains(file, StringComparer.OrdinalIgnoreCase)) missing.Add(file);
                if (slot == "map" && !neutral.Contains(name)) neutral.Add(name);
            }
        }

        // Companions claimed by name only (no material reference in the file): apply a base-color texture
        // to every material that has none.
        var fallback = item.Companions.Where(c => c.Material is null && c.Slot is null or "map" && IsTextureExtension(c.File.Extension is { Length: > 0 } e ? e : Path.GetExtension(c.LocalPath)))
            .Select(c => c.LocalPath).FirstOrDefault();
        if (fallback is not null)
            foreach (var material in scene.Materials)
                if (!textures.Any(t => t.Material == (material.Name ?? "") && t.Slot == "map"))
                    Add(material.Name ?? "", "map", () => File.ReadAllBytes(fallback));

        neutral.RemoveAll(m => textures.Any(t => t.Material == m && t.Slot == "map"));
        return (textures, missing, neutral);

        void Add(string material, string slot, Func<byte[]> bytes)
        {
            if (!includeBytes)
            {
                textures.Add(new RenderTexture(material, slot, "", []));
                return;
            }
            var (mime, data) = ForBrowser(bytes());
            textures.Add(new RenderTexture(material, slot, mime, data));
        }
    }

    /// <summary>PNG/JPEG within the size limit pass through; everything else (TGA, PSD, DDS…) becomes PNG.</summary>
    internal static (string Mime, byte[] Bytes) ForBrowser(byte[] bytes)
    {
        var isPng = bytes.Length > 4 && bytes[0] == 0x89 && bytes[1] == 0x50;
        var isJpeg = bytes.Length > 3 && bytes[0] == 0xFF && bytes[1] == 0xD8;
        try
        {
            if (isPng || isJpeg)
            {
                var info = new MagickImageInfo(bytes);
                if (Math.Max(info.Width, info.Height) <= MaxRenderTexture) return (isPng ? "image/png" : "image/jpeg", bytes);
            }
            using var image = new MagickImage(bytes);
            if (Math.Max(image.Width, image.Height) > MaxRenderTexture) image.Resize(new MagickGeometry(MaxRenderTexture, MaxRenderTexture));
            return ("image/png", image.ToByteArray(MagickFormat.Png));
        }
        catch (MagickException)
        {
            return (isJpeg ? "image/jpeg" : "image/png", bytes);
        }
    }
}
