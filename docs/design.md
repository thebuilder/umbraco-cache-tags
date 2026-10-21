# Design

`TheBuilder.CacheTags` lets a headless frontend cache Umbraco Delivery API responses and purges
exactly the right ones once every delivery server has applied a change. This is the contract the
package, its samples and its docs are held to. Evidence for the decisions is in `spike/FINDINGS.md`.

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

Each cacheable Delivery API response carries one key:

```http
X-Umbraco-Cache-Keys: response-3f9a...
```

The `response-` key is a SHA-256 of the request's identity, truncated to 32 hex characters. The
identity is the path, the query string normalised (parameters sorted, empty values and the defaults
`skip=0` and `take=10` dropped), the request culture, the segment and the start item. The same
request always gives the same key on every server.

The package records which `content-`, `media-`, `schema-` and selector keys (`type-`, `children-`,
`search-`) each response key covers, at most 1000 per response (past that, its items are recorded as
their `type-` keys). Only the Delivery API's own query parameters (`id`, `fetch`, `filter`, `sort`,
`skip`, `take`, `expand`, `fields`) are part of the key; any other parameter leaves it unchanged. A
request whose segment or start item is too long to record gets `no-store` and no key. A change is planned as item and selector keys and resolved to the response
keys that cover them. With `CacheTags:KeyMode` set to `ItemKeys` (opt-in, for a CDN in front of Umbraco that purges by tag
itself), the header lists the item keys instead, up to `MaxItemKeys` (64), then falls back to
`type-` keys. The header name is `CacheTags:HeaderName`.

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
  A change sends several purges: one per `MaxKeysPerRequest` keys, and the listing re-purges.
- A receiver that does not know `version` must not purge, and answers with an error.
- HMAC-SHA256 with the secret's UTF-8 bytes over `<METHOD>\n<path>\n<timestamp>\n<raw body>`. A
  5-minute window; two secrets accepted for rotation; duplicate ids ignored.
- The secret must be at least 32 characters (use 32 random bytes, base64), and differ between
  environments: the host is not signed, so a purge for one receiver is valid at another with the
  same secret and path. The URL must be https, except on localhost. Redirects are not followed.

Other target types (a CDN's purge API) implement `IPurgeTarget`, register it, and are configured with
`"Type": "<their type>"` and their own `Settings`.

## Delivery

1. **Ledger.** The server making a change records it (with its plan and an ack signal) as pending,
   and acknowledges it itself: it applied the cache refresh before the change was recorded.
2. **Acks.** Every server sends a heartbeat every 30 seconds. When a server applies a cache refresh
   that completes a pending change (the refresh must carry what the change did: published cultures
   for a publish, a branch refresh for a move, and so on, so a draft save does not count), it evicts
   the change's responses from its Delivery API output cache and acknowledges the change. A refresh
   can arrive before its change is recorded, so unmatched refreshes are tried again for 10 seconds.
3. **Release.** Any server releases a change once every active server that was running when it was
   made has acknowledged it, or after `CacheTags:AckTimeout` (60 seconds), logging which servers did
   not. It resolves the plan to response keys and queues one purge per target (split at
   `MaxKeysPerRequest`), plus purges of the listing responses again after
   `CacheTags:ListingRepurgeDelays` (5, 20 and 45 seconds), since listings come from an index updated
   in the background, slowly right after a restart.
4. **Outbox.** Any server sends due purges, signed, with leases so each is sent by one server at a
   time. A failure is retried after 2, 4, 8 ... seconds (at most 10 minutes), and kept as dead after
   `CacheTags:MaxAttempts` (10).

Every package write takes the package's own Umbraco write lock first (`umbracoLock` id -91730).
On SQLite, two transactions that each read and then write deadlock otherwise.

```json
"CacheTags": {
  "Targets": {
    "nextjs": { "Url": "https://www.example.com/api/cache-tags", "Secret": "<at least 32 characters>" }
  }
}
```

## Decisions

| Date | Decision |
|---|---|
| 2026-10-21 | Umbraco floor 17.4.0, range `[17.4.0,19.0.0)`. |
| 2026-10-21 | Delivery servers need write access to the package's tables. Read-only delivery servers are not supported in v1. Acks get their own seam (`IAckTransport`), separate from `ICacheTagStore`. |
| 2026-10-21 | Descendant routes are expanded at purge time (`IDocumentNavigationQueryService.TryGetDescendantsKeys`) for route-changing events, overflowing to `type-` or `umbraco` past the budget. The mapping stores no ancestor keys. |
| 2026-10-21 | Readable key names (this table), lowercase, with an optional prefix. |
| 2026-10-21 | `children-` and `search-` keys are purged stale-while-revalidate, like `type-`. |

## Debugging

An admin-only Management API lookup:

- a `response-` key gives the request it stands for (path, query, culture, segment, start item) and
  the items and selectors it covers, with names;
- a `content-`, `media-` or selector key gives the response keys that cover it;
- the latest changes list the keys each planned and the responses that plan reaches now.

`GET /umbraco/management/api/v1/cache-tags/lookup?key=<key>` and
`GET /umbraco/management/api/v1/cache-tags/changes?take=20`, for administrators. Each change is also
logged: `Cache tags change <id> (<kind>) plans <keys>`.

Milestone 5 puts a backoffice UI on it. The Next.js sample shows each page's keys and fetch times.

## Milestones

1. Spike (done, `spike/FINDINGS.md`).
2. Keys and mapping: the collector, the response key, the mapping store, the source-side ledger and
   purge planner, the lookup API.
3. Outbox, ack barrier, webhook target, signing (and the listing re-purge, pulled forward from 4).
4. More event sources, distributed output cache eviction for the listing re-purge. Listing keys
   (`type-`, `children-`, `search-`) must be purged once the Delivery API index has the change:
   purged earlier, the frontend refetches the old listing and keeps it until its safety revalidate.
   On a fresh boot the samples measured the index taking over half a minute.
5. Backoffice, Cloudflare target, docs, NuGet preview.
