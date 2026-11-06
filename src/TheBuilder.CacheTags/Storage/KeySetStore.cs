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

    /// <summary>The <c>set-</c> key of these items: the first 128 bits of the SHA-256 of their sorted keys, in hex.</summary>
    public static string KeyOf(IEnumerable<string> items) => CacheKeys.SetPrefix + HashOf(items);

    /// <summary>The hash a set of these items is stored under.</summary>
    public static string HashOf(IEnumerable<string> items)
    {
        var joined = string.Join('\n', items.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(joined)).AsSpan(0, 16));
    }

    /// <summary>
    ///     Stores the set of these items, or marks it seen when it is stored already, in one
    ///     transaction. Returns the set's hash.
    /// </summary>
    public string Add(IReadOnlyCollection<string> items, DateTime nowUtc)
    {
        var hash = HashOf(items);
        database.Write(db =>
        {
            if (db.Execute($"UPDATE {Constants.Tables.KeySets} SET LastSeenUtc = @0 WHERE Hash = @1", nowUtc, hash) == 1)
            {
                return;
            }

            db.Insert(new KeySetRow { Hash = hash, CreatedUtc = nowUtc, LastSeenUtc = nowUtc });
            foreach (var item in items.Distinct(StringComparer.Ordinal))
            {
                db.Insert(new KeySetItemRow { Hash = hash, ItemKey = item });
            }
        });
        return hash;
    }

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

    /// <summary>
    ///     Removes up to <paramref name="take" /> sets no server has rendered since the cutoff, with
    ///     their items, and returns their <c>set-</c> keys, so they can be purged: a response cached
    ///     longer than that still carries one.
    /// </summary>
    public IReadOnlyList<string> Prune(DateTime cutoffUtc, int take)
        => database.Write(db =>
        {
            List<string> hashes = db.SkipTake<string>(0, take, new Sql(
                $"SELECT Hash FROM {Constants.Tables.KeySets} WHERE LastSeenUtc < @0 ORDER BY LastSeenUtc",
                cutoffUtc));
            foreach (var batch in hashes.Chunk(Batch))
            {
                db.Execute(new Sql($"DELETE FROM {Constants.Tables.KeySetItems} WHERE Hash IN (@0)", (object)batch));
                db.Execute(new Sql($"DELETE FROM {Constants.Tables.KeySets} WHERE Hash IN (@0)", (object)batch));
            }

            return (IReadOnlyList<string>)hashes.Select(hash => CacheKeys.SetPrefix + hash).ToList();
        });

    private static Sql SetsSql(string[] items, DateTime? createdSinceUtc)
        => createdSinceUtc is { } since
            ? new Sql(
                $"SELECT DISTINCT i.Hash FROM {Constants.Tables.KeySetItems} i INNER JOIN {Constants.Tables.KeySets} s ON s.Hash = i.Hash WHERE i.ItemKey IN (@0) AND s.CreatedUtc >= @1",
                items,
                since)
            : new Sql($"SELECT DISTINCT Hash FROM {Constants.Tables.KeySetItems} WHERE ItemKey IN (@0)", (object)items);
}
