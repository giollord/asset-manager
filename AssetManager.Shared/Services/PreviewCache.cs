using AssetManager.Data;
using AssetManager.Data.Services;

namespace AssetManager.Shared.Services;

/// <summary>
/// Per-window cache of preview data URLs, so scrolling the virtualized grid back and forth does not re-read blobs.
/// Bounded; cleared when catalog content changes (previews may have been regenerated).
/// </summary>
public sealed class PreviewCache : IDisposable
{
    private const int Capacity = 3000;
    private readonly PreviewQueries _previews;
    private readonly CatalogStore _catalog;
    private readonly Dictionary<long, string?> _thumbs = [];
    private readonly Dictionary<long, IReadOnlyList<string>> _views = [];
    private readonly Queue<long> _order = new();
    private readonly Lock _gate = new();

    public PreviewCache(PreviewQueries previews, CatalogStore catalog)
    {
        _previews = previews;
        _catalog = catalog;
        _catalog.ContentChanged += Clear;
    }

    public async Task<string?> ThumbnailAsync(long assetId)
    {
        lock (_gate)
            if (_thumbs.TryGetValue(assetId, out var hit)) return hit;
        var url = (await _previews.ThumbnailAsync(assetId))?.DataUrl;
        lock (_gate)
        {
            if (_thumbs.TryAdd(assetId, url)) _order.Enqueue(assetId);
            while (_order.Count > Capacity)
            {
                var old = _order.Dequeue();
                _thumbs.Remove(old);
                _views.Remove(old);
            }
        }
        return url;
    }

    /// <summary>All angles of a model (few per asset; kept while its thumbnail is cached).</summary>
    public async Task<IReadOnlyList<string>> ModelViewsAsync(long assetId)
    {
        lock (_gate)
            if (_views.TryGetValue(assetId, out var hit)) return hit;
        var urls = (await _previews.ModelViewsAsync(assetId)).Select(p => p.DataUrl).ToList();
        lock (_gate) _views[assetId] = urls;
        return urls;
    }

    public async Task<string?> AudioClipAsync(long assetId) => (await _previews.AudioClipAsync(assetId))?.DataUrl;

    public void Clear()
    {
        lock (_gate)
        {
            _thumbs.Clear();
            _views.Clear();
            _order.Clear();
        }
    }

    public void Dispose() => _catalog.ContentChanged -= Clear;
}
