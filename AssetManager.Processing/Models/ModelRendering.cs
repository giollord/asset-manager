using AssetManager.Core.Domain;

namespace AssetManager.Processing.Models;

/// <summary>A texture the renderer applies to a material slot ("map", "normalMap"…).</summary>
public sealed record RenderTexture(string Material, string Slot, string Mime, byte[] Bytes);

/// <summary>Everything the WebView renderer needs: the model as GLB, textures by material, and the view setup.</summary>
public sealed record ModelRenderRequest(
    byte[] Glb,
    IReadOnlyList<RenderTexture> Textures,
    ModelOrientation Orientation,
    int Size,
    IReadOnlyList<int> Elevations,
    int Azimuths,
    int Quality)
{
    /// <summary>Materials whose textures are missing; rendered with a neutral default material.</summary>
    public IReadOnlyList<string> NeutralMaterials { get; init; } = [];
}

/// <summary>A probe request: the six candidate up-axes in a labeled 3×2 contact sheet.</summary>
public sealed record ProbeRequest(byte[] Glb, IReadOnlyList<RenderTexture> Textures, int TileSize, int Quality)
{
    public IReadOnlyList<string> NeutralMaterials { get; init; } = [];
}

/// <summary>An animation clip rendered from one angle: <paramref name="Fps"/> frames per second, at most <paramref name="MaxSeconds"/>.</summary>
public sealed record AnimationRenderRequest(
    byte[] Glb, IReadOnlyList<RenderTexture> Textures, ModelOrientation Orientation, int Size, int Fps, int MaxSeconds, int Quality)
{
    public IReadOnlyList<string> NeutralMaterials { get; init; } = [];
}

/// <summary>The frames of a rendered clip (each a WebP), in order.</summary>
public sealed record AnimationFrames(int Width, int Height, int Fps, IReadOnlyList<byte[]> Frames);

/// <summary>One WebP render (index = ring × azimuths + azimuth).</summary>
public sealed record RenderedView(int Index, int Width, int Height, byte[] Webp);

/// <summary>Thrown when no renderer is available (the WebView is not running, e.g. in the Web dev host or tests).</summary>
public sealed class RendererUnavailableException() : Exception("3D renderer unavailable.");

/// <summary>Renders models off-screen. Implemented by the WebView-hosted three.js renderer (PreviewRenderHost).</summary>
public interface IModelRenderer
{
    /// <summary>Whether a renderer is currently attached.</summary>
    bool IsAvailable { get; }

    /// <exception cref="RendererUnavailableException">No renderer is attached.</exception>
    Task<IReadOnlyList<RenderedView>> RenderAsync(ModelRenderRequest request, CancellationToken ct = default);

    /// <exception cref="RendererUnavailableException">No renderer is attached.</exception>
    Task<RenderedView> RenderProbeAsync(ProbeRequest request, CancellationToken ct = default);

    /// <summary>Plays the first clip of an animated GLB and renders it frame by frame from one angle.</summary>
    /// <exception cref="RendererUnavailableException">No renderer is attached.</exception>
    Task<AnimationFrames> RenderAnimationAsync(AnimationRenderRequest request, CancellationToken ct = default) =>
        throw new RendererUnavailableException();
}
