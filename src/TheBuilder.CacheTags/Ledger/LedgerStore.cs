using System.Text.Json;
using TheBuilder.CacheTags.Delivery;
using TheBuilder.CacheTags.Storage;

namespace TheBuilder.CacheTags.Ledger;

/// <summary>A recorded change, with its plan and signal read back from the ledger.</summary>
internal sealed record LedgerEntry(
    Guid Id,
    DateTime CreatedUtc,
    string Kind,
    string Server,
    JsonElement Change,
    PurgePlan Plan,
    AckSignal? Signal,
    string Status,
    DateTime? ReleasedUtc,
    IReadOnlyList<string> NotAcknowledgedBy);

/// <summary>A purge request to one target, as queued in the outbox.</summary>
internal sealed record Purge(
    Guid Id,
    Guid ChangeId,
    string Target,
    IReadOnlyList<string> Keys,
    string State,
    int Attempts,
    DateTime NotBeforeUtc,
    DateTime? SentUtc,
    string? LastError);

/// <summary>A purge to queue when a change is released.</summary>
internal sealed record NewPurge(string Target, IReadOnlyList<string> Keys, DateTime NotBeforeUtc);

/// <summary>
///     The ledger: recorded changes and the purges queued for them, in the Umbraco database. The only
///     place that knows how changes and purges are stored; everything else works with the records
///     above.
/// </summary>
internal sealed class LedgerStore(PackageDatabase database)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Records a change. Recording one that is already recorded does nothing, so a retry after a lost reply is safe.</summary>
    public void Add(Change change, PurgePlan plan, string server)
        => database.Write(db => LedgerSql.Exists(db, change.Id) || db.Insert(new EventRow
        {
            Id = change.Id,
            CreatedUtc = change.CreatedUtc,
            Kind = change.Kind.ToString(),
            Server = server,
            Change = JsonSerializer.Serialize(change, Json),
            Plan = JsonSerializer.Serialize(plan, Json),
            Signal = JsonSerializer.Serialize(AckSignal.For(change), Json),
            Status = EventStatuses.Pending,
        }) is not null);

    public IReadOnlyList<LedgerEntry> Unreleased(int take)
        => database.Read(db => LedgerSql.Unreleased(db, take)).Select(Entry).ToList();

    public IReadOnlyList<LedgerEntry> Latest(int take)
        => database.Read(db => LedgerSql.Latest(db, take)).Select(Entry).ToList();

    /// <summary>Takes the lease on releasing a change. False when another server holds it, or it is released.</summary>
    public bool ClaimRelease(Guid changeId, string owner, DateTime nowUtc, DateTime leaseUntilUtc)
        => database.Write(db => LedgerSql.ClaimRelease(db, changeId, owner, nowUtc, leaseUntilUtc));

    /// <summary>Marks a change released and queues its purges, in one transaction, if this server still holds the lease.</summary>
    public bool CompleteRelease(Guid changeId, string owner, IReadOnlyList<string> notAcknowledgedBy, bool timedOut, DateTime nowUtc, IEnumerable<NewPurge> purges)
        => database.Write(db => LedgerSql.CompleteRelease(
            db,
            changeId,
            owner,
            timedOut ? EventStatuses.ReleasedAfterTimeout : EventStatuses.Released,
            nowUtc,
            notAcknowledgedBy.Count == 0 ? null : JsonSerializer.Serialize(notAcknowledgedBy, Json),
            purges.Select(purge => new OutboxRow
            {
                Id = Guid.CreateVersion7(),
                EventId = changeId,
                Target = purge.Target,
                Keys = JsonSerializer.Serialize(purge.Keys, Json),
                State = OutboxStates.Pending,
                CreatedUtc = nowUtc,
                NotBeforeUtc = purge.NotBeforeUtc,
            })));

    /// <summary>The purges due now, and how many are pending at all (now or later).</summary>
    public (IReadOnlyList<Purge> Due, int Pending) Due(DateTime nowUtc, int take)
        => database.Read(db => (
            (IReadOnlyList<Purge>)LedgerSql.Due(db, nowUtc, take).Select(Purge).ToList(),
            LedgerSql.PendingCount(db)));

    public IReadOnlyList<Purge> PurgesOf(Guid changeId)
        => database.Read(db => db.Fetch<OutboxRow>($"SELECT * FROM {Constants.Tables.Outbox} WHERE EventId = @0 ORDER BY NotBeforeUtc", changeId))
            .Select(Purge)
            .ToList();

    public bool ClaimSend(Guid purgeId, string owner, DateTime nowUtc, DateTime leaseUntilUtc)
        => database.Write(db => LedgerSql.ClaimSend(db, purgeId, owner, nowUtc, leaseUntilUtc));

    public void Sent(Guid purgeId, string owner, DateTime nowUtc)
        => database.Write(db => LedgerSql.Sent(db, purgeId, owner, nowUtc));

    public void Failed(Guid purgeId, string owner, int attempts, int maxAttempts, string error, DateTime nowUtc)
        => database.Write(db => LedgerSql.Failed(db, purgeId, owner, attempts, maxAttempts, error, nowUtc));

    public void Prune(DateTime cutoffUtc) => database.Write(db => LedgerSql.Prune(db, cutoffUtc));

    private static LedgerEntry Entry(EventRow row) => new(
        row.Id,
        row.CreatedUtc,
        row.Kind,
        row.Server,
        JsonSerializer.Deserialize<JsonElement>(row.Change),
        // A plan that cannot be read back is treated as purging everything: over-purging is safe.
        JsonSerializer.Deserialize<PurgePlan>(row.Plan, Json) ?? new PurgePlan([], Everything: true, Cultures: null),
        row.Signal is null ? null : JsonSerializer.Deserialize<AckSignal>(row.Signal, Json),
        row.Status ?? EventStatuses.Released,
        row.ReleasedUtc,
        row.Missing is null ? [] : JsonSerializer.Deserialize<string[]>(row.Missing) ?? []);

    private static Purge Purge(OutboxRow row) => new(
        row.Id,
        row.EventId,
        row.Target,
        JsonSerializer.Deserialize<string[]>(row.Keys, Json) ?? [],
        row.State,
        row.Attempts,
        row.NotBeforeUtc,
        row.SentUtc,
        row.LastError);
}
