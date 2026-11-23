using System.Data.Common;
using AssetManager.Core.Domain;
using AssetManager.Core.Search;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;

namespace AssetManager.Data.Search;

/// <summary>Loads the sqlite-vec extension on every catalog connection when the native library is present.</summary>
public sealed class SqliteVecInterceptor : DbConnectionInterceptor
{
    private readonly string? _path;

    public SqliteVecInterceptor(string? extensionPath = null)
    {
        _path = extensionPath ?? Path.Combine(AppContext.BaseDirectory, "vec0.dll");
        Available = File.Exists(_path);
    }

    /// <summary>False when the extension is missing or failed to load; the in-memory index is used instead.</summary>
    public bool Available { get; private set; }

    public string? LoadError { get; private set; }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData) => Load(connection);

    public override Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken ct = default)
    {
        Load(connection);
        return Task.CompletedTask;
    }

    private void Load(DbConnection connection)
    {
        if (!Available || connection is not SqliteConnection c) return;
        try
        {
            c.LoadExtension(_path!);
        }
        catch (Exception ex) when (ex is SqliteException or DllNotFoundException or BadImageFormatException)
        {
            Available = false;
            LoadError = ex.Message;
        }
    }
}

/// <summary>Shared Embeddings-table handling for both index implementations.</summary>
public abstract class VectorIndexBase(IDbContextFactory<CatalogDbContext> dbFactory) : IVectorIndex
{
    protected IDbContextFactory<CatalogDbContext> DbFactory { get; } = dbFactory;

    public abstract string Name { get; }

    public async Task UpsertAsync(long assetId, ReadOnlyMemory<float> vector, string model, CancellationToken ct = default)
    {
        var v = VectorMath.Normalize(vector.Span);
        await using var db = DbFactory.CreateDbContext();
        var row = await db.Embeddings.FindAsync([assetId], ct);
        if (row is null) db.Embeddings.Add(row = new AssetEmbedding { AssetId = assetId });
        row.Vector = VectorMath.ToBytes(v);
        row.Model = model;
        row.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        await IndexAsync(db, assetId, v, ct);
    }

    public async Task RemoveAsync(IReadOnlyCollection<long> assetIds, CancellationToken ct = default)
    {
        if (assetIds.Count == 0) return;
        await using var db = DbFactory.CreateDbContext();
        await db.Embeddings.Where(e => assetIds.Contains(e.AssetId)).ExecuteDeleteAsync(ct);
        await UnindexAsync(db, assetIds, ct);
    }

    public abstract Task<IReadOnlyList<VectorHit>> SearchAsync(ReadOnlyMemory<float> query, int k, CancellationToken ct = default);

    public abstract Task RebuildAsync(CancellationToken ct = default);

    protected abstract Task IndexAsync(CatalogDbContext db, long assetId, float[] unitVector, CancellationToken ct);

    protected abstract Task UnindexAsync(CatalogDbContext db, IReadOnlyCollection<long> assetIds, CancellationToken ct);
}

