using NPoco;
using TheBuilder.CacheTags.Storage;
using Umbraco.Cms.Infrastructure.Migrations;
using Umbraco.Cms.Infrastructure.Persistence.DatabaseAnnotations;

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

    // The tables of the response mapping, which DropMappingTables removes again.
    [TableName(Constants.Tables.Responses), PrimaryKey(nameof(ResponseKey), AutoIncrement = false), ExplicitColumns]
    private sealed class ResponseRow
    {
        [Column(nameof(ResponseKey)), PrimaryKeyColumn(AutoIncrement = false), Length(100)] public string ResponseKey { get; set; } = string.Empty;
        [Column(nameof(Path)), Length(1000)] public string Path { get; set; } = string.Empty;
        [Column(nameof(Query)), SpecialDbType(SpecialDbTypes.NVARCHARMAX)] public string Query { get; set; } = string.Empty;
        [Column(nameof(Culture)), Length(50)] public string Culture { get; set; } = string.Empty;
        [Column(nameof(Segment)), Length(100)] public string Segment { get; set; } = string.Empty;
        [Column(nameof(StartItem)), Length(200)] public string StartItem { get; set; } = string.Empty;
        [Column(nameof(RenderedUtc))] public DateTime RenderedUtc { get; set; }
        [Column(nameof(SeenUtc))] public DateTime SeenUtc { get; set; }
    }

    [TableName(Constants.Tables.ResponseKeys), PrimaryKey(nameof(Id), AutoIncrement = true), ExplicitColumns]
    private sealed class ResponseKeyRow
    {
        [Column(nameof(Id)), PrimaryKeyColumn(AutoIncrement = true)] public long Id { get; set; }
        [Column(nameof(ResponseKey)), Length(100)] public string ResponseKey { get; set; } = string.Empty;
        [Column(nameof(CacheKey)), Length(300)] public string CacheKey { get; set; } = string.Empty;

        /// <summary>The last render that included this key.</summary>
        [Column(nameof(RenderedUtc))] public DateTime RenderedUtc { get; set; }
    }
}
