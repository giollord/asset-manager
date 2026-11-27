using System.Data.Common;
using AssetManager.Core.Settings;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AssetManager.Data;

/// <summary>
/// Owns the currently opened catalog file: creates DbContexts for it, applies pragmas on every connection,
/// creates/migrates the file on open (with a .bak copy before upgrading) and raises <see cref="Changed"/>
/// when the catalog is switched or its content changes.
/// </summary>
public sealed class CatalogStore : IDbContextFactory<CatalogDbContext>
{
    private readonly ILogger _logger;
    private readonly IReadOnlyList<IInterceptor> _interceptors;
    private DbContextOptions<CatalogDbContext>? _options;

    public CatalogStore(ILogger<CatalogStore>? logger = null, IEnumerable<IInterceptor>? interceptors = null)
    {
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _interceptors = [new SqlitePragmaInterceptor(), new Search.SearchIndexInterceptor(), new Search.EmbeddingDirtyInterceptor(), .. interceptors ?? []];
    }

    /// <summary>Path of the opened catalog, or null before <see cref="OpenAsync"/>.</summary>
    public string? Path { get; private set; }

    /// <summary>Raised after another catalog file was opened.</summary>
    public event Action? Opened;

    /// <summary>Raised once per committed batch of catalog changes (imports, bulk actions) for caches such as stats.</summary>
    public event Action? ContentChanged;

    public void NotifyContentChanged() => ContentChanged?.Invoke();

    private readonly SemaphoreSlim _writeGate = new(1, 1);

    /// <summary>
    /// Takes the in-process write turn; dispose the result to release it. Parallel workers that each save in their own
    /// transaction take it around the save: SQLite's busy handler is not fair, so with many writers one can keep losing
    /// the lock past the busy timeout ("database is locked"), worst on a slow (USB) drive. The gate queues them instead.
    /// </summary>
    public async Task<IDisposable> AcquireWriteAsync(CancellationToken ct = default)
    {
        await _writeGate.WaitAsync(ct);
        return new WriteTurn(_writeGate);
    }

    private sealed class WriteTurn(SemaphoreSlim gate) : IDisposable
    {
        private int _released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) gate.Release();
        }
    }

    public CatalogDbContext CreateDbContext() =>
        new(_options ?? throw new InvalidOperationException("No catalog is open."));

    /// <summary>Opens (creating if missing) and migrates the catalog at <paramref name="path"/>.</summary>
    public async Task OpenAsync(string path, CancellationToken ct = default)
    {
        path = System.IO.Path.GetFullPath(path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var isNew = !File.Exists(path) || new FileInfo(path).Length == 0;

        var options = BuildOptions(path, _interceptors);
        await using (var db = new CatalogDbContext(options))
        {
            if (isNew)
            {
                // auto_vacuum must be set before the first table is created; WAL is persistent per file.
                await db.Database.OpenConnectionAsync(ct);
                await db.Database.ExecuteSqlRawAsync("PRAGMA auto_vacuum=INCREMENTAL; PRAGMA journal_mode=WAL;", ct);
            }
            else
            {
                var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
                if (pending.Count > 0)
                {
                    await db.Database.OpenConnectionAsync(ct);
                    await db.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(TRUNCATE);", ct);
                    await db.Database.CloseConnectionAsync();
                    SqliteConnection.ClearAllPools();
                    var bak = path + ".bak";
                    File.Copy(path, bak, overwrite: true);
                    _logger.LogInformation("Upgrading catalog {Path} ({Count} migrations); backup at {Bak}", path, pending.Count, bak);
                }
            }
            await db.Database.MigrateAsync(ct);
            await EnsureAutoVacuumAsync(db, ct);
            // Backups are single files in rollback-journal mode; a catalog opened from one goes back to WAL.
            await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", ct);
        }

        _options = options;
        Path = path;
        Opened?.Invoke();
    }

    /// <summary>Runs <c>PRAGMA incremental_vacuum</c> to return free pages to the OS.</summary>
    public async Task IncrementalVacuumAsync(CancellationToken ct = default)
    {
        await using var db = CreateDbContext();
        await db.Database.ExecuteSqlRawAsync("PRAGMA incremental_vacuum;", ct);
    }

    /// <summary>Full VACUUM ("Compact catalog").</summary>
    public async Task VacuumAsync(CancellationToken ct = default)
    {
        await using var db = CreateDbContext();
        await db.Database.ExecuteSqlRawAsync("VACUUM;", ct);
    }

    private static async Task EnsureAutoVacuumAsync(CatalogDbContext db, CancellationToken ct)
    {
        var conn = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA auto_vacuum;";
        var mode = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
        if (mode != 2)
        {
            cmd.CommandText = "PRAGMA auto_vacuum=INCREMENTAL; VACUUM;";
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await db.Database.CloseConnectionAsync();
    }

    public static DbContextOptions<CatalogDbContext> BuildOptions(string path, IEnumerable<IInterceptor> interceptors)
    {
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
            DefaultTimeout = 30,
        }.ToString();
        return new DbContextOptionsBuilder<CatalogDbContext>()
            .UseSqlite(cs)
            .AddInterceptors(interceptors)
            .ConfigureWarnings(w => w.Ignore(CoreEventId.PossibleIncorrectRequiredNavigationWithQueryFilterInteractionWarning))
            .Options;
    }
}

/// <summary>Per-connection pragmas.</summary>
internal sealed class SqlitePragmaInterceptor : DbConnectionInterceptor
{
    private const string Pragmas = "PRAGMA foreign_keys=ON; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=10000; PRAGMA temp_store=MEMORY;";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData) => Apply(connection);

    public override Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken ct = default)
    {
        Apply(connection);
        return Task.CompletedTask;
    }

    private static void Apply(DbConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = Pragmas;
        cmd.ExecuteNonQuery();
    }
}

/// <summary>Design-time factory for <c>dotnet ef migrations</c>.</summary>
internal sealed class CatalogDesignTimeFactory : IDesignTimeDbContextFactory<CatalogDbContext>
{
    public CatalogDbContext CreateDbContext(string[] args) =>
        new(CatalogStore.BuildOptions(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "am-design.amdb"), []));
}
