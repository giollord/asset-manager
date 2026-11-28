using System.Data.Common;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AssetManager.Data;

/// <summary>
/// One in-process writer at a time on the catalog. SQLite allows a single writer, and its busy handler is not fair:
/// while an import's parallel workers keep saving, another job (AI enrichment of an older import, embeddings, tag
/// updates) can keep losing the lock past the busy timeout and fail with "database is locked" (SQLite error 5).
/// Every write therefore takes a turn here first and waits in a fair queue instead:
/// <list type="bullet">
/// <item>a transaction holds the turn from its start until it commits, rolls back, fails or its connection closes;</item>
/// <item>a write command outside a transaction (ExecuteUpdate/Delete, a single-statement save, raw SQL) holds it for that command;</item>
/// <item>a connection that already holds the turn does not take it again.</item>
/// </list>
/// A writer that waits longer than <see cref="Patience"/> goes ahead anyway and falls back to SQLite's busy handling,
/// so a code path that writes on a second connection while its first holds a transaction cannot hang forever.
/// </summary>
public sealed class CatalogWriteGate(ILogger? logger = null) : IDbCommandInterceptor, IDbTransactionInterceptor, IDbConnectionInterceptor
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConditionalWeakTable<DbConnection, Turn> _held = new();
    private readonly ILogger _log = logger ?? NullLogger.Instance;

    public TimeSpan Patience { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>What a connection holds: a transaction's turn or a single command's.</summary>
    private sealed class Turn
    {
        public bool Owned; // false: waited past the patience and went ahead without the gate
        public bool Transaction;
        public bool Command;
        public bool Manual;
    }

    /// <summary>Takes a turn that is not tied to a connection; dispose the result to release it.</summary>
    public async Task<IDisposable> AcquireAsync(CancellationToken ct = default)
    {
        var owned = await WaitAsync(ct);
        return new Release(() => { if (owned) _gate.Release(); });
    }

    /// <summary>
    /// Takes the turn for <paramref name="db"/>'s connection around work EF does not see, such as commands on the
    /// raw connection; a no-op when the connection already holds it or the context has no gate.
    /// </summary>
    public static async Task<IDisposable> EnterAsync(DbContext db, CancellationToken ct = default)
    {
        var gate = db.GetService<IDbContextOptions>().FindExtension<CoreOptionsExtension>()?.Interceptors?.OfType<CatalogWriteGate>().FirstOrDefault();
        if (gate is null) return new Release(() => { });
        var connection = db.Database.GetDbConnection();
        if (gate._held.TryGetValue(connection, out _)) return new Release(() => { });
        var turn = new Turn { Owned = await gate.WaitAsync(ct), Manual = true };
        gate._held.AddOrUpdate(connection, turn);
        return new Release(() => gate.Leave(connection, t => t.Manual));
    }

    private sealed class Release(Action release) : IDisposable
    {
        private int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) release();
        }
    }

    private async Task<bool> WaitAsync(CancellationToken ct)
    {
        if (await _gate.WaitAsync(Patience, ct)) return true;
        _log.LogWarning("A catalog write waited {Seconds:0}s for its turn and goes ahead without it", Patience.TotalSeconds);
        return false;
    }

    private bool Wait(CancellationToken ct)
    {
        if (_gate.Wait(Patience, ct)) return true;
        _log.LogWarning("A catalog write waited {Seconds:0}s for its turn and goes ahead without it", Patience.TotalSeconds);
        return false;
    }

    /// <summary>Releases the connection's turn when <paramref name="which"/> says this event ends it.</summary>
    private void Leave(DbConnection? connection, Func<Turn, bool> which)
    {
        if (connection is null || !_held.TryGetValue(connection, out var turn) || !which(turn)) return;
        _held.Remove(connection);
        if (turn.Owned) _gate.Release();
    }

    private void LeaveAny(DbConnection connection) => Leave(connection, _ => true);

    /// <summary>A write that is not inside a transaction this gate already covers.</summary>
    private bool NeedsTurn(DbCommand command) =>
        command.Connection is { } c && !_held.TryGetValue(c, out _) && command.Transaction is null && IsWrite(command.CommandText);

    /// <summary>Anything but a query: EF's reads start with SELECT, possibly after comment lines.</summary>
    public static bool IsWrite(string? sql)
    {
        var s = (sql ?? "").AsSpan().TrimStart();
        while (s.StartsWith("--"))
        {
            var end = s.IndexOf('\n');
            s = end < 0 ? [] : s[(end + 1)..].TrimStart();
        }
        return s.Length > 0 && !s.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase);
    }

    private async ValueTask EnterCommandAsync(DbCommand command, CancellationToken ct)
    {
        if (!NeedsTurn(command)) return;
        _held.AddOrUpdate(command.Connection!, new Turn { Owned = await WaitAsync(ct), Command = true });
    }

    private void EnterCommand(DbCommand command)
    {
        if (!NeedsTurn(command)) return;
        _held.AddOrUpdate(command.Connection!, new Turn { Owned = Wait(default), Command = true });
    }

    private void LeaveCommand(DbCommand command) => Leave(command.Connection, t => t.Command);

    // Commands: a write outside a transaction holds the turn until it finishes (a reader: until it is disposed).

    public async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
    {
        await EnterCommandAsync(command, ct);
        return result;
    }

    public InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        EnterCommand(command);
        return result;
    }

    public ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken ct = default)
    {
        LeaveCommand(command);
        return ValueTask.FromResult(result);
    }

    public int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
    {
        LeaveCommand(command);
        return result;
    }

    public async ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken ct = default)
    {
        await EnterCommandAsync(command, ct);
        return result;
    }

    public InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        EnterCommand(command);
        return result;
    }

    public ValueTask<object?> ScalarExecutedAsync(DbCommand command, CommandExecutedEventData eventData, object? result, CancellationToken ct = default)
    {
        LeaveCommand(command);
        return ValueTask.FromResult(result);
    }

    public object? ScalarExecuted(DbCommand command, CommandExecutedEventData eventData, object? result)
    {
        LeaveCommand(command);
        return result;
    }

    public async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
    {
        await EnterCommandAsync(command, ct);
        return result;
    }

    public InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        EnterCommand(command);
        return result;
    }

    public InterceptionResult DataReaderDisposing(DbCommand command, DataReaderDisposingEventData eventData, InterceptionResult result)
    {
        LeaveCommand(command);
        return result;
    }

    public void CommandFailed(DbCommand command, CommandErrorEventData eventData) => LeaveCommand(command);

    public Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData, CancellationToken ct = default)
    {
        LeaveCommand(command);
        return Task.CompletedTask;
    }

    public void CommandCanceled(DbCommand command, CommandEndEventData eventData) => LeaveCommand(command);

    public Task CommandCanceledAsync(DbCommand command, CommandEndEventData eventData, CancellationToken ct = default)
    {
        LeaveCommand(command);
        return Task.CompletedTask;
    }

    // Transactions (explicit ones and the ones SaveChanges opens) hold the turn from start to end.

    public async ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(DbConnection connection, TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result, CancellationToken ct = default)
    {
        if (!_held.TryGetValue(connection, out _)) _held.AddOrUpdate(connection, new Turn { Owned = await WaitAsync(ct), Transaction = true });
        return result;
    }

    public InterceptionResult<DbTransaction> TransactionStarting(DbConnection connection, TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result)
    {
        if (!_held.TryGetValue(connection, out _)) _held.AddOrUpdate(connection, new Turn { Owned = Wait(default), Transaction = true });
        return result;
    }

    private void LeaveTransaction(DbTransaction transaction) => Leave(transaction.Connection, t => t.Transaction);

    public void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData) => LeaveTransaction(transaction);

    public Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken ct = default)
    {
        LeaveTransaction(transaction);
        return Task.CompletedTask;
    }

    public void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData) => LeaveTransaction(transaction);

    public Task TransactionRolledBackAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken ct = default)
    {
        LeaveTransaction(transaction);
        return Task.CompletedTask;
    }

    public void TransactionFailed(DbTransaction transaction, TransactionErrorEventData eventData) => LeaveTransaction(transaction);

    public Task TransactionFailedAsync(DbTransaction transaction, TransactionErrorEventData eventData, CancellationToken ct = default)
    {
        LeaveTransaction(transaction);
        return Task.CompletedTask;
    }

    // A transaction disposed without commit or rollback ends when its connection closes (EF closes the connection it
    // opened for it); a disposed context closes its connection too.

    public InterceptionResult ConnectionClosing(DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
    {
        LeaveAny(connection);
        return result;
    }

    public ValueTask<InterceptionResult> ConnectionClosingAsync(DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
    {
        LeaveAny(connection);
        return ValueTask.FromResult(result);
    }

    public InterceptionResult ConnectionDisposing(DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
    {
        LeaveAny(connection);
        return result;
    }

    public ValueTask<InterceptionResult> ConnectionDisposingAsync(DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
    {
        LeaveAny(connection);
        return ValueTask.FromResult(result);
    }
}
