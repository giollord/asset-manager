using AssetManager.Processing.Models;

namespace AssetManager.Shared.Services;

/// <summary>What the hidden <c>PreviewRenderHost</c> component offers to the renderer service.</summary>
public interface IRenderHost
{
    Task<IReadOnlyList<RenderedView>> RenderAsync(ModelRenderRequest request, CancellationToken ct);

    Task<RenderedView> RenderProbeAsync(ProbeRequest request, CancellationToken ct);

    Task<AnimationFrames> RenderAnimationAsync(AnimationRenderRequest request, CancellationToken ct);
}

/// <summary>
/// <see cref="IModelRenderer"/> backed by three.js in the app's WebView. The <c>PreviewRenderHost</c> mounted once in
/// MainLayout attaches itself; renders are serialized (one WebGL context). Without an attached host (headless Web
/// dev host, tests) callers get <see cref="RendererUnavailableException"/>.
/// </summary>
public sealed class WebViewModelRenderer : IModelRenderer
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile IRenderHost? _host;

    public bool IsAvailable => _host is not null;

    public event Action? AvailabilityChanged;

    public void Attach(IRenderHost host)
    {
        _host = host;
        AvailabilityChanged?.Invoke();
    }

    public void Detach(IRenderHost host)
    {
        if (ReferenceEquals(_host, host)) _host = null;
        AvailabilityChanged?.Invoke();
    }

    public async Task<IReadOnlyList<RenderedView>> RenderAsync(ModelRenderRequest request, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var host = _host ?? throw new RendererUnavailableException();
            return await host.RenderAsync(request, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AnimationFrames> RenderAnimationAsync(AnimationRenderRequest request, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var host = _host ?? throw new RendererUnavailableException();
            return await host.RenderAnimationAsync(request, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<RenderedView> RenderProbeAsync(ProbeRequest request, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var host = _host ?? throw new RendererUnavailableException();
            return await host.RenderProbeAsync(request, ct);
        }
        finally
        {
            _gate.Release();
        }
    }
}
