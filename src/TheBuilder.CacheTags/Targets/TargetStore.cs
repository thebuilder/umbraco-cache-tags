using System.Text.Json;
using NPoco;
using TheBuilder.CacheTags.Storage;

namespace TheBuilder.CacheTags.Targets;

/// <summary>The targets added in the backoffice, in the Umbraco database.</summary>
internal sealed class TargetStore(PackageDatabase database, TimeProvider time)
{
    public IReadOnlyList<TargetDefinition> All() => database.Read(TargetSql.All);

    /// <summary>Adds a target. Returns false when one with its name exists.</summary>
    public bool Add(TargetDefinition target) => database.Write(db => TargetSql.Add(db, target, time.GetUtcNow().UtcDateTime));

    /// <summary>Replaces a target. Returns false when there is none with its name.</summary>
    public bool Update(TargetDefinition target) => database.Write(db => TargetSql.Update(db, target, time.GetUtcNow().UtcDateTime));

    /// <summary>
    ///     Deletes a target, and sets its unsent purges aside unless <paramref name="keepPurges" />: a
    ///     configured target of the same name owns them. Returns false when there is none with the name.
    /// </summary>
    public bool Delete(string name, bool keepPurges = false) => database.Write(db => TargetSql.Delete(db, name, keepPurges));
}

/// <summary>The target store's SQL, against any NPoco database.</summary>
internal static class TargetSql
{
    public static List<TargetDefinition> All(IDatabase database)
        => database.Fetch<TargetRow>(new Sql($"SELECT * FROM {Constants.Tables.Targets} ORDER BY Name"))
            .Select(Read)
            .ToList();

    // A row this version cannot read becomes a target with a problem, so it cannot stop the others.
    private static TargetDefinition Read(TargetRow row)
    {
        try
        {
            return JsonSerializer.Deserialize(row.Definition, TargetJson.Default.TargetDefinition) ?? new TargetDefinition { Name = row.Name, Unreadable = true };
        }
        catch (JsonException)
        {
            return new TargetDefinition { Name = row.Name, Unreadable = true };
        }
    }

    public static bool Add(IDatabase database, TargetDefinition target, DateTime nowUtc)
    {
        if (Exists(database, target.Name))
        {
            return false;
        }

        database.Insert(new TargetRow { Name = target.Name, Definition = Json(target), CreatedUtc = nowUtc, UpdatedUtc = nowUtc });
        return true;
    }

    public static bool Update(IDatabase database, TargetDefinition target, DateTime nowUtc)
        => database.Execute(
            $"UPDATE {Constants.Tables.Targets} SET Definition = @0, UpdatedUtc = @1 WHERE Name = @2",
            Json(target),
            nowUtc,
            target.Name) == 1;

    /// <summary>Deletes the target, and sets its pending and failed purges aside: nothing could send them.</summary>
    public static bool Delete(IDatabase database, string name, bool keepPurges = false)
    {
        if (database.Execute($"DELETE FROM {Constants.Tables.Targets} WHERE Name = @0", name) == 0)
        {
            return false;
        }

        if (keepPurges)
        {
            return true;
        }

        database.Execute(
            $"UPDATE {Constants.Tables.Outbox} SET State = @0 WHERE Target = @1 AND (State = @2 OR State = @3)",
            OutboxStates.Discarded,
            name,
            OutboxStates.Pending,
            OutboxStates.Dead);
        return true;
    }

    private static bool Exists(IDatabase database, string name)
        => database.ExecuteScalar<int>($"SELECT COUNT(*) FROM {Constants.Tables.Targets} WHERE Name = @0", name) > 0;

    private static string Json(TargetDefinition target) => JsonSerializer.Serialize(target, TargetJson.Default.TargetDefinition);
}
