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

    /// <summary>What a server's cache refresh must show for it to acknowledge this change, as JSON.</summary>
    [Column(nameof(Signal)), SpecialDbType(SpecialDbTypes.NVARCHARMAX), NullSetting(NullSetting = NullSettings.Null)] public string? Signal { get; set; }

    /// <summary>The change's <c>ChangeStage</c>: Pending, Indexing or Released.</summary>
    [Column(nameof(Status)), Length(30), NullSetting(NullSetting = NullSettings.Null)] public string? Status { get; set; }

    [Column(nameof(ReleasedUtc)), NullSetting(NullSetting = NullSettings.Null)] public DateTime? ReleasedUtc { get; set; }

    /// <summary>The servers that had not acknowledged when the change was released after the ack timeout, as JSON.</summary>
    [Column(nameof(Missing)), SpecialDbType(SpecialDbTypes.NVARCHARMAX), NullSetting(NullSetting = NullSettings.Null)] public string? Missing { get; set; }
}

[TableName(Constants.Tables.Servers), PrimaryKey(nameof(Server), AutoIncrement = false), ExplicitColumns]
internal sealed class ServerRow
{
    [Column(nameof(Server)), PrimaryKeyColumn(AutoIncrement = false), Length(200)] public string Server { get; set; } = string.Empty;
    [Column(nameof(FirstSeenUtc))] public DateTime FirstSeenUtc { get; set; }
    [Column(nameof(LastSeenUtc))] public DateTime LastSeenUtc { get; set; }
}

[TableName(Constants.Tables.Acks), PrimaryKey(nameof(Id), AutoIncrement = true), ExplicitColumns]
internal sealed class AckRow
{
    [Column(nameof(Id)), PrimaryKeyColumn(AutoIncrement = true)] public long Id { get; set; }
    [Column(nameof(EventId))] public Guid EventId { get; set; }
    [Column(nameof(Server)), Length(200)] public string Server { get; set; } = string.Empty;
    [Column(nameof(AckedUtc))] public DateTime AckedUtc { get; set; }

    /// <summary>When the server's Delivery API index had the change, for a change whose listings wait for it.</summary>
    [Column(nameof(IndexedUtc)), NullSetting(NullSetting = NullSettings.Null)] public DateTime? IndexedUtc { get; set; }
}

[TableName(Constants.Tables.Outbox), PrimaryKey(nameof(Id), AutoIncrement = false), ExplicitColumns]
internal sealed class OutboxRow
{
    /// <summary>Also the id sent with the purge, so a target can ignore a repeat.</summary>
    [Column(nameof(Id)), PrimaryKeyColumn(AutoIncrement = false)] public Guid Id { get; set; }
    [Column(nameof(EventId))] public Guid EventId { get; set; }
    [Column(nameof(Target)), Length(100)] public string Target { get; set; } = string.Empty;

    /// <summary>The keys this request purges, as JSON.</summary>
    [Column(nameof(Keys)), SpecialDbType(SpecialDbTypes.NVARCHARMAX)] public string Keys { get; set; } = "[]";

    /// <summary>Pending, Sent, Dead or Discarded.</summary>
    [Column(nameof(State)), Length(20)] public string State { get; set; } = OutboxStates.Pending;
    [Column(nameof(Attempts))] public int Attempts { get; set; }
    [Column(nameof(CreatedUtc))] public DateTime CreatedUtc { get; set; }
    [Column(nameof(NotBeforeUtc))] public DateTime NotBeforeUtc { get; set; }
    [Column(nameof(SentUtc)), NullSetting(NullSetting = NullSettings.Null)] public DateTime? SentUtc { get; set; }
    [Column(nameof(LastError)), SpecialDbType(SpecialDbTypes.NVARCHARMAX), NullSetting(NullSetting = NullSettings.Null)] public string? LastError { get; set; }
    [Column(nameof(LeaseOwner)), Length(200), NullSetting(NullSetting = NullSettings.Null)] public string? LeaseOwner { get; set; }
    [Column(nameof(LeaseUntilUtc)), NullSetting(NullSetting = NullSettings.Null)] public DateTime? LeaseUntilUtc { get; set; }
}

internal static class OutboxStates
{
    public const string Pending = "Pending";
    public const string Sent = "Sent";

    /// <summary>Set aside by a person: not sent, kept for the lookup until it ages out.</summary>
    public const string Discarded = "Discarded";
    public const string Dead = "Dead";
}
