using NPoco;
using TheBuilder.CacheTags.Delivery;
using TheBuilder.CacheTags.Storage;

namespace TheBuilder.CacheTags.Ledger;

/// <summary>
///     Releasing changes and sending purges, against any NPoco database. Every server runs the same
///     loop; a lease (an owner and an expiry, set by a conditional update) makes sure one server
///     handles each change and each purge at a time, and lets another take over if it stops.
/// </summary>
internal static class LedgerSql
{
    public static List<EventRow> Unreleased(IDatabase database, int take)
        => database.SkipTake<EventRow>(0, take, new Sql(
            $"SELECT * FROM {Constants.Tables.Events} WHERE Status = @0 OR Status = @1 ORDER BY CreatedUtc",
            EventStatuses.Pending,
            EventStatuses.Releasing));

    public static List<EventRow> Latest(IDatabase database, int take)
        => database.SkipTake<EventRow>(0, take, new Sql($"SELECT * FROM {Constants.Tables.Events} ORDER BY CreatedUtc DESC"));

    /// <summary>Takes the lease on releasing a change. False when another server holds it, or it is released.</summary>
    public static bool ClaimRelease(IDatabase database, Guid id, string owner, DateTime nowUtc, DateTime leaseUntilUtc)
        => database.Execute(
            $"UPDATE {Constants.Tables.Events} SET Status = @0, LeaseOwner = @1, LeaseUntilUtc = @2 WHERE Id = @3 AND (Status = @4 OR (Status = @0 AND LeaseUntilUtc < @5))",
            EventStatuses.Releasing,
            owner,
            leaseUntilUtc,
            id,
            EventStatuses.Pending,
            nowUtc) == 1;

    /// <summary>Queues the purges and marks the change released, if this server still holds the lease.</summary>
    public static bool CompleteRelease(IDatabase database, Guid id, string owner, string status, DateTime nowUtc, string? missing, IEnumerable<OutboxRow> purges)
    {
        var released = database.Execute(
            $"UPDATE {Constants.Tables.Events} SET Status = @0, ReleasedUtc = @1, Missing = @2, LeaseOwner = NULL, LeaseUntilUtc = NULL WHERE Id = @3 AND LeaseOwner = @4",
            status,
            nowUtc,
            missing ?? (object)DBNull.Value,
            id,
            owner) == 1;
        if (released)
        {
            foreach (OutboxRow purge in purges)
            {
                database.Insert(purge);
            }
        }

        return released;
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
            $"DELETE FROM {Constants.Tables.Events} WHERE (Status = @0 OR Status = @1) AND CreatedUtc < @2 AND Id NOT IN (SELECT EventId FROM {Constants.Tables.Outbox})",
            EventStatuses.Released,
            EventStatuses.ReleasedAfterTimeout,
            cutoffUtc);
    }
}
