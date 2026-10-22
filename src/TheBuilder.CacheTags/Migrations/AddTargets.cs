using TheBuilder.CacheTags.Storage;
using Umbraco.Cms.Infrastructure.Migrations;

namespace TheBuilder.CacheTags.Migrations;

/// <summary>Targets added in the backoffice.</summary>
internal sealed class AddTargets(IMigrationContext context) : AsyncMigrationBase(context)
{
    protected override Task MigrateAsync()
    {
        if (TableExists(Constants.Tables.Targets) is false)
        {
            Create.Table<TargetRow>().Do();
        }

        return Task.CompletedTask;
    }
}
