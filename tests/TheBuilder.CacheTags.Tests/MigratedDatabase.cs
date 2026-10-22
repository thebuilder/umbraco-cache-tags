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
///     missing from the plan, or a column missing from a step, fails the tests that use it.
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
