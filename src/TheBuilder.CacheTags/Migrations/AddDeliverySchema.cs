using TheBuilder.CacheTags.Storage;
using Umbraco.Cms.Infrastructure.Migrations;

namespace TheBuilder.CacheTags.Migrations;

/// <summary>Milestone 3: the ack barrier and the outbox.</summary>
internal sealed class AddDeliverySchema(IMigrationContext context) : AsyncMigrationBase(context)
{
    protected override Task MigrateAsync()
    {
        foreach (var column in new[] { nameof(EventRow.Signal), nameof(EventRow.Status), nameof(EventRow.ReleasedUtc), nameof(EventRow.Missing) })
        {
            if (ColumnExists(Constants.Tables.Events, column) is false)
            {
                AddColumn<EventRow>(Constants.Tables.Events, column);
            }
        }

        // Changes recorded before this step have no signal to acknowledge; they are history.
        Execute.Sql($"UPDATE {Constants.Tables.Events} SET Status = 'Released' WHERE Status IS NULL").Do();

        CreateIfMissing<ServerRow>(Constants.Tables.Servers);
        CreateIfMissing<AckRow>(Constants.Tables.Acks);
        CreateIfMissing<OutboxRow>(Constants.Tables.Outbox);
        Execute.Sql($"CREATE UNIQUE INDEX IX_{Constants.Tables.Acks}_Pair ON {Constants.Tables.Acks} (EventId, Server)").Do();
        Execute.Sql($"CREATE INDEX IX_{Constants.Tables.Events}_Status ON {Constants.Tables.Events} (Status)").Do();
        Execute.Sql($"CREATE INDEX IX_{Constants.Tables.Outbox}_Due ON {Constants.Tables.Outbox} (State, NotBeforeUtc)").Do();
        return Task.CompletedTask;
    }

    private void CreateIfMissing<T>(string tableName)
    {
        if (TableExists(tableName) is false)
        {
            Create.Table<T>().Do();
        }
    }
}
