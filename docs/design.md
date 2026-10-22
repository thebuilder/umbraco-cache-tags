# Design

`TheBuilder.CacheTags` lets a headless frontend cache Umbraco Delivery API responses and purges
exactly the right ones once every delivery server has applied a change. This is the contract the
package, its samples and its docs are held to. Evidence for the decisions is in `docs/findings.md`,
from the Milestone 1 spike.

## Keys

Every key is lowercase ASCII. Cloudflare matches cache tags case-insensitively and Next.js
case-sensitively, so a key that differed only in case would behave differently per target.

| Key | Means | Purge behaviour at the frontend |
|---|---|---|
| `response-<hash>` | One Delivery API response: path, normalised query, culture, segment, start item | expire now |
| `content-<guid>` | One document | expire now |
| `media-<guid>` | One media item | expire now |
| `type-<alias>` | Every listing filtered by a content or media type (alias lowercased) | stale, refresh in background |
| `schema-<alias>` | Every response that renders a document, media or block element of a type, whose shape a type or data type change alters | stale, refresh in background |
| `children-<guid>` | Every listing of a parent's children (`fetch=children:`) | stale, refresh in background |
| `search-<index>` | Every other query against an index (`search-deliveryapicontentindex`) | stale, refresh in background |
| `umbraco` | Everything | stale, refresh in background |

- "Expire now" is `revalidateTag(key, { expire: 0 })` in Next.js: the next request waits for fresh
  content. "Stale" is `revalidateTag(key, 'max')`: the next request is served the old response and
  refreshes it. Listings only change once Umbraco's index has caught up, which no ack covers, so the
  broad keys are stale-while-revalidate (decided 2026-10-21).
- `CacheTags:KeyPrefix` (default empty) prefixes every key, for a site that shares a CDN zone with
  another app using the same names.
- These names deliberately differ from Umbraco's own output cache tags (`umb-dapi-content-{key}`).
  Those are internal to Umbraco; one internal function maps a key to Umbraco's tag when the package
  evicts Umbraco's output cache. Do not "align" them.

## The response header

Each cacheable Delivery API response carries its response key, and `umbraco`, which a change to
everything (a domain, language or public access change) purges:

```http
X-Umbraco-Cache-Keys: response-3f9a..., umbraco
```

The `response-` key is a SHA-256 of the request's identity, truncated to 32 hex characters. The
identity is the path, the query parameters that select content (`id`, `fetch`, `filter`, sorted, empty
values dropped), the request culture, the segment and the start item. The same request always gives
the same key on every server. The Delivery API's other parameters (`expand`, `fields`, `sort`,
`skip`, `take`) only change how a response is presented, and any other parameter is ignored, so
requests that differ only in them share a key: the mapping only adds keys, so the shared key covers
everything any of them showed, and a purge reaches each. Input such as `?fields=properties[<random>]`
or `?skip=<n>` therefore makes no new key. A request whose segment or start item is too long to
record gets `no-store` and no key.

The package records which `content-`, `media-`, `schema-` and selector keys (`type-`, `children-`,
`search-`) each response key covers, at most 1000 per response (past that, its items are recorded as
their `type-` keys). A change is planned as item and selector keys, resolved to the response keys that
cover them, and sent with both.

A response the catch-all `search-` key reaches is not recorded. That is a listing with any filter
other than a content type filter (a search, a name filter, a custom filter), however else it is
selected; a listing with no parent or type to select it; a `fetch` of an item that does not exist;
and `items?id=` naming an item that does not exist (it covers each id asked for, so publishing one
later reaches it). Each of these can vary with input without end: recorded, each would be another
mapping row, and every content publish would purge every one of them, so a purge would grow with
every variation ever cached. Such a response carries its keys itself instead, as in item keys mode
below:

```http
X-Umbraco-Cache-Keys: content-e000..., schema-article, search-deliveryapicontentindex, umbraco
```

and the planned `search-` key purges every such response at once, however many are cached. Being
unrecorded, these responses lose culture-limited purging (a purge reaches them in every culture, which
only over-purges) and are not shown by the lookup API.

