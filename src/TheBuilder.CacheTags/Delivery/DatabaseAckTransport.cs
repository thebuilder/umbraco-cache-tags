using NPoco;
using TheBuilder.CacheTags.Storage;
using Umbraco.Cms.Infrastructure.Scoping;

namespace TheBuilder.CacheTags.Delivery;

/// <summary>The default <see cref="IAckTransport" />: heartbeats and acks in the Umbraco database.</summary>
internal sealed class DatabaseAckTransport(IScopeProvider scopeProvider) : IAckTransport
{
    public Task HeartbeatAsync(string server, DateTime firstSeenUtc, DateTime nowUtc, CancellationToken cancellationToken = default)
        => Write(database => AckSql.Heartbeat(database, server, firstSeenUtc, nowUtc));

    public Task LeaveAsync(string server, CancellationToken cancellationToken = default)
        => Write(database => database.Execute($"DELETE FROM {Constants.Tables.Servers} WHERE Server = @0", server));

    public Task<IReadOnlyCollection<ServerPresence>> GetServersAsync(CancellationToken cancellationToken = default)
        => Read<IReadOnlyCollection<ServerPresence>>(AckSql.Servers);

    public Task AckAsync(string server, IReadOnlyCollection<Guid> changeIds, DateTime ackedUtc, CancellationToken cancellationToken = default)
        => Write(database => AckSql.Ack(database, server, changeIds, ackedUtc));

    public Task<IReadOnlyDictionary<Guid, IReadOnlySet<string>>> GetAcksAsync(IReadOnlyCollection<Guid> changeIds, CancellationToken cancellationToken = default)
        => Read(database => AckSql.Acks(database, changeIds));

    public Task PruneAsync(DateTime cutoffUtc, CancellationToken cancellationToken = default)
        => Write(database => AckSql.Prune(database, cutoffUtc));

    private Task Write(Action<IDatabase> write)
    {
        using IScope scope = WriteScope.Create(scopeProvider);
        write(scope.Database);
        scope.Complete();
        return Task.CompletedTask;
    }

    private Task<T> Read<T>(Func<IDatabase, T> read)
    {
        using IScope scope = scopeProvider.CreateScope(autoComplete: true);
        return Task.FromResult(read(scope.Database));
    }
}

/// <summary>The ack transport's SQL, against any NPoco database.</summary>
internal static class AckSql
{
    public static void Heartbeat(IDatabase database, string server, DateTime firstSeenUtc, DateTime nowUtc)
    {
        var updated = database.Execute(
            $"UPDATE {Constants.Tables.Servers} SET LastSeenUtc = @0 WHERE Server = @1", nowUtc, server);
        if (updated == 0)
        {
            database.Insert(new ServerRow { Server = server, FirstSeenUtc = firstSeenUtc, LastSeenUtc = nowUtc });
        }
    }

    public static IReadOnlyCollection<ServerPresence> Servers(IDatabase database)
        => database.Fetch<ServerRow>($"SELECT * FROM {Constants.Tables.Servers}")
            .Select(row => new ServerPresence(row.Server, row.FirstSeenUtc, row.LastSeenUtc))
            .ToList();

    public static void Ack(IDatabase database, string server, IReadOnlyCollection<Guid> changeIds, DateTime ackedUtc)
    {
        foreach (Guid changeId in changeIds.Distinct())
        {
            var exists = database.ExecuteScalar<int>(
                $"SELECT COUNT(*) FROM {Constants.Tables.Acks} WHERE EventId = @0 AND Server = @1", changeId, server);
            if (exists == 0)
            {
                database.Insert(new AckRow { EventId = changeId, Server = server, AckedUtc = ackedUtc });
            }
        }
    }

    public static IReadOnlyDictionary<Guid, IReadOnlySet<string>> Acks(IDatabase database, IReadOnlyCollection<Guid> changeIds)
    {
        var acks = new Dictionary<Guid, IReadOnlySet<string>>();
        foreach (Guid[] chunk in changeIds.Distinct().Chunk(500))
        {
            foreach (IGrouping<Guid, AckRow> group in database
                .Fetch<AckRow>(new Sql($"SELECT * FROM {Constants.Tables.Acks} WHERE EventId IN (@ids)", new { ids = chunk }))
                .GroupBy(row => row.EventId))
            {
                acks[group.Key] = group.Select(row => row.Server).ToHashSet(StringComparer.Ordinal);
            }
        }

        return acks;
    }

    public static void Prune(IDatabase database, DateTime cutoffUtc)
    {
        database.Execute($"DELETE FROM {Constants.Tables.Servers} WHERE LastSeenUtc < @0", cutoffUtc);
        database.Execute($"DELETE FROM {Constants.Tables.Acks} WHERE AckedUtc < @0", cutoffUtc);
    }
}
