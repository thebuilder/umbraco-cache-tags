using TheBuilder.CacheTags.Storage;
using Umbraco.Cms.Infrastructure.Migrations;

namespace TheBuilder.CacheTags.Migrations;

/// <summary>
///     Milestone 4: listing purges wait for every server's Delivery API index, and a change's status
///     is its stage (Pending, Indexing, Released).
/// </summary>
internal sealed class AddIndexAcks(IMigrationContext context) : AsyncMigrationBase(context)
{
    protected override Task MigrateAsync()
    {
        if (ColumnExists(Constants.Tables.Acks, nameof(AckRow.IndexedUtc)) is false)
        {
            AddColumn<AckRow>(Constants.Tables.Acks, nameof(AckRow.IndexedUtc));
        }

        // Milestone 3's statuses: a change being released is released again; a timeout is in Missing.
        Execute.Sql($"UPDATE {Constants.Tables.Events} SET Status = 'Pending' WHERE Status = 'Releasing'").Do();
        Execute.Sql($"UPDATE {Constants.Tables.Events} SET Status = 'Released' WHERE Status = 'ReleasedAfterTimeout'").Do();
        return Task.CompletedTask;
    }
}
