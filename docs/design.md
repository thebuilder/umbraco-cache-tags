# Design

`TheBuilder.CacheTags` lets a headless frontend cache Umbraco Delivery API responses and purges
exactly the right ones once every delivery server has applied a change. This is the contract the
package, its samples and its docs are held to.

It supports Umbraco `[17.4.0,19.0.0)`. 17.4.0 added the Delivery API output cache eviction and the
resolved-items stash the package relies on. Every Umbraco server, delivery servers included, needs
write access to the package's tables. Read-only delivery servers are not supported.

## Keys

Every key is lowercase ASCII. Cloudflare matches cache tags case-insensitively and Next.js
case-sensitively, so a key that differed only in case would behave differently per target.

| Key | Means | Purge behaviour at the frontend |
|---|---|---|
| `content-<guid>` | One document | expire now |
| `media-<guid>` | One media item | expire now |
| `type-<alias>` | Every listing filtered by a content or media type (alias lowercased) | stale, refresh in background |
| `children-<guid>` | Every listing of a parent's children (`fetch=children:`) | stale, refresh in background |
| `search-<index>` | Every other query against an index (`search-deliveryapicontentindex`) | stale, refresh in background |
| `umbraco` | Everything | stale, refresh in background |

- "Expire now" is `revalidateTag(key, { expire: 0 })` in Next.js: the next request waits for fresh
  content. "Stale" is `revalidateTag(key, 'max')`: the next request is served the old response and
  refreshes it. Listings only change once Umbraco's index has caught up, which no ack covers, so the
  broad keys are stale-while-revalidate.
- `CacheTags:KeyPrefix` (default empty) prefixes every key, for a site that shares a CDN zone with
  another app using the same names.
- These names deliberately differ from Umbraco's own output cache tags (`umb-dapi-content-{key}`),
  which are internal to Umbraco. The package adds its own keys to Umbraco's output cache entries
  (`OutputCacheTags`) and evicts by those, so the two never need to match.

## The response header

Each cacheable Delivery API response carries the keys of what it shows:

```http
X-Umbraco-Cache-Keys: umbraco, children-e000..., content-e000..., content-e001...
```

- `umbraco`, which a change to everything (a domain, language or public access change) purges;
- the selector keys that select it, for a listing (`type-`, `children-`, or the catch-all `search-`);
- the site's own `x-` keys, for a tagged action (see Your own APIs);
- the `content-` and `media-` keys of the items it shows, the items they pick or link to, and the
  media they use (`ResponseCollector`, `ReferenceReader`, `OutputWalker`).

A purge reaches a cached response by the keys it carries, as with any tag-based cache. Nothing is
stored per response, so nothing has to be written, kept or looked up for a purge to reach a copy,
however long it has been cached, and requests cannot make the package store anything.

A listing gets the catch-all `search-` key when nothing narrower selects it:

- a filter other than a content type filter (a search, a name filter, a custom filter), however else
  the listing is selected;
- no parent or type to select it;
- a `fetch` without an axis (`children`, `descendants`, `ancestors`) and an item that exists, by path
  or by id;
- a type filter naming a type that does not exist (one longer than an alias can be included), or more
  than 2 types, whatever the `fetch`.

Content and media types are read at most once a minute, so one created in the last minute counts as
unknown until then. Every content publish plans the `search-` key, which purges every such listing
at once:

```http
X-Umbraco-Cache-Keys: umbraco, search-deliveryapicontentindex, content-e000...
```

`items?id=` carries the key of every id asked for, so publishing one that did not exist yet reaches
it.

