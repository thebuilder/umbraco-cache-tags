using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NPoco;
using NPoco.FluentMappings;
using TheBuilder.CacheTags.Migrations;
using Umbraco.Cms.Core.Configuration.Models;
using Umbraco.Cms.Infrastructure.Migrations;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Cms.Persistence.Sqlite.Services;

namespace TheBuilder.CacheTags.Tests;

/// <summary>
///     A SQLite database with the package's schema made by its own migration plan, run through
///     Umbraco's SQLite syntax provider, so the SQL tests run against the tables a site gets. A step
///     missing from the plan fails the tests that use the tables or rows it adds. A fresh install
///     creates each table from today's row class, so a column a later step adds to an existing table
///     is only checked by the upgrade tests in <see cref="MigrationTests" />.
///     The steps run one after another with a minimal <see cref="IMigrationContext" />, not through
///     Umbraco's migration executor: no scopes, no post-migrations, and SQLite only.
/// </summary>
internal sealed class MigratedDatabase : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"cache-tags-{Guid.NewGuid():N}.sqlite");
    private readonly SqliteConnection _connection;

    private MigratedDatabase()
    {
        // Pooling off, so the file is closed, and can be deleted, when the connections are.
        ConnectionString = new SqliteConnectionStringBuilder { DataSource = _file, Pooling = false }.ToString();

        // Umbraco's own table, which a site has before any package migration runs.
        _connection = new SqliteConnection(ConnectionString);
        _connection.Open();
        Database = new Database(_connection, DatabaseType.SQLite);
        State = Plan.InitialState;
        Database.Execute("CREATE TABLE umbracoLock (id INTEGER NOT NULL PRIMARY KEY, value INTEGER NOT NULL, name TEXT NOT NULL)");
    }

    public string ConnectionString { get; }

    public CacheTagsMigrationPlan Plan { get; } = new();

    /// <summary>The plan state the migrations have reached.</summary>
    public string State { get; private set; } = string.Empty;

    /// <summary>The database, for the package's SQL.</summary>
    public Database Database { get; }

    /// <summary>A database migrated to the plan's final state.</summary>
    public static MigratedDatabase Create()
    {
        var database = new MigratedDatabase();
        database.MigrateAsync().GetAwaiter().GetResult();
        return database;
    }

    /// <summary>A database migrated until the plan reaches the given migration, which has not run.</summary>
    public static MigratedDatabase Before<TMigration>()
    {
        var database = new MigratedDatabase();
        database.MigrateAsync(stopAt: typeof(TMigration)).GetAwaiter().GetResult();
        return database;
    }

    /// <summary>Runs the plan's steps from the state the database is in, as an upgrade does.</summary>
    public async Task MigrateAsync(Type? stopAt = null)
    {

        using var umbracoDatabase = new UmbracoDatabase(
            ConnectionString,
            SqlContext(),
            SqliteFactory.Instance,
            NullLogger<UmbracoDatabase>.Instance,
            new SqliteBulkSqlInsertProvider(),
            null!,
            []);

        while (Plan.Transitions.TryGetValue(State, out MigrationPlan.Transition? transition) && transition is not null)
        {
            if (transition.MigrationType == stopAt)
            {
                break;
            }

            var context = new Context(Plan, umbracoDatabase);
            var migration = (AsyncMigrationBase)Activator.CreateInstance(transition.MigrationType, context)!;
            await migration.RunAsync();
            State = transition.TargetState;
        }
    }

    /// <summary>The package's tables, their columns and their indexes, one line each, for comparing two databases.</summary>
    public IReadOnlyList<string> Schema()
    {
        var lines = new List<string>();
        foreach (var table in Database.Fetch<string>("SELECT name FROM sqlite_master WHERE type = 'table' AND name LIKE 'theBuilderCacheTags%' ORDER BY name"))
        {
            foreach (IDictionary<string, object> column in Database.Fetch<dynamic>("SELECT * FROM pragma_table_info(@0)", table))
            {
                var notNull = Convert.ToInt64(column["notnull"]) == 1 ? " NOT NULL" : "";
                var primaryKey = Convert.ToInt64(column["pk"]) > 0 ? " PRIMARY KEY" : "";
                lines.Add($"{table}.{column["name"]} {column["type"]}{notNull}{primaryKey}");
            }

            lines.AddRange(Indexes(table));
        }

        return lines.Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>A table's own indexes (not the ones SQLite makes for a primary key): name, unique, columns.</summary>
    public IReadOnlyList<string> Indexes(string table)
    {
        var indexes = new List<string>();
        foreach (IDictionary<string, object> index in Database.Fetch<dynamic>("SELECT * FROM pragma_index_list(@0)", table))
        {
            if ((string)index["origin"] != "c")
            {
                continue;
            }

            var columns = Database.Fetch<dynamic>("SELECT * FROM pragma_index_info(@0)", index["name"])
                .Cast<IDictionary<string, object>>()
                .OrderBy(column => Convert.ToInt64(column["seqno"]))
                .Select(column => (string)column["name"]);
            indexes.Add($"{table} {(Convert.ToInt64(index["unique"]) == 1 ? "UNIQUE " : "")}{index["name"]} ({string.Join(", ", columns)})");
        }

        return indexes.Order(StringComparer.Ordinal).ToList();
    }

    public void Dispose()
    {
        Database.Dispose();
        _connection.Dispose();
        File.Delete(_file);
    }

    private static SqlContext SqlContext()
    {
        var syntax = new SqliteSyntaxProvider(Options.Create(new GlobalSettings()), NullLogger<SqliteSyntaxProvider>.Instance);
        var mappers = new Umbraco.Cms.Infrastructure.Persistence.Mappers.MapperCollection(() => []);
        var pocoDataFactory = new FluentPocoDataFactory((type, factory) => new PocoDataBuilder(type, new NPoco.MapperCollection()).Init(), new NPoco.MapperCollection());
        return new SqlContext(syntax, DatabaseType.SQLite, pocoDataFactory, mappers);
    }

    private sealed class Context(MigrationPlan plan, IUmbracoDatabase database) : IMigrationContext
    {
        public MigrationPlan Plan => plan;

        public ILogger<IMigrationContext> Logger => NullLogger<IMigrationContext>.Instance;

        public IUmbracoDatabase Database => database;

        public ISqlContext SqlContext => database.SqlContext;

        public int Index { get; set; }

        public bool BuildingExpression { get; set; }

        public bool IsCompleted { get; private set; }

        public void Complete() => IsCompleted = true;
    }
}