/// <summary>sqlite-vec <c>vec0</c> virtual table with cosine distance.</summary>
public sealed class SqliteVecIndex(IDbContextFactory<CatalogDbContext> dbFactory, int dimensions = SqliteVecIndex.DefaultDimensions)
    : VectorIndexBase(dbFactory)
{
    public const int DefaultDimensions = 256;

    public override string Name => "sqlite-vec";

    public async Task EnsureTableAsync(CancellationToken ct = default)
    {
        await using var db = DbFactory.CreateDbContext();
        await db.Database.ExecuteSqlRawAsync(
            $"CREATE VIRTUAL TABLE IF NOT EXISTS asset_vec USING vec0(embedding float[{dimensions}] distance_metric=cosine)", ct);
    }

    protected override async Task IndexAsync(CatalogDbContext db, long assetId, float[] v, CancellationToken ct)
    {
        Check(v);
        await db.Database.ExecuteSqlRawAsync("DELETE FROM asset_vec WHERE rowid = {0}", [assetId], ct);
        await db.Database.ExecuteSqlRawAsync("INSERT INTO asset_vec(rowid, embedding) VALUES ({0}, {1})", [assetId, VectorMath.ToBytes(v)], ct);
    }

    protected override async Task UnindexAsync(CatalogDbContext db, IReadOnlyCollection<long> ids, CancellationToken ct)
    {
        foreach (var chunk in ids.Chunk(500))
            await db.Database.ExecuteSqlRawAsync($"DELETE FROM asset_vec WHERE rowid IN ({string.Join(',', chunk)})", ct);
    }

    public override async Task<IReadOnlyList<VectorHit>> SearchAsync(ReadOnlyMemory<float> query, int k, CancellationToken ct = default)
    {
        var q = VectorMath.Normalize(query.Span);
        Check(q);
        await using var db = DbFactory.CreateDbContext();
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT rowid, distance FROM asset_vec WHERE embedding MATCH $q AND k = $k ORDER BY distance";
        cmd.Parameters.AddWithValue("$q", VectorMath.ToBytes(q));
        cmd.Parameters.AddWithValue("$k", k);
        var hits = new List<VectorHit>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) hits.Add(new(r.GetInt64(0), r.GetDouble(1)));
        return hits;
    }

    public override async Task RebuildAsync(CancellationToken ct = default)
    {
        await EnsureTableAsync(ct);
        await using var db = DbFactory.CreateDbContext();
        await db.Database.ExecuteSqlRawAsync("DELETE FROM asset_vec", ct);
        await foreach (var e in db.Embeddings.AsNoTracking().AsAsyncEnumerable().WithCancellation(ct))
        {
            var v = VectorMath.FromBytes(e.Vector);
            if (v.Length == dimensions)
                await db.Database.ExecuteSqlRawAsync("INSERT INTO asset_vec(rowid, embedding) VALUES ({0}, {1})", [e.AssetId, e.Vector], ct);
        }
    }

    private void Check(float[] v)
    {
        if (v.Length != dimensions) throw new ArgumentException($"Expected {dimensions}-d vector, got {v.Length}.");
    }
}

/// <summary>
/// Fallback when sqlite-vec cannot load: brute-force cosine over int8-quantized unit vectors held in memory
/// (~256 B per asset at 256-d).
/// </summary>
public sealed class InMemoryVectorIndex(IDbContextFactory<CatalogDbContext> dbFactory) : VectorIndexBase(dbFactory)
{
    private readonly Lock _lock = new();
    private Dictionary<long, sbyte[]>? _vectors;

    public override string Name => "in-memory";

    protected override async Task IndexAsync(CatalogDbContext db, long assetId, float[] v, CancellationToken ct)
    {
        var map = await LoadAsync(ct);
        lock (_lock) map[assetId] = Quantize(v);
    }

    protected override async Task UnindexAsync(CatalogDbContext db, IReadOnlyCollection<long> ids, CancellationToken ct)
    {
        var map = await LoadAsync(ct);
        lock (_lock)
            foreach (var id in ids) map.Remove(id);
    }

    public override async Task<IReadOnlyList<VectorHit>> SearchAsync(ReadOnlyMemory<float> query, int k, CancellationToken ct = default)
    {
        var map = await LoadAsync(ct);
        var q = Quantize(VectorMath.Normalize(query.Span));
        KeyValuePair<long, sbyte[]>[] items;
        lock (_lock) items = map.ToArray();
        var top = new PriorityQueue<long, int>(k + 1);
        var scores = new Dictionary<long, int>(k + 1);
        foreach (var (id, v) in items)
        {
            if (v.Length != q.Length) continue;
            var dot = Dot(q, v);
            top.Enqueue(id, dot);
            scores[id] = dot;
            if (top.Count > k) scores.Remove(top.Dequeue());
        }
        return scores
            .Select(s => new VectorHit(s.Key, 1 - s.Value / (127.0 * 127.0)))
            .OrderBy(h => h.Distance)
            .ToList();
    }

