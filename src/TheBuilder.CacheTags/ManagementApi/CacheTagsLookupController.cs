using System.Text.Json;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TheBuilder.CacheTags.Delivery;
using TheBuilder.CacheTags.Keys;
using TheBuilder.CacheTags.Ledger;
using TheBuilder.CacheTags.Storage;
using Umbraco.Cms.Api.Common.Attributes;
using Umbraco.Cms.Core.Models.Entities;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Scoping;
using Umbraco.Cms.Web.Common.Authorization;
using Umbraco.Cms.Web.Common.Routing;

namespace TheBuilder.CacheTags.ManagementApi;

/// <summary>
///     Admin-only answers to "what does this key stand for?" and "what did this change purge?". The
///     backoffice view (Milestone 5) is built on it.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[BackOfficeRoute(Constants.ApiRoot)]
[Authorize(Policy = AuthorizationPolicies.BackOfficeAccess)]
[Authorize(Policy = AdministratorRequirement.Policy)]
[MapToApi(Constants.ApiName)]
[ApiExplorerSettings(GroupName = Constants.PackageName)]
[ResponseCache(Location = ResponseCacheLocation.None, NoStore = true)]
public sealed class CacheTagsLookupController(ICacheTagStore store, IAckTransport ackTransport, IEntityService entityService, IScopeProvider scopeProvider) : ControllerBase
{
    private const int MaxResponses = 200;

    /// <summary>
    ///     A <c>response-</c> key: the request it stands for and the keys it covers. Any other key: the
    ///     responses that cover it.
    /// </summary>
    [HttpGet("lookup")]
    public async Task<IActionResult> Lookup([FromQuery] string key, CancellationToken cancellationToken)
    {
        key = key.Trim().ToLowerInvariant();
        if (key.StartsWith(CacheKeys.ResponsePrefix, StringComparison.Ordinal))
        {
            CachedResponse? response = await store.GetAsync(key, cancellationToken);
            return response is null
                ? NotFound(new { key, error = "No response with this key has been rendered, or it has aged out." })
                : Ok(new
                {
                    key,
                    kind = "response",
                    request = new { response.Path, response.Query, response.Culture, response.Segment, response.StartItem },
                    response.RenderedUtc,
                    response.SeenUtc,
                    keys = response.Keys.Select(k => new { key = k, name = NameOf(k) }),
                });
        }

        IReadOnlyCollection<string> responses = await store.FindResponseKeysAsync([key], null, cancellationToken);
        var described = new List<object>();
        foreach (var responseKey in responses.Order(StringComparer.Ordinal).Take(MaxResponses))
        {
            if (await store.GetAsync(responseKey, cancellationToken) is { } response)
            {
                described.Add(new { key = responseKey, response.Path, response.Query, response.Culture, response.SeenUtc });
            }
        }

        return Ok(new { key, kind = Kind(key), name = NameOf(key), responseCount = responses.Count, responses = described });
    }

    /// <summary>The latest changes, newest first: what each planned, the responses that plan reaches now, which servers acknowledged it, and its purges.</summary>
    [HttpGet("changes")]
    public async Task<IActionResult> Changes([FromQuery] int take = 20, CancellationToken cancellationToken = default)
    {
        List<EventRow> rows;
        using (IScope scope = scopeProvider.CreateScope(autoComplete: true))
        {
            rows = scope.Database.Fetch<EventRow>(
                $"SELECT * FROM {Constants.Tables.Events} ORDER BY CreatedUtc DESC").Take(Math.Clamp(take, 1, 200)).ToList();
        }

        var changes = new List<object>();
        foreach (EventRow row in rows)
        {
            PurgePlan? plan = JsonSerializer.Deserialize<PurgePlan>(row.Plan, ChangeRecorder.Json);
            IReadOnlyCollection<string> responses = plan is null || plan.Everything
                ? []
                : await store.FindResponseKeysAsync(plan.Keys, plan.Cultures, cancellationToken);
            List<OutboxRow> purges;
            using (IScope scope = scopeProvider.CreateScope(autoComplete: true))
            {
                purges = scope.Database.Fetch<OutboxRow>($"SELECT * FROM {Constants.Tables.Outbox} WHERE EventId = @0 ORDER BY NotBeforeUtc", row.Id);
            }

            IReadOnlyDictionary<Guid, IReadOnlySet<string>> acks = await ackTransport.GetAcksAsync([row.Id], cancellationToken);
            changes.Add(new
            {
                row.Id,
                row.CreatedUtc,
                row.Kind,
                row.Server,
                row.Status,
                row.ReleasedUtc,
                acknowledgedBy = acks.TryGetValue(row.Id, out IReadOnlySet<string>? by) ? by.Order(StringComparer.Ordinal) : Enumerable.Empty<string>(),
                notAcknowledgedBy = row.Missing is null ? null : JsonSerializer.Deserialize<string[]>(row.Missing),
                change = JsonSerializer.Deserialize<JsonElement>(row.Change),
                plan,
                responses = responses.Order(StringComparer.Ordinal),
                purges = purges.Select(p => new
                {
                    p.Id,
                    p.Target,
                    p.State,
                    p.Attempts,
                    p.NotBeforeUtc,
                    p.SentUtc,
                    p.LastError,
                    keys = JsonSerializer.Deserialize<string[]>(p.Keys),
                }),
            });
        }

        return Ok(changes);
    }

    private static string Kind(string key) => key switch
    {
        _ when key.StartsWith(CacheKeys.ContentPrefix, StringComparison.Ordinal) => "content",
        _ when key.StartsWith(CacheKeys.MediaPrefix, StringComparison.Ordinal) => "media",
        _ when key.StartsWith(CacheKeys.TypePrefix, StringComparison.Ordinal) => "type",
        _ when key.StartsWith(CacheKeys.ChildrenPrefix, StringComparison.Ordinal) => "children",
        _ when key.StartsWith(CacheKeys.SearchPrefix, StringComparison.Ordinal) => "search",
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
