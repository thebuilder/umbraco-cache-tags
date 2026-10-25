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
    public const int ChunkSize = 500;

    public static void Record(IDatabase database, CachedResponse response)
    {
        var updated = database.Execute(
            $"UPDATE {Constants.Tables.Responses} SET RenderedUtc = @0, SeenUtc = @0, Culture = @1 WHERE ResponseKey = @2",
            response.RenderedUtc,
            response.Culture,
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

        var existing = database.Fetch<string>(
            $"SELECT CacheKey FROM {Constants.Tables.ResponseKeys} WHERE ResponseKey = @0",
            response.ResponseKey).ToHashSet(StringComparer.Ordinal);
        var keys = response.Keys.Distinct(StringComparer.Ordinal).ToList();
        foreach (var chunk in keys.Where(existing.Contains).Chunk(ChunkSize))
        {
            database.Execute(
                new Sql($"UPDATE {Constants.Tables.ResponseKeys} SET RenderedUtc = @rendered WHERE ResponseKey = @response AND CacheKey IN (@keys)", new { rendered = response.RenderedUtc, response = response.ResponseKey, keys = chunk }));
        }

        foreach (var key in keys.Where(key => existing.Contains(key) is false))
        {
            database.Insert(new ResponseKeyRow { ResponseKey = response.ResponseKey, CacheKey = key, RenderedUtc = response.RenderedUtc });
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

    /// <summary>Keys and responses containing the text, for the backoffice's search (see ICacheTagStore.SearchAsync).</summary>
    public static CacheSearchResult Search(IDatabase database, string text, int take)
    {
        // LIKE ignores case on SQLite and SQL Server's default collations. A query is stored with its
        // values escaped (ResponseIdentity.NormaliseQuery), so it is matched as typed and escaped.
        var plain = Like(text);
        var encoded = Like(Uri.EscapeDataString(text));
        var keys = database.SkipTake<string>(0, take, new Sql(
            $"SELECT DISTINCT CacheKey FROM {Constants.Tables.ResponseKeys} WHERE CacheKey LIKE @0 ESCAPE '\\' ORDER BY CacheKey",
            plain));
        var responses = database.SkipTake<ResponseRow>(0, take, new Sql(
            $"SELECT * FROM {Constants.Tables.Responses} WHERE ResponseKey LIKE @0 ESCAPE '\\' OR Path LIKE @0 ESCAPE '\\' OR Query LIKE @0 ESCAPE '\\' OR Query LIKE @1 ESCAPE '\\' ORDER BY SeenUtc DESC",
            plain,
            encoded));
        return new CacheSearchResult
        {
            Keys = keys,
            Responses = responses.Select(row => Response(row, [])).ToList(),
        };
    }

    // A LIKE pattern that matches the text anywhere, with the wildcards in it taken literally.
    private static string Like(string text)
        => "%" + text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("[", "\\[") + "%"; // [ is a wildcard on SQL Server

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
        return Response(row, keys);
    }

    public static int Prune(IDatabase database, DateTime cutoffUtc)
        => PruneDroppedKeys(database, cutoffUtc) + PruneUnseenKeys(database, cutoffUtc) + PruneUnseenResponses(database, cutoffUtc);

    /// <summary>Keys a later render no longer included, once they are older than the cutoff.</summary>
    public static int PruneDroppedKeys(IDatabase database, DateTime cutoffUtc)
        => database.Execute(
            $"DELETE FROM {Constants.Tables.ResponseKeys} WHERE RenderedUtc < @0 AND RenderedUtc < (SELECT r.RenderedUtc FROM {Constants.Tables.Responses} r WHERE r.ResponseKey = {Constants.Tables.ResponseKeys}.ResponseKey)",
            cutoffUtc);

    /// <summary>The keys of responses nobody has asked for since the cutoff.</summary>
    public static int PruneUnseenKeys(IDatabase database, DateTime cutoffUtc)
        => database.Execute(
            $"DELETE FROM {Constants.Tables.ResponseKeys} WHERE ResponseKey IN (SELECT ResponseKey FROM {Constants.Tables.Responses} WHERE SeenUtc < @0)",
            cutoffUtc);

    /// <summary>Responses nobody has asked for since the cutoff; their keys go first.</summary>
    public static int PruneUnseenResponses(IDatabase database, DateTime cutoffUtc)
        => database.Execute($"DELETE FROM {Constants.Tables.Responses} WHERE SeenUtc < @0", cutoffUtc);

    /// <summary>
    ///     Of at most <see cref="ChunkSize" /> responses, those neither rendered nor served since the
    ///     cutoff (a render sets the seen time too); their keys go first.
    /// </summary>
    public static int Forget(IDatabase database, IReadOnlyCollection<string> responseKeys, DateTime cutoffUtc)
        => database.Execute(new Sql(
               $"DELETE FROM {Constants.Tables.ResponseKeys} WHERE ResponseKey IN (SELECT ResponseKey FROM {Constants.Tables.Responses} WHERE SeenUtc < @cutoff AND ResponseKey IN (@keys))",
               new { cutoff = cutoffUtc, keys = responseKeys }))
           + database.Execute(new Sql(
               $"DELETE FROM {Constants.Tables.Responses} WHERE SeenUtc < @cutoff AND ResponseKey IN (@keys)",
               new { cutoff = cutoffUtc, keys = responseKeys }));

    private static CachedResponse Response(ResponseRow row, IReadOnlyCollection<string> keys) => new()
    {
        ResponseKey = row.ResponseKey,
        Path = row.Path,
        Query = row.Query,
        Culture = row.Culture,
        Segment = row.Segment,
        StartItem = row.StartItem,
        Keys = keys,
        RenderedUtc = row.RenderedUtc,
        SeenUtc = row.SeenUtc,
    };
}
