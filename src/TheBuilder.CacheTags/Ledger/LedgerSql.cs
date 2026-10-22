using NPoco;
using TheBuilder.CacheTags.Delivery;
using TheBuilder.CacheTags.Storage;

namespace TheBuilder.CacheTags.Ledger;

/// <summary>
///     The ledger's SQL, against any NPoco database. Every server runs the same loop. A change moves
///     on a stage only by a conditional update in the same transaction as the purges it queues, so
///     whichever server gets there first queues them and the others do nothing. Sending a purge is
///     not transactional, so a lease (an owner and an expiry) makes sure one server sends it at a
///     time, and lets another take over if it stops.
/// </summary>
internal static class LedgerSql
{
    public static bool Exists(IDatabase database, Guid id)
        => database.ExecuteScalar<int>($"SELECT COUNT(*) FROM {Constants.Tables.Events} WHERE Id = @0", id) > 0;

    /// <summary>The changes at a stage, oldest first.</summary>
    public static List<EventRow> InStage(IDatabase database, ChangeStage stage, int take)
        => database.SkipTake<EventRow>(0, take, new Sql(
            $"SELECT * FROM {Constants.Tables.Events} WHERE Status = @0 ORDER BY CreatedUtc",
            stage.ToString()));

    public static List<EventRow> Latest(IDatabase database, int take)
        => database.SkipTake<EventRow>(0, take, new Sql($"SELECT * FROM {Constants.Tables.Events} ORDER BY CreatedUtc DESC"));

    /// <summary>Releases a pending change to <paramref name="stage" /> and queues its purges, unless another server already has. Returns whether this call did.</summary>
    public static bool Release(IDatabase database, Guid id, ChangeStage stage, DateTime releasedUtc, string? missing, IEnumerable<OutboxRow> purges)
        => Advance(
            database,
            purges,
            $"UPDATE {Constants.Tables.Events} SET Status = @0, ReleasedUtc = @1, Missing = @2 WHERE Id = @3 AND Status = @4",
            stage.ToString(),
            releasedUtc,
            missing ?? (object)DBNull.Value,
            id,
            nameof(ChangeStage.Pending));

    /// <summary>Releases an indexing change's listing purges, unless another server already has. Returns whether this call did.</summary>
    public static bool ReleaseListings(IDatabase database, Guid id, IEnumerable<OutboxRow> purges)
        => Advance(
            database,
            purges,
            $"UPDATE {Constants.Tables.Events} SET Status = @0 WHERE Id = @1 AND Status = @2",
            nameof(ChangeStage.Released),
            id,
            nameof(ChangeStage.Indexing));

    private static bool Advance(IDatabase database, IEnumerable<OutboxRow> purges, string sql, params object[] args)
    {
        var advanced = database.Execute(sql, args) == 1;
        if (advanced)
        {
            foreach (OutboxRow purge in purges)
            {
                database.Insert(purge);
            }
        }

        return advanced;
    }

    public static List<OutboxRow> Due(IDatabase database, DateTime nowUtc, int take)
        => database.SkipTake<OutboxRow>(0, take, new Sql(
            $"SELECT * FROM {Constants.Tables.Outbox} WHERE State = @0 AND NotBeforeUtc <= @1 AND (LeaseUntilUtc IS NULL OR LeaseUntilUtc < @1) ORDER BY NotBeforeUtc",
            OutboxStates.Pending,
            nowUtc));

    public static int PendingCount(IDatabase database)
        => database.ExecuteScalar<int>($"SELECT COUNT(*) FROM {Constants.Tables.Outbox} WHERE State = @0", OutboxStates.Pending);

    public static bool ClaimSend(IDatabase database, Guid id, string owner, DateTime nowUtc, DateTime leaseUntilUtc)
        => database.Execute(
            $"UPDATE {Constants.Tables.Outbox} SET LeaseOwner = @0, LeaseUntilUtc = @1 WHERE Id = @2 AND State = @3 AND (LeaseUntilUtc IS NULL OR LeaseUntilUtc < @4)",
            owner,
            leaseUntilUtc,
            id,
            OutboxStates.Pending,
            nowUtc) == 1;

    public static void Sent(IDatabase database, Guid id, string owner, DateTime nowUtc)
        => database.Execute(
            $"UPDATE {Constants.Tables.Outbox} SET State = @0, SentUtc = @1, Attempts = Attempts + 1, LastError = NULL, LeaseOwner = NULL, LeaseUntilUtc = NULL WHERE Id = @2 AND LeaseOwner = @3",
            OutboxStates.Sent,
            nowUtc,
            id,
            owner);

    /// <summary>Records a failed attempt; the purge is tried again after a backoff, or given up after the last attempt.</summary>
    public static void Failed(IDatabase database, Guid id, string owner, int attempts, int maxAttempts, string error, DateTime nowUtc)
        => database.Execute(
            $"UPDATE {Constants.Tables.Outbox} SET State = @0, Attempts = @1, LastError = @2, NotBeforeUtc = @3, LeaseOwner = NULL, LeaseUntilUtc = NULL WHERE Id = @4 AND LeaseOwner = @5",
            attempts >= maxAttempts ? OutboxStates.Dead : OutboxStates.Pending,
            attempts,
            error.Length > 2000 ? error[..2000] : error,
            nowUtc + Backoff.After(attempts),
            id,
            owner);

    public static void Prune(IDatabase database, DateTime cutoffUtc)
    {
        database.Execute(
            $"DELETE FROM {Constants.Tables.Outbox} WHERE (State = @0 OR State = @1) AND CreatedUtc < @2",
            OutboxStates.Sent,
            OutboxStates.Dead,
            cutoffUtc);
        database.Execute(
            $"DELETE FROM {Constants.Tables.Events} WHERE Status = @0 AND CreatedUtc < @1 AND Id NOT IN (SELECT EventId FROM {Constants.Tables.Outbox})",
            nameof(ChangeStage.Released),
            cutoffUtc);
    }
}
