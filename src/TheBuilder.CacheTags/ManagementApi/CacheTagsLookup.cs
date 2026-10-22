using System.Text.Json;
using TheBuilder.CacheTags.Delivery;
using TheBuilder.CacheTags.Keys;
using TheBuilder.CacheTags.Ledger;
using TheBuilder.CacheTags.Storage;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;

namespace TheBuilder.CacheTags.ManagementApi;

/// <summary>
///     What the lookup API answers. Public only because the controller's constructor takes it; its
///     members are internal, so nothing outside the package can implement or call it.
/// </summary>
public interface ICacheTagsLookup
{
    internal Task<KeyLookup?> LookupAsync(string key, CancellationToken cancellationToken);

    internal Task<IReadOnlyList<ChangeLookup>> ChangesAsync(int take, CancellationToken cancellationToken);

    internal Task<SearchLookup> SearchAsync(string text, CancellationToken cancellationToken);
}

/// <summary>What a search found: items by name, recorded keys, and recorded responses.</summary>
internal sealed record SearchLookup(IReadOnlyList<SearchItem> Items, IReadOnlyList<NamedKey> Keys, IReadOnlyList<ResponseSummary> Responses);

/// <summary>A document or media item found by name, with its key.</summary>
internal sealed record SearchItem(string Key, string Name, string Kind);

internal sealed record KeyLookup(
    string Key,
    string Kind,
    string? Name,
    ResponseLookup? Response,
    int ResponseCount,
    IReadOnlyList<ResponseSummary> Responses);

internal sealed record ResponseLookup(
    string Path,
    string Query,
    string Culture,
    string Segment,
    string StartItem,
    DateTime RenderedUtc,
    DateTime SeenUtc,
    IReadOnlyList<NamedKey> Keys);

internal sealed record NamedKey(string Key, string? Name);

internal sealed record ResponseSummary(string Key, string Path, string Query, string Culture, DateTime SeenUtc);

internal sealed record ChangeLookup(
    Guid Id,
    DateTime CreatedUtc,
    string Kind,
    string Server,
    string Status,
    bool WaitsForServers,
    DateTime? ReleasedUtc,
    IReadOnlyList<string> AcknowledgedBy,
    IReadOnlyList<string> NotAcknowledgedBy,
    IReadOnlyList<string> IndexedBy,
    Change Change,
    PurgePlan Plan,
    int ResponseCount,
    IReadOnlyList<Purge> Purges);

