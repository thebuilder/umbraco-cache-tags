---
title: Cache Tags for Umbraco
description: Cache Umbraco Delivery API responses at the frontend, and purge exactly the right ones once every server has applied a change.
sidebar:
  label: Overview
seo:
  image: /og/overview.png
---

Cache Tags lets a headless frontend cache Umbraco Delivery API responses for months, and purges exactly the right ones when an editor publishes.

Every cacheable response carries its cache keys in a header. The frontend tags its cache entries with them. When content changes, the package works out which responses showed it, waits until every Umbraco server has applied the change, and sends those keys to the frontend or CDN to purge.

- **Exact purges.** Publishing an article purges only the responses that showed it, such as its page, the listings it appears in and the pages that pick it.
- **No stale refetch.** A purge goes out only once every delivery server serves the new content, and listings only once every server's index has it. A frontend that refetches at once gets the new version, not the old one again.
- **Your frontend decides how to cache.** The keys work with Next.js `cacheTag` and `revalidateTag`, with a CDN that purges by tag such as Cloudflare, or with anything that can receive a signed webhook.

## How it fits together

1. A frontend fetches from the Delivery API. The response carries `X-Umbraco-Cache-Keys: response-3f9a..., umbraco`.
2. The frontend caches the response, tagged with those keys.
3. An editor publishes. The package records the change and plans which keys it touches.
4. Every Umbraco server applies the change and acknowledges it.
5. The package sends the keys to each purge target: a signed webhook to your frontend, or Cloudflare's purge API.
6. The frontend expires the tagged entries, and the next request renders the new content.

Listings, such as an article list or a search, come from each server's Delivery API index, which Umbraco updates a moment after the change. The package purges them a second time once every server's index has it. See [How a purge is timed](/timing).

## Requirements

- Umbraco CMS 17.4 or later, Umbraco 18 included, on .NET 10, with the Delivery API enabled.
- SQL Server, or SQLite for a single server. Every Umbraco server, delivery servers included, needs write access to the package's tables.
- A frontend or CDN that can tag its cache entries and purge by tag. The [Next.js guide](/nextjs) has a complete receiver.

Where you can, call the Delivery API from your frontend's server rather than from browsers. See [Call the Delivery API from your server](/keys#call-the-delivery-api-from-your-server).

## Where to go next

- [Why Cache Tags](/why) compares it with caching on a timer, purging everything and purging by path.
- [Quickstart](/quickstart) installs the package and purges a first page.
- [Keys](/keys) explains what each key covers.
- [Your own APIs](/your-apis) tags responses from your site's own controllers, such as a dictionary endpoint.
- [Troubleshooting](/troubleshooting) covers the problems people run into first.
