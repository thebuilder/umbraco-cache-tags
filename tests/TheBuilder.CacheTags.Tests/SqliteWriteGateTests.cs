using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NPoco;
using TheBuilder.CacheTags.Storage;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Cms.Infrastructure.Scoping;
using Umbraco.Cms.Persistence.Sqlite.Services;

namespace TheBuilder.CacheTags.Tests;

/// <summary>
///     The package's writes against a site's own transaction on a SQLite file the way Umbraco 17.8
///     opens it: write-ahead log, no shared cache, deferred transactions. Each connection carries the
///     gate as an interceptor, as Umbraco's database factory adds it to every database.
/// </summary>
public sealed class SqliteWriteGateTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"cache-tags-gate-{Guid.NewGuid():N}.sqlite");
    private readonly string _connectionString;
    private readonly List<Database> _databases = [];

    public SqliteWriteGateTests()
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = _file, Pooling = false, DefaultTimeout = 1 }.ToString();
        using Database setup = Open(gate: null);
        setup.Execute("PRAGMA journal_mode=WAL");
        setup.Execute("CREATE TABLE siteRows (x INTEGER NOT NULL)");
        setup.Execute("CREATE TABLE packageRows (x INTEGER NOT NULL)");
    }

    public void Dispose()
    {
        foreach (Database database in _databases)
        {
            database.Dispose();
        }

        SqliteConnection.ClearAllPools();
        foreach (var path in new[] { _file, _file + "-wal", _file + "-shm" })
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task A_save_that_reads_before_it_writes_still_writes_while_the_package_writes_in_the_background()
    {
        var gate = new SqliteWriteGate(NullLogger<SqliteWriteGate>.Instance);
        PackageDatabase package = Package(gate);

        // A site save that reads first, as Umbraco's data type service does before it inserts.
        Database site = Open(gate);
        site.BeginTransaction();
        site.ExecuteScalar<int>("SELECT COUNT(*) FROM siteRows");

        // The delivery worker writes meanwhile.
        Task write = Task.Run(() => package.Write(db => db.Execute("INSERT INTO packageRows (x) VALUES (1)")));
        await Task.WhenAny(write, Task.Delay(300));

        // Committing the package's row first would leave the save on an old snapshot: SQLITE_BUSY_SNAPSHOT.
        site.Execute("INSERT INTO siteRows (x) VALUES (1)");
        site.CompleteTransaction();
        await write.WaitAsync(TimeSpan.FromSeconds(5));

        using Database check = Open(gate: null);
        Assert.Equal(1, check.ExecuteScalar<int>("SELECT COUNT(*) FROM siteRows"));
        Assert.Equal(1, check.ExecuteScalar<int>("SELECT COUNT(*) FROM packageRows"));
    }

    [Fact]
    public async Task A_save_that_starts_while_the_package_writes_waits_for_the_write_to_commit()
    {
        var gate = new SqliteWriteGate(NullLogger<SqliteWriteGate>.Instance);
        var holding = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        PackageDatabase package = Package(gate);

        Task write = Task.Run(() => package.Write(db =>
        {
            db.Execute("INSERT INTO packageRows (x) VALUES (1)");
            holding.Set();
            release.Wait(TimeSpan.FromSeconds(5));
        }));
        Assert.True(holding.Wait(TimeSpan.FromSeconds(5)));

        Task<int> save = Task.Run(() =>
        {
            Database site = Open(gate);
            site.BeginTransaction();
            var seen = site.ExecuteScalar<int>("SELECT COUNT(*) FROM packageRows");
            site.Execute("INSERT INTO siteRows (x) VALUES (1)");
            site.CompleteTransaction();
            return seen;
        });
        await Task.Delay(300);
        release.Set();

        await write.WaitAsync(TimeSpan.FromSeconds(5));

        // The save read after the package's row was committed, so its snapshot was current when it wrote.
        Assert.Equal(1, await save.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task The_package_waits_for_a_transaction_that_never_ends_only_as_long_as_the_wait_limit()
    {
        var gate = new SqliteWriteGate(NullLogger<SqliteWriteGate>.Instance, TimeSpan.FromMilliseconds(300));
        PackageDatabase package = Package(gate);

        Database site = Open(gate);
        site.BeginTransaction();

        var watch = System.Diagnostics.Stopwatch.StartNew();
        await Task.Run(() => package.Write(db => db.Execute("INSERT INTO packageRows (x) VALUES (1)"))).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(250), $"The write went ahead after {watch.Elapsed}.");
        site.AbortTransaction();
    }

    // A connection the way Umbraco opens one on SQLite: transactions are deferred.
    private Database Open(SqliteWriteGate? gate)
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        var database = new Database(new SqlitePreferDeferredTransactionsConnection(connection), DatabaseType.SQLite);
        if (gate is not null)
        {
            database.Interceptors.Add(gate);
            _databases.Add(database);
        }

        return database;
    }

    // A PackageDatabase whose scopes open their own connection and transaction, as a root Umbraco scope does.
    private PackageDatabase Package(SqliteWriteGate gate)
        => new(
            Fake.Of<IScopeProvider>((method, _) =>
            {
                if (method.Name != nameof(IScopeProvider.CreateScope))
                {
                    return null;
                }

                Database database = Open(gate);
                database.BeginTransaction();
                IUmbracoDatabase umbracoDatabase = Fake.Forwarding<IUmbracoDatabase>(database);
                var completed = false;
                return Fake.Of<IScope>((scopeMethod, _) =>
                {
                    switch (scopeMethod.Name)
                    {
                        case "get_Database":
                            return umbracoDatabase;
                        case nameof(IScope.Complete):
                            completed = true;
                            return null;
                        case nameof(IScope.Dispose):
                            if (completed)
                            {
                                database.CompleteTransaction();
                            }
                            else
                            {
                                database.AbortTransaction();
                            }

                            return null;
                        default:
                            return null;
                    }
                });
            }),
            Fake.Of<IRuntimeState>((method, _) => method.Name == "get_Level" ? RuntimeLevel.Run : null),
            Fake.Of<IKeyValueService>());
}
