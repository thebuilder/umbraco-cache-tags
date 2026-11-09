using System.Data;
using Microsoft.Extensions.Logging;
using NPoco;
using Umbraco.Cms.Infrastructure.Persistence;

namespace TheBuilder.CacheTags.Storage;

/// <summary>
///     Keeps the package's own writes on SQLite out of Umbraco's transactions. Umbraco opens SQLite
///     transactions deferred, and some of its services read before they write without taking a lock
///     first (saving a data type, for one). Once Umbraco stopped using a shared cache (17.8), another
///     connection that commits between that read and that write leaves the transaction on an old
///     snapshot: its write fails with <c>SQLITE_BUSY_SNAPSHOT</c>, which no wait cures, and Umbraco
///     retries it as a busy database for minutes. The package's background work commits every few
///     seconds while it delivers a change, so it did this to the editor's next save.
///     <para>
///         So a package write that starts its own transaction waits here until no other transaction on
///         this site's database is open, and transactions that start while it runs wait for it to end.
///         Its writes take milliseconds. A wait never lasts longer than <see cref="DefaultWaitLimit" />: after
///         that the write goes ahead as it did before, so a transaction that never ends cannot stop
///         the package, or the site. Only SQLite runs this; on SQL Server a write waits for the row
///         locks it needs, and no snapshot goes stale.
///     </para>
/// </summary>
internal sealed class SqliteWriteGate(ILogger<SqliteWriteGate> logger, TimeSpan? waitLimit = null) : IProviderSpecificTransactionInterceptor
{
    /// <summary>The longest any transaction waits here, for the package's write or for the others.</summary>
    public static readonly TimeSpan DefaultWaitLimit = TimeSpan.FromSeconds(5);

    // How often a waiting transaction looks again for ones that ended without saying so.
    private static readonly TimeSpan RecheckInterval = TimeSpan.FromMilliseconds(100);

    private readonly TimeSpan _waitLimit = waitLimit ?? DefaultWaitLimit;

    // Set while the package writes, on the flow that writes: its transaction is the one to keep apart.
    private static readonly AsyncLocal<bool> PackageWriting = new();

    private readonly object _lock = new();
    private readonly HashSet<IDatabase> _open = new(System.Collections.Generic.ReferenceEqualityComparer.Instance);
    private IDatabase? _packageWrite;

    /// <inheritdoc />
    public string ProviderName => "Microsoft.Data.Sqlite";

    /// <summary>The package's write is about to start: a transaction it begins is kept apart.</summary>
    public static IDisposable PackageWrite()
    {
        if (PackageWriting.Value)
        {
            return NoScope.Instance;
        }

        PackageWriting.Value = true;
        return new WriteScope();
    }

    public void OnBeginTransaction(IDatabase database)
    {
        var package = PackageWriting.Value;
        lock (_lock)
        {
            DateTime deadline = DateTime.UtcNow + _waitLimit;

            // The package waits for every other transaction; the others wait for the package's write.
            while (true)
            {
                ForgetEnded();
                if (_packageWrite is null && (package is false || _open.Count == 0))
                {
                    break;
                }

                TimeSpan left = deadline - DateTime.UtcNow;
                if (left <= TimeSpan.Zero)
                {
                    logger.LogDebug(
                        "Cache tags: a SQLite transaction waited {Limit} for {Waited} and starts anyway.",
                        _waitLimit,
                        package ? $"{_open.Count} other transactions" : "the package's write");
                    break;
                }

                Monitor.Wait(_lock, left < RecheckInterval ? left : RecheckInterval);
            }

            if (package && _packageWrite is null)
            {
                _packageWrite = database;
            }
            else
            {
                _open.Add(database);
            }
        }
    }

    public void OnCompleteTransaction(IDatabase database) => Ended(database);

    public void OnAbortTransaction(IDatabase database) => Ended(database);

    private void Ended(IDatabase database)
    {
        lock (_lock)
        {
            if (ReferenceEquals(_packageWrite, database))
            {
                _packageWrite = null;
            }

            _open.Remove(database);
            Monitor.PulseAll(_lock);
        }
    }

    // NPoco says nothing when a commit throws, and its Dispose closes the connection without saying so
    // either. A transaction whose connection is closed has ended: forget it, or every later
    // transaction would wait the full limit for it.
    private void ForgetEnded()
    {
        if (_packageWrite is not null && IsOpen(_packageWrite) is false)
        {
            _packageWrite = null;
        }

        _open.RemoveWhere(database => IsOpen(database) is false);
    }

    private static bool IsOpen(IDatabase database)
        => database.Transaction is { Connection.State: ConnectionState.Open };

    private sealed class WriteScope : IDisposable
    {
        public void Dispose() => PackageWriting.Value = false;
    }

    private sealed class NoScope : IDisposable
    {
        public static readonly NoScope Instance = new();

        public void Dispose()
        {
        }
    }
}
