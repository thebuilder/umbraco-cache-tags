---
title: Keys
description: What each cache key covers, which responses get one, and how a change is planned as keys.
seo:
  image: /og/keys.png
---

Every key is lowercase ASCII, because Cloudflare matches tags case-insensitively and Next.js case-sensitively.

| Key | Covers | Purged as |
| --- | --- | --- |
| `response-<hash>` | One Delivery API response | expire |
| `content-<guid>` | One document | expire |
| `media-<guid>` | One media item | expire |
| `type-<alias>` | Every listing filtered by a content or media type | stale |
| `schema-<alias>` | Every response that renders an item of a type, whose shape a type or data type change alters | stale |
| `children-<guid>` | Every listing of a parent's children (`fetch=children:`) | stale |
| `search-<index>` | Every other query against an index, such as a search | stale |
| `x-<name>` | Whatever a site's own API names. See [Your own APIs](/your-apis) | expire |
| `umbraco` | Everything | stale |

"Expire" means the next request waits for fresh content. "Stale" means the next request may get the old response once while it refreshes. Listings and site-wide changes touch many pages, and listings only change once the index has caught up.

## The response key

A response normally carries only its own key and `umbraco`:

```http
X-Umbraco-Cache-Keys: response-3f9a1c..., umbraco
```

The package records which items and listings each response key covers. When content changes, it plans the change as item and listing keys, looks up the response keys that cover them, and purges both. A header stays short however much the response shows, and purging an article also purges the pages that pick it.

The response key is a hash of what selects the content:

- The path
- The query parameters the endpoint reads: `id` for `items?id=`, `fetch` and `filter` for a query (listing) endpoint, none for an item by route or id. Values are lowercased and stripped of whitespace, ids are written in one form, type lists are sorted, and empty and repeated values are dropped.
- The culture and the start item (as the item it resolves to)

The segment is left out, so a response's segment variants share its key. Nothing purges by segment, so a purge still reaches each.

The other Delivery API parameters (`expand`, `fields`, `sort`, `skip`, `take`) only change how Umbraco presents a response, and the package ignores any other parameter. Requests that differ only in those, or in the case, form or order of a value, share a key. The shared key covers everything any of them showed, so a purge reaches each, and input such as `?skip=<n>` cannot make new keys.

The record of what a response covers is written in the background, within moments. A response carries its response key only once that write is queued. When the queue is full, or while writes are failing, the response carries its item and listing keys itself instead, as a catch-all listing does, so a purge reaches it without the record. A failed write is retried one response at a time, so one bad response does not hold up the rest. One the database keeps rejecting while others go through is given up after three tries and logged.

## Catch-all listings

The package does not record a listing selected by a search, a name filter, or any filter other than a content type, or a listing with no parent or content type to select it, such as all descendants of a path. Such listings vary with input without end. Recording them would store a row for each variation, and every publish would purge every one. Each such response carries its keys in its own header instead:

```http
X-Umbraco-Cache-Keys: umbraco, search-deliveryapicontentindex, content-e000..., schema-article
```

Every publish plans the `search-` key, which purges all of them at once, however many are cached. The same goes for a `fetch` of an item that does not exist (by path or by id), a content type filter naming a type that does not exist or more than 2 types, and an `items?id=` request naming more than one item or one that does not exist yet. A request in a culture that is not one of the site's languages is not recorded either, and carries its own keys.

## Which responses get keys

- **Cacheable responses** (200) get keys.
- **Preview requests** get `no-store` and no keys.
- **Requests from a signed-in member or backoffice user** get `private` and no keys.
- **Other responses**, such as not found and redirects, get `no-store`, because no item covers them and no purge would reach them.

## How long a response may be cached

A frontend or CDN can keep a response until a purge reaches it, for at most `MappingRetention`, 90 days by default. The package forgets a recorded response nobody has asked for in that time, and no purge reaches a copy kept longer. A recorded response says so in a header:

```http
CDN-Cache-Control: max-age=7776000
```

CDNs such as Cloudflare read `CDN-Cache-Control`, and browsers ignore it. Next.js sets an entry's lifetime with `cacheLife` only, so keep its `expire` within the retention. Some responses get no such header:

- A response that carries its own keys, such as a catch-all listing, since its keys never age out.
- A response to a request with an `Api-Key`, since a CDN keys its cache by URL and would serve it to anyone.
- A response whose action set its own `Cache-Control` or `CDN-Cache-Control`.

## Call the Delivery API from your server

Fetch from your frontend's server, not from browsers. No purge reaches a browser's copy, and every distinct request a browser or bot makes is recorded. To enforce it, turn off public access:

```json appsettings.json
{
  "Umbraco": {
    "CMS": {
      "DeliveryApi": {
        "PublicAccess": false
      }
    }
  }
}
```

Set the key in `Umbraco:CMS:DeliveryApi:ApiKey` from user secrets or the environment (`Umbraco__CMS__DeliveryApi__ApiKey`), not source control, and send it in an `Api-Key` header from your fetch. A request without it gets a 401 with `no-store`. The same key unlocks preview, so keep it on the server.

## Limits

Next.js keeps at most 128 tags per entry, each at most 256 characters, and drops the rest. A header lists at most `MaxHeaderKeys` (128) keys, with the keys purges need first: its `response-` key, `umbraco`, its listing keys, its `x-` keys and its item keys. Past `MaxItemKeys` (64) items, or when the items would push the header past its limit, it lists their `type-` keys instead. Its `schema-` keys come last, so on a response that renders very many types, a header at its limit loses some of those first. Only a response with more listing, `x-` or `type-` keys than the limit loses any of those, from the end. Umbraco's own output cache keeps every key.

## Item keys mode

With `CacheTags:KeyMode` set to `ItemKeys`, every header lists the item keys as well as the response key. Use it for a CDN in front of Umbraco that purges by tag itself, with no way to look up response keys. The limits above still apply: past `MaxItemKeys`, or when the items would push the header past `MaxHeaderKeys`, it lists their `type-` keys instead.

## A prefix

`CacheTags:KeyPrefix` puts a prefix on every key, such as `shop-`, for a site that shares a CDN zone with another site using the same key names.
