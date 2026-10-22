using Umbraco.Cms.Infrastructure.Migrations;

namespace TheBuilder.CacheTags.Migrations;

/// <summary>Every server looks up the changes released recently without it, to evict them when it applies them late.</summary>
internal sealed class AddReleasedIndex(IMigrationContext context) : AsyncMigrationBase(context)
{
    private const string Name = $"IX_{Constants.Tables.Events}_ReleasedUtc";

    protected override Task MigrateAsync()
    {
        if (IndexExists(Name) is false)
        {
            Execute.Sql($"CREATE INDEX {Name} ON {Constants.Tables.Events} (ReleasedUtc)").Do();
        }

        return Task.CompletedTask;
    }
}
