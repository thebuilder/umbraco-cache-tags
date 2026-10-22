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

"Expire" means the next request waits for fresh content. "Stale" means the next request may get the old response once while it refreshes: listings and site-wide changes touch many pages, and listings only change once the index has caught up.

## The response key

A response normally carries only its own key and `umbraco`:

```http
X-Umbraco-Cache-Keys: response-3f9a1c..., umbraco
```

The package records which items and listings each response key covers. When content changes, it plans the change as item and listing keys, looks up the response keys that cover them, and purges both. So a header stays short however much a response shows, and a page that picks an article is purged with it.

The response key is a hash of what selects the content:

- the path;
- the `id`, `fetch` and `filter` query parameters, sorted, empty ones dropped;
- the culture, the segment and the start item.

The other Delivery API parameters (`expand`, `fields`, `sort`, `skip`, `take`) only change how a response is presented, and any other parameter is ignored. Requests that differ only in them share a key. The shared key covers everything any of them showed, so a purge reaches each, and input such as `?skip=<n>` cannot make new keys.

## Catch-all listings

A listing selected by anything other than a parent or a content type, such as a search, a name filter or another filter, is not recorded. It varies with input without end, and each variation would be another row to store and another response to purge on every publish. Such a response carries its keys itself instead:

```http
X-Umbraco-Cache-Keys: content-e000..., schema-article, search-deliveryapicontentindex, umbraco
```

Every publish plans the `search-` key, which purges all of them at once, however many are cached. The same goes for a `fetch` of an item that does not exist, and for `items?id=` naming one that does not exist yet.

## Which responses get keys

- **Cacheable responses** (200) get keys.
- **Preview requests** get `no-store` and no keys.
- **Requests from a signed-in member or backoffice user** get `private` and no keys.
- **Other responses** (not found, redirects) get `no-store`: no item covers them, so no purge would reach them.

## Limits

Next.js keeps 128 tags per entry, each at most 256 characters. A header lists at most `MaxItemKeys` (64) item keys, past which it lists their `type-` keys, plus schema and listing keys, which stays under that.

## Item keys mode

With `CacheTags:KeyMode` set to `ItemKeys`, every header lists the item keys as well as the response key. Use it for a CDN in front of Umbraco that purges by tag itself, with no way to look up response keys.

## A prefix

`CacheTags:KeyPrefix` puts a prefix on every key, such as `shop-`, for a site that shares a CDN zone with another site using the same key names.
