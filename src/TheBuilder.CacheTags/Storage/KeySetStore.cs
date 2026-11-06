using System.Security.Cryptography;
using System.Text;
using NPoco;
using TheBuilder.CacheTags.Keys;

namespace TheBuilder.CacheTags.Storage;

/// <summary>
///     The stored key sets, in the Umbraco database. A set is content-addressed: its hash covers its
///     sorted item keys, so every response showing the same items shares one set, and storing a set
///     that is already stored only marks it seen.
/// </summary>
internal sealed class KeySetStore(PackageDatabase database)
{
    // Below SQL Server's 2100 parameters per query.
    private const int Batch = 1000;

    // The item rows inserted by one command.
    private const int ItemsPerCommand = 200;

    /// <summary>
    ///     The hash a set of these items is stored under, and its <c>set-</c> key carries: the first
    ///     128 bits of the SHA-256 of their sorted keys, in hex.
    /// </summary>
    public static string HashOf(IEnumerable<string> items)
    {
        var joined = string.Join('\n', items.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(joined)).AsSpan(0, 16));
    }

    /// <summary>
    ///     Stores the set of these items under <paramref name="hash" />, or marks it seen when it is
    ///     stored already, in one transaction. Stores nothing and returns <c>false</c> when the set is
    ///     new and <paramref name="mayCreate" /> says no.
    /// </summary>
    public bool Add(string hash, IReadOnlyCollection<string> items, TimeProvider time, Func<bool> mayCreate)
        => database.Write(db =>
        {
            // Read once the write lock is held, not when the response asked for the set: a release
            // that looks the sets up while this write waits for the lock cannot see it, and the
            // second look finds it by this time.
            DateTime now = time.GetUtcNow().UtcDateTime;
            if (db.Execute($"UPDATE {Constants.Tables.KeySets} SET LastSeenUtc = @0 WHERE Hash = @1", now, hash) == 1)
            {
                return true;
            }

            if (mayCreate() is false)
            {
                return false;
            }

            db.Insert(new KeySetRow { Hash = hash, CreatedUtc = now, LastSeenUtc = now });

            // Many rows to a command: a set holds hundreds of items, and the write lock is held while
            // they go in. Two parameters a row keeps a batch well below SQL Server's 2100.
            db.InsertBatch(items.Distinct(StringComparer.Ordinal).Select(item => new KeySetItemRow { Hash = hash, ItemKey = item }), new BatchOptions { BatchSize = ItemsPerCommand });
            return true;
        });

    /// <summary>
    ///     The <c>set-</c> keys of the sets that hold any of these items, optionally only the sets
    ///     stored since a time.
    /// </summary>
    public IReadOnlyList<string> SetsOf(IEnumerable<string> items, DateTime? createdSinceUtc = null)
    {
        var keys = items.Where(CacheKeys.IsItem).Distinct(StringComparer.Ordinal).ToList();
        if (keys.Count == 0)
        {
            return [];
        }

        return database.Read(db => keys
                .Chunk(Batch)
                .SelectMany(batch => db.Fetch<string>(SetsSql(batch, createdSinceUtc)))
                .ToList())
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Select(hash => CacheKeys.SetPrefix + hash)
            .ToList();
    }

    /// <summary>How many stored sets hold the item.</summary>
    public int CountOf(string itemKey)
        => database.Read(db => db.ExecuteScalar<int>($"SELECT COUNT(*) FROM {Constants.Tables.KeySetItems} WHERE ItemKey = @0", itemKey));

    /// <summary>The <c>set-</c> keys of up to <paramref name="take" /> sets no server has rendered since the cutoff, oldest first.</summary>
    public IReadOnlyList<string> Unseen(DateTime cutoffUtc, int take)
        => database.Read(db => db.SkipTake<string>(0, take, new Sql(
                $"SELECT Hash FROM {Constants.Tables.KeySets} WHERE LastSeenUtc < @0 ORDER BY LastSeenUtc",
                cutoffUtc)))
            .Select(hash => CacheKeys.SetPrefix + hash)
            .ToList();

    /// <summary>
    ///     Removes these sets, with their items, unless a server has rendered one since the cutoff.
    ///     Only once their purge is recorded: a response cached for longer still carries one.
    /// </summary>
    public void Remove(IReadOnlyList<string> sets, DateTime cutoffUtc)
        => database.Write(db =>
        {
            foreach (var batch in sets.Select(set => set[CacheKeys.SetPrefix.Length..]).Chunk(Batch))
            {
                db.Execute(new Sql($"DELETE FROM {Constants.Tables.KeySetItems} WHERE Hash IN (SELECT Hash FROM {Constants.Tables.KeySets} WHERE Hash IN (@0) AND LastSeenUtc < @1)", batch, cutoffUtc));
                db.Execute(new Sql($"DELETE FROM {Constants.Tables.KeySets} WHERE Hash IN (@0) AND LastSeenUtc < @1", batch, cutoffUtc));
            }
        });

    private static Sql SetsSql(string[] items, DateTime? createdSinceUtc)
        => createdSinceUtc is { } since
            ? new Sql(
                $"SELECT DISTINCT i.Hash FROM {Constants.Tables.KeySetItems} i INNER JOIN {Constants.Tables.KeySets} s ON s.Hash = i.Hash WHERE i.ItemKey IN (@0) AND s.CreatedUtc >= @1",
                items,
                since)
            : new Sql($"SELECT DISTINCT Hash FROM {Constants.Tables.KeySetItems} WHERE ItemKey IN (@0)", (object)items);
}