A frontend or CDN keeps only so many tags per cache entry: Next.js keeps 128 (each at most 256
characters) and ignores the rest. A header carries at most `MaxItemKeys` (64) item keys, plus schema
and selector keys and `umbraco`, which stays under that; lowering `MaxItemKeys` makes room for a
target with a smaller limit.

With `CacheTags:KeyMode` set to `ItemKeys` (opt-in, for a CDN in front of Umbraco that purges by tag
itself), every header lists the item keys as well, up to `MaxItemKeys` (64), then their `type-` keys.
The header name is `CacheTags:HeaderName`.

No keys, and the given `Cache-Control`, on:

- preview requests: `no-store`;
- requests with a member's bearer token, or any authenticated user: `private`.

The package does not send `Vary`. A frontend or CDN that forwards `Accept-Language`, `Accept-Segment`
or `Start-Item` to Umbraco must include them in its own cache key.

## The purge webhook

Sent by the built-in `webhook` target type to each configured target, and received by the Next.js
sample (`samples/README.md`):

```http
POST <target path>
Content-Type: application/json
User-Agent: TheBuilder.CacheTags/<version>
X-Cache-Tags-Timestamp: <unix seconds>
X-Cache-Tags-Signature: sha256=<lowercase hex>

{
  "version": 1,
  "id": "<purge id>",
  "changeId": "<change id>",
  "expire": ["response-...", "content-..."],
  "stale": ["type-article", "children-..."]
}
```

- `expire` holds `response-`, `content-` and `media-` keys: one response or item, purged at once.
  `stale` holds `type-`, `children-`, `search-` and `umbraco`: listings or everything, served stale
  while they refresh. The package decides, so a receiver needs no knowledge of key names.
- `id` is the purge (one request, retried with the same id); `changeId` is the change it is for.
  A change sends several purges: one per `MaxKeysPerRequest` keys, and its listing purge.
- A receiver that does not know `version` must not purge, and answers with an error.
- HMAC-SHA256 with the secret's UTF-8 bytes over `<METHOD>\n<path>\n<timestamp>\n<raw body>`. A
  5-minute window; two secrets accepted for rotation; duplicate ids ignored.
- The secret must be at least 32 characters (use 32 random bytes, base64), and differ between
  environments: the host is not signed, so a purge for one receiver is valid at another with the
  same secret and path. The URL must be https, except on localhost. Redirects are not followed.