A frontend or CDN keeps only so many tags per cache entry: Next.js keeps 128 (each at most 256
characters) and ignores the rest. A header carries at most `MaxHeaderKeys` (128) keys, most needed
first: `umbraco`, its selector keys, its `x-` keys, then its item keys. Items past 64, or items that would push the header past the limit, are listed as their `type-` keys instead.
Every change to an item plans its `type-` key, and a route change plans its descendants' `type-` keys
as well as their content keys, so such a header is still reached, only more often. A response with
more selector, `x-` or `type-` keys than the limit loses the last of those. Umbraco's output cache
has no limit, so its entry is tagged with every key the header would carry. Raise
`MaxHeaderKeys` for a target that limits tags by size, such as Cloudflare in front of Umbraco, which
keeps 16 KB of `Cache-Tag` per response: about 300 keys, fewer with a `KeyPrefix`. A header of 128
keys is about 6 KB and one of 300 about 15 KB, which a proxy in front of Umbraco must allow (nginx
needs a `proxy_buffer_size` of 16k or more).

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
  "expire": ["content-...", "x-dictionary-da-dk"],
  "stale": ["type-article", "children-..."]
}
```

- `expire` holds `content-`, `media-` and `x-` keys: one item or piece of custom output, purged
  at once. `stale` holds `type-`, `children-`, `search-` and `umbraco`:
  listings, types or everything, served stale while they refresh. The package decides, so a receiver needs no knowledge of key names.
- `id` is the purge (one request, retried with the same id); `changeId` is the change it is for.
  A change sends several purges: one per 500 keys, and its listing purge.
- A receiver that does not know `version` must not purge, and answers with an error.
- HMAC-SHA256 with the secret's UTF-8 bytes over `<METHOD>\n<path>\n<timestamp>\n<raw body>`. A
  5-minute window; two secrets accepted for rotation; duplicate ids ignored.
- The secret must be at least 32 characters (use 32 random bytes, base64), and differ between
  environments: the host is not signed, so a purge for one receiver is valid at another with the
  same secret and path. The URL must be https, except on localhost. Redirects are not followed.

Other target types (a CDN's purge API) implement `IPurgeTarget`, register it, and are configured with
`"Type": "<their type>"` and their own `Settings`.

Targets are configured in `CacheTags:Targets` or added in the backoffice (Settings > Advanced > Cache
tags > Targets, administrators only), stored in `theBuilderCacheTagsTarget` as JSON and read by every
server at most 5 seconds late. A configured name wins over a backoffice one. Each backoffice value
(signing secret, header, setting) is typed in or names a secret under `CacheTags:Secrets`, read at
send time. A name cannot contain `:`, so a target can read nothing else from configuration, and an
administrator cannot send it the connection string or another target's secret. The package stores
no secret of its own. A secret missing on a server fails that server's purges with an error that
names the header or signing secret, never the secret's name, and they retry like any purge. Backoffice
targets reach only public addresses unless `CacheTags:AllowPrivateNetworkTargets`; webhooks send their
target's `Headers`, but never one the purge sets itself.

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

- Cloudflare reads tags from a `Cache-Tag` response header. Where it caches a frontend, the
  frontend must pass the keys on in its own `Cache-Tag` header. Where it caches Umbraco's Delivery
  API directly, which suits a public API only, set `CacheTags:HeaderName` to `Cache-Tag`.
- Each purge posts the keys as `tags` to `POST /client/v4/zones/{zone}/purge_cache`, at most 100 per
  call (Cloudflare's limit). Cloudflare has no "serve stale" purge, so `stale` keys are purged too.
- Tags are case-insensitive at Cloudflare; the package's keys are lowercase already. A `Cache-Tag`
  header holds about 16 KB, far more than a response's keys.
- A failed call (a bad token, a rate limit) is retried like any purge, and its error is kept with the
  purge for the backoffice.

## Planning

The server where a change happens plans it as keys, in `PurgePlanner`:

- A route change (rename, move, unpublish) also reaches the descendants, whose routes follow. The
  planner expands them when the change is recorded, with
  `IDocumentNavigationQueryService.TryGetDescendantsKeys`, up to 500, and
  plans `umbraco` past that. A response carries no ancestor keys.
- A route change also plans the descendants' `type-` keys, read with `IEntityService`. A header past
  64 items lists its items' types instead of the items, so only those reach it. An unpublish
  also takes the descendants out of the listings of their types, including pages of a listing that
  do not show them. When their types cannot be read, it plans `umbraco`.
- A custom change, or a purge by hand, that names an item's `content-` or `media-` key also plans
  the item's `type-` key, read with `IEntityService`, since a header past 64 items lists its items'
  types. When the types cannot be read, it plans `umbraco`.
- A sort plans no item keys, since the items did not change, but every listing that can show them
  in their new order: the parent's `children-` key, the sorted items' `type-` keys and the catch-all
  `search-` key. It waits for the index like any listing.
- A content type, media type or data type change purges everything (`umbraco`), since any response
  may render an item of the type. Type changes are rare and usually ship with a deploy, so a key per
  type is not worth its place in every header. The plan also names the `type-` keys of the types it
  alters, for the listing purge below.
- Renaming a content or media type's alias names the `type-` keys of the old alias as well as the
  new one: listings rendered before the rename carry the old one. Umbraco's
  changed notification carries only the new alias, so the old one is read when the type is saving.
  A content type's rename waits for the index (Delivery, step 4), which moves its documents from
  one `filter=contentType:` to the other up to a minute later.
- A purge reaches every culture of an item: a response's keys carry no culture, so publishing one
  culture purges the item's responses in all of them.
- A sort's re-publish of the sorted items is not recorded as a publish, and items deleted from the
  recycle bin are not recorded again.
- A domain, language or public access change purges everything.

## Delivery

1. **Ledger.** The server making a change records it (with its plan and an ack signal) as pending.
   It acknowledges the change from its own cache refresh, like every other server: Umbraco raises
   some changes (unpublish, delete, sort, media save) before refreshing. A change that cannot be
   recorded (the write lock timed out) is kept in memory and recorded again by the worker.
2. **Acks.** Every server sends a heartbeat every 30 seconds. When a server applies a cache refresh
   that completes a pending change (the refresh must carry what the change did: published cultures
   for a publish, a branch refresh for a move, and so on, so a draft save does not count; a branch
   refresh or removal of an item's ancestor counts for the item; a type, data type, language,
   domain or public access change needs the refresh of that same type, data type, language or
   domain), it evicts the change's responses from its Delivery API output cache and acknowledges
   the change. A change is timed when Umbraco raises it, before its items are read. A refresh seen
   more than 2 seconds after a change reloaded its items after it, so one refresh can complete
   several changes made before it. A refresh can also arrive moments before its change is
   recorded (Umbraco raises some changes after refreshing), so unmatched refreshes are tried again
   for 10 seconds. One seen within 2 seconds of a change, before or after, counts for that one
   change only, and not if its notification already completed another change. Those 2 seconds also
   cover how far apart two servers' clocks may be, so keep servers' clocks in sync (NTP does). A
   server more than 2 seconds behind is not acknowledged in time: its changes wait for the ack
   timeout, and it evicts them once they are released (step 3).
3. **Release.** Any server releases a change once every active server that was running when it was
   made has acknowledged it, or after 60 seconds, logging which servers did
   not. It queues one purge of the planned keys per target (at most 500 keys per request). The
   listing keys are in it too, deliberately: the items listings show come from the published cache,
   so they are fresh at once, even though their membership and order are not yet. A server that a change was released without evicts the change's responses and listings
   from its own output cache once it has seen a refresh of the change, whether before the release
   (its clock is behind) or up to 15 minutes after (it applied the change late). It keeps the
   refreshes it applied for those 15 minutes, and evicts again for each new one: evicting early only
   costs a cache miss, and the change's own refresh evicts after it. It does not acknowledge the
   change and is not waited for in the listings stage, so its listings may be cached again from its
   index before the index has the change.
4. **Listings.** Listings come from each server's Delivery API index, which Umbraco updates in a
   background task after the refresh (a second or two; over a minute right after a restart). An
   index handler stamps every document with the state of the item it was built from (version,
   published version and cultures, path, sort order). After acknowledging a content change whose
   plan has listing keys, each server compares that stamp with each item's state in the database
   until they match (or no document is left, for a trashed or deleted item; or, for a renamed
   content type, until no document has the old alias), then evicts the
   change's listings from its output cache and records the change as indexed. Once every active
   server that acknowledged the change has, or after 5 minutes, one server
   queues the listing purge, which sends the plan's listing keys again. Media listings walk the media cache, and other type changes
   and site-wide changes purge as a whole, so those do not wait.
5. **Outbox.** Any server sends due purges, signed, with leases so each is sent by one server at a
   time. Each lease runs 30 seconds from when the purge is claimed, just before it is sent, so slow
   sends before it do not leave it expired, and a purge is claimed only while it is due, so a server
   working from an older list does not resend one another server just failed. A failure is retried
   after 2, 4, 8 ... seconds (at most 10 minutes), counted in the database, and kept as dead after
   10 attempts. A dead purge stays until someone retries it or sets it aside, since a copy it missed
   is still cached. Released changes and sent or set-aside purges are pruned after 7 days.

The delivery worker and the heartbeat log an error and carry on, so neither stops the site. Settings
that fail validation at start stop the site; settings that become invalid on a reload of
appsettings are logged once, and the last valid settings stay in use until they are fixed.

Heartbeats and acks go through `IAckTransport`, which keeps them in the database by default. A site
can replace it.

A server that has just started applies other servers' changes only once Umbraco's instruction job
first runs, about a minute after start; a change made in that minute waits for it (or the ack
timeout). A server that restarts after a change was released is a new identity that nothing waits
for, and serves Umbraco's listings as its index rebuilds.

Non-200 Delivery API responses (not found, redirects) get `no-store`: no item covers them, so no
purge would reach them. The package tags a response before its result runs, so a result that sets
its status only as it runs (a redirect, such as the Delivery API's 301 for an item's old route, a
challenge or a forbid) counts as not a 200, as does a result type it does not know.

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
Delivery API responses: `umbraco` and the keys of what the response shows, in the header. The action
names its keys with the scoped `ICacheTagsResponse`, and the package finds any Delivery API items in
its result on its own.

```csharp
[ApiController, Route("api/dictionary"), CacheTagged]
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

