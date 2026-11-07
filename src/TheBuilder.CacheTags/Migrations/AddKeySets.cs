using TheBuilder.CacheTags.Storage;
using Umbraco.Cms.Infrastructure.Migrations;

namespace TheBuilder.CacheTags.Migrations;

/// <summary>
///     Stored key sets: a response showing more items than its header lists carries one <c>set-</c>
///     key, and a change to any of its items purges it. A released change looks its items' sets up
///     once more shortly after.
/// </summary>
internal sealed class AddKeySets(IMigrationContext context) : AsyncMigrationBase(context)
{
    protected override Task MigrateAsync()
    {
        if (TableExists(Constants.Tables.KeySets) is false)
        {
            Create.Table<KeySetRow>().Do();
            Execute.Sql($"CREATE INDEX IX_{Constants.Tables.KeySets}_LastSeenUtc ON {Constants.Tables.KeySets} (LastSeenUtc)").Do();
        }

        if (TableExists(Constants.Tables.KeySetItems) is false)
        {
            Create.Table<KeySetItemRow>().Do();
            Execute.Sql($"CREATE UNIQUE INDEX IX_{Constants.Tables.KeySetItems}_Pair ON {Constants.Tables.KeySetItems} (Hash, ItemKey)").Do();
            Execute.Sql($"CREATE INDEX IX_{Constants.Tables.KeySetItems}_ItemKey ON {Constants.Tables.KeySetItems} (ItemKey)").Do();
        }

        if (ColumnExists(Constants.Tables.Events, nameof(EventRow.SetsCheckUtc)) is false)
        {
            AddColumn<EventRow>(Constants.Tables.Events, nameof(EventRow.SetsCheckUtc));
        }

        const string checkIndex = $"IX_{Constants.Tables.Events}_SetsCheckUtc";
        if (IndexExists(checkIndex) is false)
        {
            Execute.Sql($"CREATE INDEX {checkIndex} ON {Constants.Tables.Events} (SetsCheckUtc)").Do();
        }

        return Task.CompletedTask;
    }
}
