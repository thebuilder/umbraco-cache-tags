using NPoco;
using Umbraco.Cms.Infrastructure.Persistence.DatabaseAnnotations;

namespace TheBuilder.CacheTags.Storage;

[TableName(Constants.Tables.Responses), PrimaryKey(nameof(ResponseKey), AutoIncrement = false), ExplicitColumns]
internal sealed class ResponseRow
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
internal sealed class ResponseKeyRow
{
    [Column(nameof(Id)), PrimaryKeyColumn(AutoIncrement = true)] public long Id { get; set; }
    [Column(nameof(ResponseKey)), Length(100)] public string ResponseKey { get; set; } = string.Empty;
    [Column(nameof(CacheKey)), Length(300)] public string CacheKey { get; set; } = string.Empty;

    /// <summary>The last render that included this key.</summary>
    [Column(nameof(RenderedUtc))] public DateTime RenderedUtc { get; set; }
}

[TableName(Constants.Tables.Events), PrimaryKey(nameof(Id), AutoIncrement = false), ExplicitColumns]
internal sealed class EventRow
{
    [Column(nameof(Id)), PrimaryKeyColumn(AutoIncrement = false)] public Guid Id { get; set; }
    [Column(nameof(CreatedUtc))] public DateTime CreatedUtc { get; set; }
    [Column(nameof(Kind)), Length(50)] public string Kind { get; set; } = string.Empty;
    [Column(nameof(Server)), Length(200)] public string Server { get; set; } = string.Empty;

    /// <summary>The change, as JSON.</summary>
    [Column(nameof(Change)), SpecialDbType(SpecialDbTypes.NVARCHARMAX)] public string Change { get; set; } = string.Empty;

    /// <summary>The planned purge, as JSON.</summary>
    [Column(nameof(Plan)), SpecialDbType(SpecialDbTypes.NVARCHARMAX)] public string Plan { get; set; } = string.Empty;
}
