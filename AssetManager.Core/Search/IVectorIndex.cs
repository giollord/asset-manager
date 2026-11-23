namespace AssetManager.Core.Search;

public sealed record VectorHit(long AssetId, double Distance);

/// <summary>
/// KNN index over asset embeddings. The catalog's Embeddings table is the source of truth; implementations
/// keep a derived index (sqlite-vec <c>vec0</c> table or an in-memory int8 copy) in sync with it.
/// </summary>
public interface IVectorIndex
{
    /// <summary>Implementation name for diagnostics ("sqlite-vec" or "in-memory").</summary>
    string Name { get; }

    /// <summary>Stores the embedding of an asset (normalized to unit length) and indexes it.</summary>
    Task UpsertAsync(long assetId, ReadOnlyMemory<float> vector, string model, CancellationToken ct = default);

    Task RemoveAsync(IReadOnlyCollection<long> assetIds, CancellationToken ct = default);

    /// <summary>Nearest assets by cosine distance (0 = identical), closest first.</summary>
    Task<IReadOnlyList<VectorHit>> SearchAsync(ReadOnlyMemory<float> query, int k, CancellationToken ct = default);

    /// <summary>Rebuilds the derived index from the Embeddings table.</summary>
    Task RebuildAsync(CancellationToken ct = default);
}

public static class VectorMath
{
    public static float[] Normalize(ReadOnlySpan<float> v)
    {
        double sum = 0;
        foreach (var x in v) sum += x * x;
        var n = Math.Sqrt(sum);
        var r = new float[v.Length];
        if (n == 0) return r;
        for (var i = 0; i < v.Length; i++) r[i] = (float)(v[i] / n);
        return r;
    }

    public static byte[] ToBytes(ReadOnlySpan<float> v) => System.Runtime.InteropServices.MemoryMarshal.AsBytes(v).ToArray();

    public static float[] FromBytes(byte[] b) => System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(b).ToArray();
}