    public override async Task RebuildAsync(CancellationToken ct = default)
    {
        lock (_lock) _vectors = null;
        await LoadAsync(ct);
    }

    private async Task<Dictionary<long, sbyte[]>> LoadAsync(CancellationToken ct)
    {
        lock (_lock)
            if (_vectors is not null) return _vectors;
        await using var db = DbFactory.CreateDbContext();
        var map = new Dictionary<long, sbyte[]>();
        await foreach (var e in db.Embeddings.AsNoTracking().AsAsyncEnumerable().WithCancellation(ct))
            map[e.AssetId] = Quantize(VectorMath.FromBytes(e.Vector));
        lock (_lock) return _vectors ??= map;
    }

    private static sbyte[] Quantize(float[] unit)
    {
        var r = new sbyte[unit.Length];
        for (var i = 0; i < unit.Length; i++) r[i] = (sbyte)Math.Clamp(MathF.Round(unit[i] * 127f), -127, 127);
        return r;
    }

    private static int Dot(sbyte[] a, sbyte[] b)
    {
        var s = 0;
        for (var i = 0; i < a.Length; i++) s += a[i] * b[i];
        return s;
    }
}

/// <summary>
/// The registered <see cref="IVectorIndex"/>: uses sqlite-vec when the extension loads, else the in-memory fallback.
/// </summary>
public sealed class VectorIndexSelector(
    IDbContextFactory<CatalogDbContext> dbFactory, SqliteVecInterceptor vec, ILogger<VectorIndexSelector> logger) : IVectorIndex
{
    private IVectorIndex? _impl;

    private IVectorIndex Impl => _impl ?? throw new InvalidOperationException("Vector index not initialized.");

    public string Name => _impl?.Name ?? "uninitialized";

    /// <summary>Picks the implementation for the opened catalog and syncs the derived index.</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (vec.Available)
        {
            try
            {
                var s = new SqliteVecIndex(dbFactory);
                await s.EnsureTableAsync(ct);
                if (vec.Available)
                {
                    _impl = s;
                    await s.RebuildIfStaleAsync(dbFactory, ct);
                    logger.LogInformation("Vector index: {Name}", s.Name);
                    return;
                }
            }
            catch (SqliteException ex)
            {
                logger.LogWarning(ex, "sqlite-vec unavailable; using in-memory vector index");
            }
        }
        if (vec.LoadError is not null) logger.LogWarning("sqlite-vec failed to load: {Error}", vec.LoadError);
        _impl = new InMemoryVectorIndex(dbFactory);
        logger.LogInformation("Vector index: {Name}", _impl.Name);
    }

    public Task UpsertAsync(long assetId, ReadOnlyMemory<float> vector, string model, CancellationToken ct = default) =>
        Impl.UpsertAsync(assetId, vector, model, ct);

    public Task RemoveAsync(IReadOnlyCollection<long> assetIds, CancellationToken ct = default) => Impl.RemoveAsync(assetIds, ct);

    public Task<IReadOnlyList<VectorHit>> SearchAsync(ReadOnlyMemory<float> query, int k, CancellationToken ct = default) =>
        Impl.SearchAsync(query, k, ct);

    public Task RebuildAsync(CancellationToken ct = default) => Impl.RebuildAsync(ct);
}

internal static class SqliteVecIndexExtensions
{
    /// <summary>Rebuilds vec0 when its row count differs from the Embeddings table (e.g. catalog copied from a machine without the extension).</summary>
    public static async Task RebuildIfStaleAsync(this SqliteVecIndex index, IDbContextFactory<CatalogDbContext> dbFactory, CancellationToken ct)
    {
        await using var db = dbFactory.CreateDbContext();
        var vecCount = await db.Database.SqlQueryRaw<long>("SELECT count(*) AS Value FROM asset_vec").SingleAsync(ct);
        var embCount = await db.Embeddings.LongCountAsync(ct);
        if (vecCount != embCount) await index.RebuildAsync(ct);
    }
}
