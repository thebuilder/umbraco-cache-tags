---
title: Keys
description: What each cache key covers, which responses get one, and how a change is planned as keys.
seo:
  image: /og/keys.png
---

Every key is lowercase ASCII, because Cloudflare matches tags case-insensitively and Next.js case-sensitively.

| Key | Covers | Purged as |
| --- | --- | --- |
| `content-<guid>` | One document | expire |
| `media-<guid>` | One media item | expire |
| `type-<alias>` | Every listing filtered by a content or media type | stale |
| `children-<guid>` | Every listing of a parent's children (`fetch=children:`) | stale |
| `search-<index>` | Every other query against an index, such as a search | stale |
| `x-<name>` | Whatever a site's own API names. See [Your own APIs](/your-apis) | expire |
| `umbraco` | Everything | stale |

"Expire" means the next request waits for fresh content. "Stale" means the next request may get the old response once while it refreshes. Listings and site-wide changes touch many pages, and listings only change once the index has caught up.

## What a response carries

A response carries the keys of what it shows:

```http
X-Umbraco-Cache-Keys: umbraco, content-e000...0003, content-e000...0004
```

That is `umbraco`, the listing keys that select the response, any `x-` keys, and the `content-` and `media-` keys of the items it shows, the items it links to and the media it uses. A listing carries its listing key too:

```http
X-Umbraco-Cache-Keys: umbraco, children-e000...0001, content-e000...0003, content-e000...0004
```

When content changes, the package plans the change as keys and purges them. A cached response is reached by the keys it carries, so a purge reaches every copy of it, however long it has been cached. Nothing else has to be stored or looked up for that to hold.

## Catch-all listings

A listing selected by a search, a name filter, or any filter other than a content type, or a listing with no parent or content type to select it, such as all descendants of a path, can change with any publish. It carries the catch-all `search-` key:

```http
X-Umbraco-Cache-Keys: umbraco, search-deliveryapicontentindex, content-e000...
```

Every publish plans the `search-` key, which purges all of them at once. The same goes for a `fetch` of an item that does not exist yet (by path or by id), and a content type filter naming a type that does not exist or more than 2 types. A request for items by id (`items?id=`) carries the key of every id asked for, so publishing one that did not exist yet reaches it.

## Which responses get keys

- **Cacheable responses** (200) get keys.
- **Preview requests** get `no-store` and no keys.
- **Requests from a signed-in member or backoffice user** get `private` and no keys.
- **Other responses**, such as not found and redirects, get `no-store`, because no item covers them and no purge would reach them.

## How long a response may be cached

As long as you like. A purge reaches a cached response by the keys it carries, so a long lifetime only matters for a purge that never arrives, such as one to a target that stayed down. In Next.js, `cacheLife("days")` or `cacheLife("weeks")` is a sensible safety net.

## Call the Delivery API from your server

Fetch from your frontend's server, not from browsers. No purge reaches a browser's copy. To enforce it, turn off public access:

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

Next.js keeps at most 128 tags per entry, each at most 256 characters, and drops the rest. A header lists at most `MaxHeaderKeys` (128) keys, with the keys purges need first: `umbraco`, its listing keys, its `x-` keys, then its item keys. Past `MaxItemKeys` (64) items, or when the items would push the header past its limit, it lists their `type-` keys instead. Every change to an item, and every purge of its key by hand, purges its `type-` key too, so such a response is still reached, only more often. Umbraco's own output cache keeps every key.

A full header is about 6 KB. A proxy in front of Umbraco must allow response headers that large. Nginx, for example, answers 502 when they pass its `proxy_buffer_size`, 4 or 8 KB by default, so set it to 16k.

## A prefix

`CacheTags:KeyPrefix` puts a prefix on every key, such as `shop-`, for a site that shares a CDN zone with another site using the same key names.
