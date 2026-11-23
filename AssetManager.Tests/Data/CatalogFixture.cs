using AssetManager.Data;
using AssetManager.Tests.Fixtures;

namespace AssetManager.Tests.Data;

/// <summary>A fresh catalog file in a temp folder.</summary>
public sealed class TestCatalog : IAsyncDisposable
{
    private readonly TempDir _dir = FixtureBuilder.TempDir();

    public CatalogStore Store { get; } = new();
    public string Path => System.IO.Path.Combine(_dir.Path, "catalog.amdb");
    public TempDir Dir => _dir;

    public static async Task<TestCatalog> CreateAsync()
    {
        var c = new TestCatalog();
        await c.Store.OpenAsync(c.Path);
        return c;
    }

    public CatalogDbContext Db() => Store.CreateDbContext();

    public ValueTask DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _dir.Dispose();
        return ValueTask.CompletedTask;
    }
}
