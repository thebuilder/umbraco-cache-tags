using System.Text.Json;
using TheBuilder.CacheTags.Configuration;
using TheBuilder.CacheTags.Delivery;
using TheBuilder.CacheTags.Keys;
using TheBuilder.CacheTags.Storage;
using TheBuilder.CacheTags.Targets;

namespace TheBuilder.CacheTags.Ledger;

/// <summary>A recorded change, with its plan and signal, read back from the ledger.</summary>
/// <param name="Change">What happened.</param>
/// <param name="Server">The server that recorded it.</param>
/// <param name="Plan">The keys it purges.</param>
/// <param name="Signal">What a server's refresh must show to acknowledge it.</param>
/// <param name="Stage">Where it is on its way to the frontends.</param>
/// <param name="ReleasedUtc">When its purge was queued.</param>
/// <param name="NotAcknowledgedBy">The servers it was released without, after the ack timeout.</param>
internal sealed record LedgerEntry(
    Change Change,
    string Server,
    PurgePlan Plan,
    AckSignal? Signal,
    ChangeStage Stage,
    DateTime? ReleasedUtc,
    IReadOnlyList<string> NotAcknowledgedBy)
{
    public Guid Id => Change.Id;

    public DateTime CreatedUtc => Change.CreatedUtc;
}

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

/// <summary>A change whose every purge was sent.</summary>
/// <param name="CreatedUtc">When the change was recorded.</param>
/// <param name="ResponseKeys">The <c>response-</c> keys its purges expired, without the configured prefix.</param>
internal sealed record DeliveredChange(DateTime CreatedUtc, IReadOnlyList<string> ResponseKeys);

