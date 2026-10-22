namespace TheBuilder.CacheTags;

internal static class Constants
{
    public const string PackageName = "TheBuilder.CacheTags";

    /// <summary>Under <c>/umbraco/</c>, which <c>BackOfficeRoute</c> adds.</summary>
    public const string ApiRoot = "management/api/v{version:apiVersion}/cache-tags";
    public const string ApiName = "thebuilder-cache-tags";

    public static class Tables
    {
        public const string Responses = "theBuilderCacheTagsResponse";
        public const string ResponseKeys = "theBuilderCacheTagsResponseKey";
        public const string Events = "theBuilderCacheTagsEvent";
        public const string Servers = "theBuilderCacheTagsServer";
        public const string Acks = "theBuilderCacheTagsAck";
        public const string Outbox = "theBuilderCacheTagsOutbox";
    }
}