Only a 200 is tagged, read from the result before it runs: an `ObjectResult`, `JsonResult`,
`ContentResult`, status code result, file or `EmptyResult`, or a typed `IResult` (`TypedResults`,
`Results<...>`) with a status code. Any other result (a redirect, a challenge, a forbid, a result
type the package does not know) gets `no-store` and no key.

**Changes.** `ICacheTagsChanges.Record(keys, waitFor, reason)` records a change that purges
the keys: custom keys (`x-` and lowercase letters, digits and hyphens), items' `content-` or `media-`
keys, or `umbraco`. With `waitFor`, the purge waits until every server has reported that signal with
`ICacheTagsChanges.Applied(signal)`, which a site calls on every server where its data is refreshed,
usually its cache refresher's notification handler. Without it, the purge goes at once, for data each
server reads straight from the database.

```csharp
public async Task HandleAsync(DictionaryItemSavedNotification notification, CancellationToken token)
    => changes.Record((await languages.GetAllAsync()).Select(l => $"x-dictionary-{l.IsoCode.ToLowerInvariant()}"), "dictionary");

public void Handle(DictionaryCacheRefresherNotification notification) => changes.Applied("dictionary");
```

Custom keys go in the webhook's `expire` list: each names specific output. The samples use this for
the dictionary (`samples/TheBuilder.CacheTags.Example/Dictionary`).

**By hand.** The Management API purges keys at once (`POST .../cache-tags/purge` with a JSON array of
keys, `["umbraco"]` for everything), lists purges by state (`GET .../purges?state=Dead`), sends a dead
or discarded purge again (`POST .../purges/{id}/retry`), and sets a pending or dead one aside
(`POST .../purges/{id}/discard`). All of it is for administrators.

## Debugging

A response's header shows the keys it carries. The admin-only Management API lists the latest
changes, with the keys each planned, which servers applied and indexed it, and its purges
(`GET /umbraco/management/api/v1/cache-tags/changes?take=20`). Each change is also logged:
`Cache tags change <id> (<kind>) plans <keys>`.

The backoffice shows the same under **Settings > Advanced > Cache tags**, and a Cache box on each
document's and media item's Info tab gives the item's key and purges it. The Next.js sample shows each
page's keys and fetch times.
