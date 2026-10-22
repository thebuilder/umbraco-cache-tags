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
- The `id`, `fetch` and `filter` query parameters, lowercased and sorted, with empty and repeated ones dropped, and ids written in one form
- The culture, the segment and the start item

The other Delivery API parameters (`expand`, `fields`, `sort`, `skip`, `take`) only change how Umbraco presents a response, and the package ignores any other parameter. Requests that differ only in those, or in the case or form of a value, share a key. The shared key covers everything any of them showed, so a purge reaches each, and input such as `?skip=<n>` cannot make new keys.

The record of what a response covers is written in the background. A response carries its response key only once that write is queued, and a queued write is retried until it succeeds. When the queue is full, the response carries its item and listing keys itself instead, as a catch-all listing does, so a purge always reaches it.

## Catch-all listings

The package does not record a listing selected by a search, a name filter, or any filter other than a content type. Such listings vary with input without end. Recording them would store a row for each variation, and every publish would purge every one. Each such response carries its keys in its own header instead:

```http
X-Umbraco-Cache-Keys: content-e000..., schema-article, search-deliveryapicontentindex, umbraco
```

Every publish plans the `search-` key, which purges all of them at once, however many are cached. The same goes for a `fetch` of an item that does not exist (by path or by id), a content type filter naming a type that does not exist or more than 10 types, and an `items?id=` request naming an item that does not exist yet.

## Which responses get keys

- **Cacheable responses** (200) get keys.
- **Preview requests** get `no-store` and no keys.
- **Requests from a signed-in member or backoffice user** get `private` and no keys.
- **Other responses**, such as not found and redirects, get `no-store`, because no item covers them and no purge would reach them.

## Limits

Next.js keeps at most 128 tags per entry, each at most 256 characters. A header lists at most `MaxItemKeys` (64) item keys, and lists their `type-` keys instead past that. With its schema and listing keys added, a header stays under Next.js's limit.

## Item keys mode

With `CacheTags:KeyMode` set to `ItemKeys`, every header lists the item keys as well as the response key. Use it for a CDN in front of Umbraco that purges by tag itself, with no way to look up response keys.

## A prefix

`CacheTags:KeyPrefix` puts a prefix on every key, such as `shop-`, for a site that shares a CDN zone with another site using the same key names.