Other target types (a CDN's purge API) implement `IPurgeTarget`, register it, and are configured with
`"Type": "<their type>"` and their own `Settings`.

## Cloudflare

The built-in `cloudflare` target purges a Cloudflare zone by cache tag, for a site where Cloudflare
caches the responses that carry the package's keys:

```json
"CacheTags": {
  "Targets": {
    "cdn": {
      "Type": "cloudflare",
      "Settings": { "ZoneId": "<zone id>", "ApiToken": "<token with Zone > Cache Purge>" }
    }
  }
}
```

- Cloudflare reads tags from a `Cache-Tag` response header. Where it caches Umbraco's Delivery API
  directly, set `CacheTags:HeaderName` to `Cache-Tag`; where it caches a frontend, the frontend must
  pass the keys on in its own `Cache-Tag` header.
- Each purge posts the keys as `tags` to `POST /client/v4/zones/{zone}/purge_cache`, at most 100 per
  call (Cloudflare's limit). Cloudflare has no "serve stale" purge, so `stale` keys are purged too.
- Tags are case-insensitive at Cloudflare; the package's keys are lowercase already. A `Cache-Tag`
  header holds about 16 KB, far more than a response's keys.
- A failed call (a bad token, a rate limit) is retried like any purge, and its error is kept with the
  purge for the lookup API.

## Delivery

1. **Ledger.** The server making a change records it (with its plan and an ack signal) as pending.
   It acknowledges the change from its own cache refresh, like every other server: Umbraco raises
   some changes (unpublish, delete, sort, media save) before refreshing. A change that cannot be
   recorded (the write lock timed out) is kept in memory and recorded again by the worker.
2. **Acks.** Every server sends a heartbeat every 30 seconds. When a server applies a cache refresh
   that completes a pending change (the refresh must carry what the change did: published cultures
   for a publish, a branch refresh for a move, and so on, so a draft save does not count; a branch
   refresh or removal of an item's ancestor counts for the item), it evicts
   the change's responses from its Delivery API output cache and acknowledges the change. A refresh
   can arrive before its change is recorded, so unmatched refreshes are tried again for 10 seconds,
   and a refresh counts only for changes made before it (with 10 seconds of slack for clocks).
3. **Release.** Any server releases a change once every active server that was running when it was
   made has acknowledged it, or after `CacheTags:AckTimeout` (60 seconds), logging which servers did
   not. It resolves the plan to response keys and queues one purge per target (split at
   `MaxKeysPerRequest`). Listing responses are in it too, deliberately: the items they show come
   from the published cache, so they are fresh at once, even though their membership and order are
   not yet.
4. **Listings.** Listings come from each server's Delivery API index, which Umbraco updates in a
   background task after the refresh (a second or two; over a minute right after a restart). An
   index handler stamps every document with the state of the item it was built from (version,
   published version and cultures, path, sort order). After acknowledging a content change whose
   plan has listing keys, each server compares that stamp with each item's state in the database
   until they match (or no document is left, for a trashed or deleted item), then evicts the
   change's listing responses from its output cache and records the change as indexed. Once every
   active server that acknowledged the change has, or after `CacheTags:IndexTimeout` (5 minutes), one
   server resolves the listing keys again (a listing first requested in between has a new response
   key) and queues the listing purge. Media listings walk the media cache, and type and site-wide
   changes purge as a whole, so those do not wait.
5. **Outbox.** Any server sends due purges, signed, with leases so each is sent by one server at a
   time. A failure is retried after 2, 4, 8 ... seconds (at most 10 minutes), and kept as dead after
   `CacheTags:MaxAttempts` (10).

A server that has just started applies other servers' changes only once Umbraco's instruction job
first runs, about a minute after start; a change made in that minute waits for it (or the ack
timeout). A server that restarts after a change was released is a new identity that nothing waits
for, and serves Umbraco's listings as its index rebuilds.

Non-200 Delivery API responses (not found, redirects) get `no-store`: no item covers them, so no
purge would reach them.

Every package write takes the package's own Umbraco write lock first (`umbracoLock` id -91730).
On SQLite, two transactions that each read and then write deadlock otherwise.

```json
"CacheTags": {
  "Targets": {
    "nextjs": { "Url": "https://www.example.com/api/cache-tags", "Secret": "<at least 32 characters>" }
  }
}
```

## Your own APIs

A site's own API (anything outside the Delivery API whose responses a frontend caches) joins in
through two public pieces.

**Tagging.** `[CacheTagged]` on an action or controller tags its responses the way the package tags
Delivery API responses: a `response-` key and `umbraco` in the header, recorded with what the response
covers. The action names its keys with the scoped `ICacheTagsResponse`; any Delivery API items in its
result are found on their own.

```csharp
[ApiController, Route("api/dictionary"), CacheTagged(QueryParameters = ["locale"])]
public sealed class DictionaryController(ICacheTagsResponse cacheTags, ...) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(string locale)
    {
        cacheTags.Add($"x-dictionary-{locale.ToLowerInvariant()}");
        ...
    }
}
```

`QueryParameters` lists the parameters that select what a response covers; none by default, and only
those are part of its key, so listing an unbounded parameter makes a key per value. `Recorded = false`
makes each response carry its keys itself instead, for an action whose input varies without end.

**Changes.** `ICacheTagsChanges.Record(keys, waitFor, cultures, reason)` records a change that purges
the keys: custom keys (`x-` and lowercase letters, digits and hyphens), items' `content-` or `media-`
keys, or `umbraco`. With `waitFor`, the purge waits until every server has reported that signal with
`ICacheTagsChanges.Applied(signal)`, which a site calls on every server where its data is refreshed,
usually its cache refresher's notification handler. Without it, the purge goes at once, for data each
server reads straight from the database.

```csharp
public Task HandleAsync(DictionaryItemSavedNotification notification, CancellationToken token)
    => changes.Record(languages.Select(l => $"x-dictionary-{l.IsoCode.ToLowerInvariant()}"), "dictionary");

public void Handle(DictionaryCacheRefresherNotification notification) => changes.Applied("dictionary");
```

Custom keys go in the webhook's `expire` list: each names specific output. The samples use this for
the dictionary (`samples/TheBuilder.CacheTags.Example/Dictionary`).

**By hand.** The Management API purges keys at once (`POST .../cache-tags/purge` with a JSON array of
keys, `["umbraco"]` for everything), lists purges by state (`GET .../purges?state=Dead`), sends a dead
or discarded purge again (`POST .../purges/{id}/retry`), and sets a pending or dead one aside
(`POST .../purges/{id}/discard`). All of it is for administrators.

## Decisions

| Date | Decision |
|---|---|
| 2026-10-21 | Umbraco floor 17.4.0, range `[17.4.0,19.0.0)`. |
| 2026-10-21 | Delivery servers need write access to the package's tables. Read-only delivery servers are not supported in v1. Acks get their own seam (`IAckTransport`), separate from `ICacheTagStore`. |
| 2026-10-21 | Descendant routes are expanded at purge time (`IDocumentNavigationQueryService.TryGetDescendantsKeys`) for route-changing events, overflowing to `type-` or `umbraco` past the budget. The mapping stores no ancestor keys. |
| 2026-10-21 | Readable key names (this table), lowercase, with an optional prefix. |
| 2026-10-21 | `children-` and `search-` keys are purged stale-while-revalidate, like `type-`. |
| 2026-10-22 | Purge targets are a seam (`IPurgeTarget`), configured by name with a `Type`; the webhook is the built-in type. The webhook body is versioned and says which keys to expire and which to mark stale, so receivers need no knowledge of key names. |
| 2026-10-22 | `schema-<alias>` keys: a content, media or element type change (or a data type used by it) purges every response that renders an item of that type, not only listings filtered by it. |
| 2026-10-22 | Listings are purged a second time once every server's Delivery API index has the change, checked by comparing a stamp of the item's state written into each index document with its state in the database, replacing fixed re-purge delays. A time stamp was tried first and accepted a draft's document built just before the publish. |
| 2026-10-22 | Sites extend the package through `[CacheTagged]`, `ICacheTagsResponse` and `ICacheTagsChanges`, with `x-` keys, and a change waits for a named signal each server reports rather than for a cache refresher the package would have to know. A manual purge is the same kind of change. |
| 2026-10-22 | Catch-all listings (search, name filters, other queries) carry their own keys and are not recorded, so user input cannot grow the mapping or the purges. Every response carries `umbraco`, so a change to everything reaches it. |
| 2026-10-22 | Unpublishing always purges every culture: Umbraco raises it only when the whole document goes. A sort's re-publish of the sorted items is not recorded as a publish. Items deleted from the recycle bin are not recorded again. Public access changes purge everything. |

## Debugging

An admin-only Management API lookup:

- a `response-` key gives the request it stands for (path, query, culture, segment, start item) and
  the items and selectors it covers, with names;
- a `content-`, `media-` or selector key gives the response keys that cover it;
- the latest changes list the keys each planned and the responses that plan reaches now.

`GET /umbraco/management/api/v1/cache-tags/lookup?key=<key>` and
`GET /umbraco/management/api/v1/cache-tags/changes?take=20`, for administrators. Each change is also
logged: `Cache tags change <id> (<kind>) plans <keys>`.

The backoffice shows the same under **Settings > Advanced > Cache tags**, with a search, and a Cache
box on each document's and media item's Info tab. The Next.js sample shows each page's keys and
fetch times.

## Milestones

1. Spike (done, `docs/findings.md`; the probe itself is removed).
2. Keys and mapping: the collector, the response key, the mapping store, the source-side ledger and
   purge planner, the lookup API.
3. Outbox, ack barrier, webhook target, signing (done; the listing purge was pulled forward from 4
   as fixed delays).
4. Listings purged once every server's index has the change, with each server evicting its own
   listing responses (done). More event sources (public access, done in 3). Listing keys
   (`type-`, `children-`, `search-`) must be purged once the Delivery API index has the change:
   purged earlier, the frontend refetches the old listing and keeps it until its safety revalidate.
   On a fresh boot the samples measured the index taking over half a minute.
5. Backoffice, Cloudflare target, sites' own APIs, docs (`apps/docs`), NuGet preview (`RELEASE.md`).