/// <summary>A purge to queue when a change is released.</summary>
internal sealed record NewPurge(string Target, IReadOnlyList<string> Keys, DateTime NotBeforeUtc)
{
    /// <summary>The purges of these keys to every enabled target, split at each target's <c>MaxKeysPerRequest</c>.</summary>
    public static IReadOnlyList<NewPurge> ToTargets(IEnumerable<ResolvedTarget> targets, IReadOnlyList<string> keys, DateTime nowUtc)
        => keys.Count == 0
            ? []
            : targets
                .Where(target => target.Options.Enabled)
                .SelectMany(target => keys.Chunk(target.Options.MaxKeysPerRequest).Select(chunk => new NewPurge(target.Name, chunk, nowUtc)))
                .ToList();
}

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
            Status = nameof(ChangeStage.Pending),
        }) is not null);

    /// <summary>The changes at a stage, oldest first.</summary>
    public IReadOnlyList<LedgerEntry> InStage(ChangeStage stage, int take)
        => database.Read(db => LedgerSql.InStage(db, stage, take)).Select(Entry).ToList();

    /// <summary>The changes released since a time without <paramref name="server" />'s acknowledgement, after the ack timeout.</summary>
    public IReadOnlyList<LedgerEntry> ReleasedWithout(string server, DateTime sinceUtc, int take)
        => database.Read(db => LedgerSql.ReleasedWithout(db, JsonSerializer.Serialize(server, Json), sinceUtc, take))
            .Select(Entry)
            // SQLite's LIKE ignores case.
            .Where(change => change.NotAcknowledgedBy.Contains(server, StringComparer.Ordinal))
            .ToList();

    public IReadOnlyList<LedgerEntry> Latest(int take)
        => database.Read(db => LedgerSql.Latest(db, take)).Select(Entry).ToList();

    /// <summary>
    ///     Releases a pending change to <paramref name="stage" /> and queues its purges, in one
    ///     transaction, unless another server already has. Returns whether this call did.
    /// </summary>
    public bool Release(Guid changeId, ChangeStage stage, IReadOnlyList<string> notAcknowledgedBy, DateTime nowUtc, IEnumerable<NewPurge> purges)
        => database.Write(db => LedgerSql.Release(
            db,
            changeId,
            stage,
            nowUtc,
            notAcknowledgedBy.Count == 0 ? null : JsonSerializer.Serialize(notAcknowledgedBy, Json),
            Rows(changeId, nowUtc, purges)));

    /// <summary>Queues an indexing change's listing purges, in one transaction, unless another server already has. Returns whether this call did.</summary>
    public bool ReleaseListings(Guid changeId, DateTime nowUtc, IEnumerable<NewPurge> purges)
        => database.Write(db => LedgerSql.ReleaseListings(db, changeId, Rows(changeId, nowUtc, purges)));

    /// <summary>The purges due now, and how many are pending at all (now or later).</summary>
    public (IReadOnlyList<Purge> Due, int Pending) Due(DateTime nowUtc, int take)
        => database.Read(db => (
            (IReadOnlyList<Purge>)LedgerSql.Due(db, nowUtc, take).Select(Purge).ToList(),
            LedgerSql.PendingCount(db)));

    public IReadOnlyList<Purge> PurgesOf(Guid changeId)
        => database.Read(db => db.Fetch<OutboxRow>($"SELECT * FROM {Constants.Tables.Outbox} WHERE EventId = @0 ORDER BY NotBeforeUtc", changeId))
            .Select(Purge)
            .ToList();

    /// <summary>The changes released before the cutoff whose purges were all sent and named a response.</summary>
    public IReadOnlyList<DeliveredChange> Delivered(DateTime releasedBeforeUtc, string prefix)
        => database.Read(db => LedgerSql.Delivered(db, releasedBeforeUtc))
            .GroupBy(row => (row.EventId, row.CreatedUtc))
            .Select(change => new DeliveredChange(
                change.Key.CreatedUtc,
                change
                    .SelectMany(row => JsonSerializer.Deserialize<string[]>(row.Keys, Json) ?? [])
                    .Select(key => CacheKeys.WithoutPrefix(prefix, key))
                    .Where(CacheKeys.IsResponse)
                    .Distinct(StringComparer.Ordinal)
                    .ToList()))
            .Where(change => change.ResponseKeys.Count > 0)
            .ToList();

    /// <summary>The purges in a state (Pending, Sent, Dead, Discarded), newest first.</summary>
    public IReadOnlyList<Purge> InState(string state, int take)
        => database.Read(db => LedgerSql.InState(db, state, take)).Select(Purge).ToList();

    public bool Retry(Guid purgeId, DateTime nowUtc) => database.Write(db => LedgerSql.Retry(db, purgeId, nowUtc));

    public bool Discard(Guid purgeId) => database.Write(db => LedgerSql.Discard(db, purgeId));

    public bool ClaimSend(Guid purgeId, string owner, DateTime nowUtc, DateTime leaseUntilUtc)
        => database.Write(db => LedgerSql.ClaimSend(db, purgeId, owner, nowUtc, leaseUntilUtc));

    public void Sent(Guid purgeId, string owner, DateTime nowUtc)
        => database.Write(db => LedgerSql.Sent(db, purgeId, owner, nowUtc));

    /// <summary>Records a failed attempt. Returns the attempt it was, or <c>null</c> when <paramref name="owner" /> no longer holds the lease.</summary>
    public int? Failed(Guid purgeId, string owner, int maxAttempts, string error, DateTime nowUtc)
        => database.Write(db => LedgerSql.Failed(db, purgeId, owner, maxAttempts, error, nowUtc));

    public void Prune(DateTime cutoffUtc) => database.Write(db => LedgerSql.Prune(db, cutoffUtc));

    private static IEnumerable<OutboxRow> Rows(Guid changeId, DateTime nowUtc, IEnumerable<NewPurge> purges)
        => purges.Select(purge => new OutboxRow
        {
            Id = Guid.CreateVersion7(),
            EventId = changeId,
            Target = purge.Target,
            Keys = JsonSerializer.Serialize(purge.Keys, Json),
            State = OutboxStates.Pending,
            CreatedUtc = nowUtc,
            NotBeforeUtc = purge.NotBeforeUtc,
        }).ToList();

    private static LedgerEntry Entry(EventRow row) => new(
        JsonSerializer.Deserialize<Change>(row.Change, Json)!,
        row.Server,
        // A plan that cannot be read back is treated as purging everything: over-purging is safe.
        JsonSerializer.Deserialize<PurgePlan>(row.Plan, Json) ?? new PurgePlan([], Everything: true, Cultures: null),
        row.Signal is null ? null : JsonSerializer.Deserialize<AckSignal>(row.Signal, Json),
        Enum.Parse<ChangeStage>(row.Status ?? nameof(ChangeStage.Released)),
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
