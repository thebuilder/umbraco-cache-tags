using TheBuilder.CacheTags.Storage;
using Umbraco.Cms.Infrastructure.Migrations;

namespace TheBuilder.CacheTags.Migrations;

/// <summary>Milestone 4: listing purges wait for every server's Delivery API index.</summary>
internal sealed class AddIndexAcks(IMigrationContext context) : AsyncMigrationBase(context)
{
    protected override Task MigrateAsync()
    {
        if (ColumnExists(Constants.Tables.Acks, nameof(AckRow.IndexedUtc)) is false)
        {
            AddColumn<AckRow>(Constants.Tables.Acks, nameof(AckRow.IndexedUtc));
        }

        if (ColumnExists(Constants.Tables.Events, nameof(EventRow.ListingsState)) is false)
        {
            AddColumn<EventRow>(Constants.Tables.Events, nameof(EventRow.ListingsState));
        }

        return Task.CompletedTask;
    }
}