internal sealed class CacheTagsLookup(
    ICacheTagStore store,
    LedgerStore ledger,
    PurgeResolver resolver,
    IAckTransport transport,
    IEntityService entityService,
    IEntitySearchService entitySearch) : ICacheTagsLookup
{
    private const int MaxResponses = 200;
    private const int MaxSearchResults = 20;

    /// <summary>Documents and media by name, and recorded keys and responses containing the text.</summary>
    public async Task<SearchLookup> SearchAsync(string text, CancellationToken cancellationToken)
    {
        var items = new (UmbracoObjectTypes Type, string Prefix, string Kind)[] { (UmbracoObjectTypes.Document, CacheKeys.ContentPrefix, "content"), (UmbracoObjectTypes.Media, CacheKeys.MediaPrefix, "media") }
            .SelectMany(search => entitySearch.Search(search.Type, text, 0, MaxSearchResults / 2).Items
                .Select(entity => new SearchItem(search.Prefix + entity.Key.ToString("D"), entity.Name ?? string.Empty, search.Kind)))
            .ToList();
        CacheSearchResult found = await store.SearchAsync(text, MaxSearchResults, cancellationToken);
        return new SearchLookup(
            items,
            found.Keys.Select(key => new NamedKey(key, NameOf(key))).ToList(),
            found.Responses.Select(response => new ResponseSummary(response.ResponseKey, response.Path, response.Query, response.Culture, response.SeenUtc)).ToList());
    }

    /// <summary>
    ///     A <c>response-</c> key: the request it stands for and the keys it covers. Any other key: the
    ///     responses that cover it. Null for a response key that was never rendered or has aged out.
    /// </summary>
    public async Task<KeyLookup?> LookupAsync(string key, CancellationToken cancellationToken)
    {
        key = key.Trim().ToLowerInvariant();
        if (key.StartsWith(CacheKeys.ResponsePrefix, StringComparison.Ordinal))
        {
            return await store.GetAsync(key, cancellationToken) is { } response
                ? new KeyLookup(key, "response", null, Describe(response), 1, [])
                : null;
        }

        IReadOnlyCollection<string> responses = await store.FindResponseKeysAsync([key], null, cancellationToken);
        var summaries = new List<ResponseSummary>();
        foreach (var responseKey in responses.Order(StringComparer.Ordinal).Take(MaxResponses))
        {
            if (await store.GetAsync(responseKey, cancellationToken) is { } response)
            {
                summaries.Add(new ResponseSummary(responseKey, response.Path, response.Query, response.Culture, response.SeenUtc));
            }
        }

        return new KeyLookup(key, Kind(key), NameOf(key), null, responses.Count, summaries);
    }

    /// <summary>The latest changes, newest first: what each planned, the responses it reaches now, who acknowledged it, and its purges.</summary>
    public async Task<IReadOnlyList<ChangeLookup>> ChangesAsync(int take, CancellationToken cancellationToken)
    {
        IReadOnlyList<LedgerEntry> changes = ledger.Latest(Math.Clamp(take, 1, 200));
        IReadOnlyDictionary<Guid, IReadOnlyList<ServerAck>> acks = await transport.GetAcksAsync(changes.Select(change => change.Id).ToList(), cancellationToken);

        var lookups = new List<ChangeLookup>();
        foreach (LedgerEntry change in changes)
        {
            ResolvedPurge resolved = await resolver.ResolveAsync(change.Plan, cancellationToken);
            lookups.Add(new ChangeLookup(
                change.Id,
                change.CreatedUtc,
                change.Change.Kind.ToString(),
                change.Server,
                change.Stage.ToString(),
                change.Signal is not { WaitsForServers: false },
                change.ReleasedUtc,
                acks.AcknowledgedBy(change.Id).Order(StringComparer.Ordinal).ToList(),
                change.NotAcknowledgedBy,
                acks.IndexedBy(change.Id).Order(StringComparer.Ordinal).ToList(),
                change.Change,
                change.Plan,
                resolved.Responses.Count,
                ledger.PurgesOf(change.Id)));
        }

        return lookups;
    }

    private ResponseLookup Describe(CachedResponse response) => new(
        response.Path,
        response.Query,
        response.Culture,
        response.Segment,
        response.StartItem,
        response.RenderedUtc,
        response.SeenUtc,
        response.Keys.Select(key => new NamedKey(key, NameOf(key))).ToList());

    private static string Kind(string key) => key switch
    {
        _ when key.StartsWith(CacheKeys.ContentPrefix, StringComparison.Ordinal) => "content",
        _ when key.StartsWith(CacheKeys.MediaPrefix, StringComparison.Ordinal) => "media",
        _ when key.StartsWith(CacheKeys.TypePrefix, StringComparison.Ordinal) => "type",
        _ when key.StartsWith(CacheKeys.ChildrenPrefix, StringComparison.Ordinal) => "children",
        _ when key.StartsWith(CacheKeys.SearchPrefix, StringComparison.Ordinal) => "search",
        _ when key.StartsWith(CacheKeys.SchemaPrefix, StringComparison.Ordinal) => "schema",
        _ when key.StartsWith(CacheKeys.CustomPrefix, StringComparison.Ordinal) => "custom",
        CacheKeys.All => "everything",
        _ => "unknown",
    };

    // The name of the document or media item (or the parent, for children-) a key points at.
    private string? NameOf(string key)
    {
        foreach (var prefix in new[] { CacheKeys.ContentPrefix, CacheKeys.MediaPrefix, CacheKeys.ChildrenPrefix })
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal) && Guid.TryParse(key[prefix.Length..], out Guid id))
            {
                return id == Guid.Empty ? "(root)" : entityService.Get(id)?.Name;
            }
        }

        return null;
    }
}
