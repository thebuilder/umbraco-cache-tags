using TheBuilder.CacheTags.Storage;
using Umbraco.Cms.Infrastructure.Migrations;

namespace TheBuilder.CacheTags.Migrations;

internal sealed class InstallCacheTagsSchema(IMigrationContext context) : AsyncMigrationBase(context)
{
    protected override Task MigrateAsync()
    {
        CreateIfMissing<ResponseRow>(Constants.Tables.Responses);
        CreateIfMissing<ResponseKeyRow>(Constants.Tables.ResponseKeys);
        CreateIfMissing<EventRow>(Constants.Tables.Events);

        Execute.Sql($"CREATE UNIQUE INDEX IX_{Constants.Tables.ResponseKeys}_Pair ON {Constants.Tables.ResponseKeys} (ResponseKey, CacheKey)").Do();
        Execute.Sql($"CREATE INDEX IX_{Constants.Tables.ResponseKeys}_CacheKey ON {Constants.Tables.ResponseKeys} (CacheKey)").Do();
        Execute.Sql($"CREATE INDEX IX_{Constants.Tables.Responses}_SeenUtc ON {Constants.Tables.Responses} (SeenUtc)").Do();
        Execute.Sql($"CREATE INDEX IX_{Constants.Tables.Events}_CreatedUtc ON {Constants.Tables.Events} (CreatedUtc)").Do();
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
