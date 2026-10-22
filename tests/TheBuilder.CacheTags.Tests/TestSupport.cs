using System.Reflection;
using System.Runtime.ExceptionServices;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using NPoco;
using TheBuilder.CacheTags.Configuration;
using TheBuilder.CacheTags.Migrations;
using TheBuilder.CacheTags.Storage;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Cms.Infrastructure.Scoping;

namespace TheBuilder.CacheTags.Tests;

/// <summary>The package's ledger and ack tables in an in-memory SQLite database, behind a real <see cref="PackageDatabase" />.</summary>
internal sealed class TestDatabase : IDisposable
{
    // The schema the package's migrations create, written out for SQLite.
    public const string Schema = """
        CREATE TABLE theBuilderCacheTagsEvent (Id TEXT PRIMARY KEY, CreatedUtc TEXT NOT NULL, Kind TEXT NOT NULL, Server TEXT NOT NULL,
            Change TEXT NOT NULL, Plan TEXT NOT NULL, Signal TEXT NULL, Status TEXT NULL, ReleasedUtc TEXT NULL, Missing TEXT NULL);
        CREATE TABLE theBuilderCacheTagsOutbox (Id TEXT PRIMARY KEY, EventId TEXT NOT NULL, Target TEXT NOT NULL, Keys TEXT NOT NULL,
            State TEXT NOT NULL, Attempts INTEGER NOT NULL, CreatedUtc TEXT NOT NULL, NotBeforeUtc TEXT NOT NULL, SentUtc TEXT NULL,
            LastError TEXT NULL, LeaseOwner TEXT NULL, LeaseUntilUtc TEXT NULL);
        CREATE TABLE theBuilderCacheTagsServer (Server TEXT PRIMARY KEY, FirstSeenUtc TEXT NOT NULL, LastSeenUtc TEXT NOT NULL);
        CREATE TABLE theBuilderCacheTagsAck (Id INTEGER PRIMARY KEY AUTOINCREMENT, EventId TEXT NOT NULL, Server TEXT NOT NULL, AckedUtc TEXT NOT NULL, IndexedUtc TEXT NULL);
        CREATE UNIQUE INDEX IX_AckPair ON theBuilderCacheTagsAck (EventId, Server);
        """;

    private readonly SqliteConnection _connection;

    /// <param name="installed">Whether Umbraco is running with the package's migrations done.</param>
    public TestDatabase(bool installed = true)
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        Sql = new Database(_connection, DatabaseType.SQLite);
        Sql.Execute(Schema);

        IUmbracoDatabase umbracoDatabase = Fake.Forwarding<IUmbracoDatabase>(Sql);
        IScope scope = Fake.Of<IScope>((method, _) => method.Name == "get_Database" ? umbracoDatabase : null);
        Package = new PackageDatabase(
            Fake.Of<IScopeProvider>((method, _) => method.Name == nameof(IScopeProvider.CreateScope) ? scope : null),
            Fake.Of<IRuntimeState>((method, _) => method.Name == "get_Level" ? (installed ? RuntimeLevel.Run : RuntimeLevel.Boot) : null),
            Fake.Of<IKeyValueService>((method, _) => method.Name == nameof(IKeyValueService.GetValue) ? new CacheTagsMigrationPlan().FinalState : null));
    }

    /// <summary>The database itself, for arranging rows and reading them back.</summary>
    public Database Sql { get; }

    public PackageDatabase Package { get; }

    public void Dispose()
    {
        Sql.Dispose();
        _connection.Dispose();
    }
}

/// <summary>An interface implemented by one function, for the Umbraco services a test needs a member or two of.</summary>
public class Fake : DispatchProxy
{
    private Func<MethodInfo, object?[], object?> _handle = (_, _) => null;

    /// <summary>
    ///     A <typeparamref name="T" /> whose members call <paramref name="handle" />. A <c>null</c> result
    ///     is the member's default: a completed task, or the type's default value.
    /// </summary>
    public static T Of<T>(Func<MethodInfo, object?[], object?>? handle = null)
        where T : class
    {
        T proxy = Create<T, Fake>();
        if (handle is not null)
        {
            ((Fake)(object)proxy)._handle = handle;
        }

        return proxy;
    }

    /// <summary>A <typeparamref name="T" /> that calls each member on <paramref name="target" />.</summary>
    public static T Forwarding<T>(object target)
        where T : class
        => Of<T>((method, args) =>
        {
            try
            {
                return method.Invoke(target, args);
            }
            catch (TargetInvocationException exception) when (exception.InnerException is not null)
            {
                ExceptionDispatchInfo.Throw(exception.InnerException);
                throw;
            }
        });

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        MethodInfo method = targetMethod!;
        return _handle(method, args ?? []) ?? Default(method.ReturnType);
    }

    private static object? Default(Type type)
        => type == typeof(Task) ? Task.CompletedTask
            : type == typeof(void) ? null
            : type.IsValueType ? Activator.CreateInstance(type)
            : null;
}

/// <summary>
///     A clock that stands still until a test moves it, and then fires the timers (delays, timeouts)
///     that have fallen due.
/// </summary>
internal sealed class ManualTime(DateTime? startUtc = null) : TimeProvider
{
    private readonly Lock _lock = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTime _utcNow = startUtc ?? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public DateTime UtcNow
    {
        get
        {
            lock (_lock)
            {
                return _utcNow;
            }
        }

        set
        {
            List<ManualTimer> due;
            lock (_lock)
            {
                _utcNow = value;
                due = _timers.Where(timer => timer.DueUtc <= _utcNow).ToList();
                _timers.RemoveAll(due.Contains);
            }

            foreach (ManualTimer timer in due)
            {
                timer.Fire();
            }
        }
    }

    public void Advance(TimeSpan by) => UtcNow += by;

    public override DateTimeOffset GetUtcNow() => new(UtcNow, TimeSpan.Zero);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    // One-shot: the package's delays and timeouts never repeat.
    private sealed class ManualTimer(ManualTime time, TimerCallback callback, object? state) : ITimer
    {
        public DateTime DueUtc { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (time._lock)
            {
                time._timers.Remove(this);
                if (dueTime == Timeout.InfiniteTimeSpan)
                {
                    return true;
                }

                DueUtc = time._utcNow + dueTime;
                time._timers.Add(this);
            }

            if (dueTime == TimeSpan.Zero)
            {
                time.Advance(TimeSpan.Zero);
            }

            return true;
        }

        public void Fire() => callback(state);

        public void Dispose()
        {
            lock (time._lock)
            {
                time._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>The real clock, with every timer and delay a thousand times shorter.</summary>
internal sealed class FastTime : TimeProvider
{
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        => System.CreateTimer(callback, state, Scale(dueTime), Scale(period));

    private static TimeSpan Scale(TimeSpan span) => span == Timeout.InfiniteTimeSpan ? span : span / 1000;
}

/// <summary>Settings read through a function, which can throw as an invalid reload does.</summary>
internal sealed class TestOptions(Func<CacheTagsOptions> read) : IOptionsMonitor<CacheTagsOptions>
{
    private int _reads;

    public TestOptions(CacheTagsOptions options)
        : this(() => options)
    {
    }

    /// <summary>How many times the settings were read.</summary>
    public int Reads => _reads;

    public CacheTagsOptions CurrentValue => Get(null);

    public CacheTagsOptions Get(string? name)
    {
        Interlocked.Increment(ref _reads);
        return read();
    }

    public IDisposable? OnChange(Action<CacheTagsOptions, string?> listener) => null;
}
