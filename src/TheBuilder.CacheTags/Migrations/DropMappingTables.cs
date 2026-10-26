using Umbraco.Cms.Infrastructure.Migrations;

namespace TheBuilder.CacheTags.Migrations;

/// <summary>
///     A response carries the keys of what it shows, so the package no longer records which keys each
///     response covers.
/// </summary>
internal sealed class DropMappingTables(IMigrationContext context) : AsyncMigrationBase(context)
{
    protected override Task MigrateAsync()
    {
        foreach (var table in new[] { Constants.Tables.ResponseKeys, Constants.Tables.Responses })
        {
            if (TableExists(table))
            {
                Delete.Table(table).Do();
            }
        }

        return Task.CompletedTask;
    }
}
