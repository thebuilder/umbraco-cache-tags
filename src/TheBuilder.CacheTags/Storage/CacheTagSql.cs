using NPoco;

namespace TheBuilder.CacheTags.Storage;

/// <summary>
///     The mapping store's SQL, against any NPoco database, so it runs the same on SQL Server, SQLite
///     and in the tests. Upserts are an update followed by an insert when nothing was updated, which
///     every provider supports.
/// </summary>
internal static class CacheTagSql
{
    // SQL Server allows 2100 parameters per command; stay well under.
    private const int ChunkSize = 500;

    public static void Record(IDatabase database, CachedResponse response)
    {
        var updated = database.Execute(
            $"UPDATE {Constants.Tables.Responses} SET RenderedUtc = @0, SeenUtc = @0 WHERE ResponseKey = @1",
            response.RenderedUtc,
            response.ResponseKey);
        if (updated == 0)
        {
            database.Insert(new ResponseRow
            {
                ResponseKey = response.ResponseKey,
                Path = response.Path,
                Query = response.Query,
                Culture = response.Culture,
                Segment = response.Segment,
                StartItem = response.StartItem,
                RenderedUtc = response.RenderedUtc,
                SeenUtc = response.SeenUtc,
            });
        }

        foreach (var key in response.Keys.Distinct(StringComparer.Ordinal))
        {
            var refreshed = database.Execute(
                $"UPDATE {Constants.Tables.ResponseKeys} SET RenderedUtc = @0 WHERE ResponseKey = @1 AND CacheKey = @2",
                response.RenderedUtc,
                response.ResponseKey,
                key);
            if (refreshed == 0)
            {
                database.Insert(new ResponseKeyRow { ResponseKey = response.ResponseKey, CacheKey = key, RenderedUtc = response.RenderedUtc });
            }
        }
    }

    public static void Touch(IDatabase database, IReadOnlyCollection<string> responseKeys, DateTime seenUtc)
    {
        foreach (var chunk in responseKeys.Distinct(StringComparer.Ordinal).Chunk(ChunkSize))
        {
            database.Execute(
                new Sql($"UPDATE {Constants.Tables.Responses} SET SeenUtc = @seen WHERE SeenUtc < @seen AND ResponseKey IN (@keys)", new { seen = seenUtc, keys = chunk }));
        }
    }

    public static IReadOnlyCollection<string> FindResponseKeys(IDatabase database, IReadOnlyCollection<string> keys, IReadOnlyCollection<string>? cultures)
    {
        var all = cultures is null || cultures.Count == 0 || cultures.Contains("*");
        var lowered = all ? [] : cultures!.Select(c => c.ToLowerInvariant()).Append(string.Empty).Distinct().ToArray();
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var chunk in keys.Distinct(StringComparer.Ordinal).Chunk(ChunkSize))
        {
            var sql = new Sql(
                $"SELECT DISTINCT r.ResponseKey FROM {Constants.Tables.ResponseKeys} k INNER JOIN {Constants.Tables.Responses} r ON r.ResponseKey = k.ResponseKey WHERE k.CacheKey IN (@keys)",
                new { keys = chunk });
            if (all is false)
            {
                sql = sql.Append("AND r.Culture IN (@cultures)", new { cultures = lowered });
            }

            found.UnionWith(database.Fetch<string>(sql));
        }

        return found;
    }

    public static CachedResponse? Get(IDatabase database, string responseKey)
    {
        ResponseRow? row = database.SingleOrDefault<ResponseRow>(
            $"SELECT * FROM {Constants.Tables.Responses} WHERE ResponseKey = @0", responseKey);
        if (row is null)
        {
            return null;
        }

        // Only the keys the latest render included; older ones are waiting to age out.
        List<string> keys = database.Fetch<string>(
            $"SELECT CacheKey FROM {Constants.Tables.ResponseKeys} WHERE ResponseKey = @0 AND RenderedUtc = @1 ORDER BY CacheKey",
            responseKey,
            row.RenderedUtc);
        return new CachedResponse(row.ResponseKey, row.Path, row.Query, row.Culture, row.Segment, row.StartItem, keys, row.RenderedUtc, row.SeenUtc);
    }

    public static int Prune(IDatabase database, DateTime cutoffUtc)
    {
        // Keys a later render no longer included, once they are older than the cutoff.
        var dropped = database.Execute(
            $"DELETE FROM {Constants.Tables.ResponseKeys} WHERE RenderedUtc < @0 AND RenderedUtc < (SELECT r.RenderedUtc FROM {Constants.Tables.Responses} r WHERE r.ResponseKey = {Constants.Tables.ResponseKeys}.ResponseKey)",
            cutoffUtc);

        // Responses nobody has asked for since the cutoff, with all their keys.
        var orphaned = database.Execute(
            $"DELETE FROM {Constants.Tables.ResponseKeys} WHERE ResponseKey IN (SELECT ResponseKey FROM {Constants.Tables.Responses} WHERE SeenUtc < @0)",
            cutoffUtc);
        var responses = database.Execute($"DELETE FROM {Constants.Tables.Responses} WHERE SeenUtc < @0", cutoffUtc);
        return dropped + orphaned + responses;
    }
}
