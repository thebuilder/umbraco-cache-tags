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

    /// <summary>
    ///     The changes released since a time without a server's acknowledgement, newest first.
    ///     <paramref name="missingToken" /> is the server's name as JSON, as <c>Missing</c> lists it.
    /// </summary>
    public static List<EventRow> ReleasedWithout(IDatabase database, string missingToken, DateTime sinceUtc, int take)
        => database.SkipTake<EventRow>(0, take, new Sql(
            $"SELECT * FROM {Constants.Tables.Events} WHERE ReleasedUtc >= @0 AND Status <> @1 AND Missing LIKE @2 ESCAPE '\\' ORDER BY ReleasedUtc DESC",
            sinceUtc,
            nameof(ChangeStage.Pending),
            "%" + EscapeLike(missingToken) + "%"));

    private static string EscapeLike(string value)
        => value.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_").Replace("[", @"\[");

    /// <summary>
    ///     The keys of every purge of the changes released in the window whose purges were all sent,
    ///     with the change each belongs to.
    /// </summary>
    public static List<DeliveredRow> Delivered(IDatabase database, DateTime releasedFromUtc, DateTime releasedBeforeUtc)
        => database.Fetch<DeliveredRow>(
            $"SELECT e.Id AS EventId, e.CreatedUtc, o.Keys FROM {Constants.Tables.Outbox} o INNER JOIN {Constants.Tables.Events} e ON e.Id = o.EventId WHERE e.Status = @0 AND e.ReleasedUtc >= @1 AND e.ReleasedUtc < @2 AND NOT EXISTS (SELECT 1 FROM {Constants.Tables.Outbox} x WHERE x.EventId = e.Id AND x.State <> @3)",
            nameof(ChangeStage.Released),
            releasedFromUtc,
            releasedBeforeUtc,
            OutboxStates.Sent);

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

    /// <summary>
    ///     Leases a purge that is still due to this server. A server working from an older list of due
    ///     purges must not claim one another server has just failed and put off.
    /// </summary>
    public static bool ClaimSend(IDatabase database, Guid id, string owner, DateTime nowUtc, DateTime leaseUntilUtc)
        => database.Execute(
            $"UPDATE {Constants.Tables.Outbox} SET LeaseOwner = @0, LeaseUntilUtc = @1 WHERE Id = @2 AND State = @3 AND NotBeforeUtc <= @4 AND (LeaseUntilUtc IS NULL OR LeaseUntilUtc < @4)",
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

    /// <summary>
    ///     Records a failed attempt, counted from the row rather than from what the sender read: the
    ///     purge is tried again after a backoff, or given up after the last attempt. Returns the attempt
    ///     this was, or <c>null</c> when this server no longer holds the lease.
    /// </summary>
    public static int? Failed(IDatabase database, Guid id, string owner, int maxAttempts, string error, DateTime nowUtc)
    {
        var attempts = database.ExecuteScalar<int?>(
            $"SELECT Attempts FROM {Constants.Tables.Outbox} WHERE Id = @0 AND LeaseOwner = @1", id, owner) + 1;
        if (attempts is not { } attempt)
        {
            return null;
        }

        database.Execute(
            $"UPDATE {Constants.Tables.Outbox} SET State = @0, Attempts = @1, LastError = @2, NotBeforeUtc = @3, LeaseOwner = NULL, LeaseUntilUtc = NULL WHERE Id = @4 AND LeaseOwner = @5",
            attempt >= maxAttempts ? OutboxStates.Dead : OutboxStates.Pending,
            attempt,
            error.Length > 2000 ? error[..2000] : error,
            nowUtc + Backoff.After(attempt),
            id,
            owner);
        return attempt;
    }

    /// <summary>The purges in a state, newest first.</summary>
    public static List<OutboxRow> InState(IDatabase database, string state, int take)
        => database.SkipTake<OutboxRow>(0, take, new Sql(
            $"SELECT * FROM {Constants.Tables.Outbox} WHERE State = @0 ORDER BY CreatedUtc DESC",
            state));

    /// <summary>Sends a dead or discarded purge again, from its first attempt. Returns whether there was one.</summary>
    public static bool Retry(IDatabase database, Guid id, DateTime nowUtc)
        => database.Execute(
            $"UPDATE {Constants.Tables.Outbox} SET State = @0, Attempts = 0, LastError = NULL, NotBeforeUtc = @1, LeaseOwner = NULL, LeaseUntilUtc = NULL WHERE Id = @2 AND (State = @3 OR State = @4)",
            OutboxStates.Pending,
            nowUtc,
            id,
            OutboxStates.Dead,
            OutboxStates.Discarded) == 1;

    /// <summary>Sets a pending or dead purge aside, so it is not sent. Returns whether there was one.</summary>
    public static bool Discard(IDatabase database, Guid id)
        => database.Execute(
            $"UPDATE {Constants.Tables.Outbox} SET State = @0 WHERE Id = @1 AND (State = @2 OR State = @3)",
            OutboxStates.Discarded,
            id,
            OutboxStates.Pending,
            OutboxStates.Dead) == 1;

    public static void Prune(IDatabase database, DateTime cutoffUtc)
    {
        database.Execute(
            $"DELETE FROM {Constants.Tables.Outbox} WHERE (State = @0 OR State = @1 OR State = @2) AND CreatedUtc < @3",
            OutboxStates.Sent,
            OutboxStates.Dead,
            OutboxStates.Discarded,
            cutoffUtc);
        database.Execute(
            $"DELETE FROM {Constants.Tables.Events} WHERE Status = @0 AND CreatedUtc < @1 AND Id NOT IN (SELECT EventId FROM {Constants.Tables.Outbox})",
            nameof(ChangeStage.Released),
            cutoffUtc);
    }
}
