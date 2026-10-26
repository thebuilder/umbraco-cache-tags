![Cache tags under Settings > Advanced in the Umbraco backoffice, with a publish of Harbour mornings opened to show its planned keys and purges](https://raw.githubusercontent.com/thebuilder/umbraco-cache-tags/refs/heads/main/apps/docs/content/screenshots/changes.png)

Cache Tags lets a headless frontend cache Umbraco Delivery API responses for months, and purges exactly the right ones when an editor publishes.

Every cacheable response carries its cache keys in a header. Your frontend tags its cache entries with them. When content changes, the package works out which responses showed it, waits until every Umbraco server serves the change, and sends those keys to your frontend or CDN to purge.

## What it does

- Tags every cacheable Delivery API response with a key, and records which documents, media and listings it covers.
- Purges a publish's page, the listings it appears in, and the pages that pick it, and nothing else.
- Waits until every Umbraco server has applied a change before purging, and purges listings again once every server's Delivery API index has it, so the frontend never refetches the old version.
- Sends purges as a signed webhook, to Cloudflare's purge API, or to a target of your own.
- Tags your site's own API responses too, and purges them when your data changes.
- Shows each change, its purges and what every key covers under **Settings > Advanced > Cache tags**, and on every document's Info tab.

## Install

Needs Umbraco CMS 17.4 through 18.x on .NET 10, with the Delivery API enabled.

```sh
dotnet add package TheBuilder.CacheTags --prerelease
```

Then point the package at your frontend's purge webhook:

```json
{
  "CacheTags": {
    "Targets": {
      "nextjs": { "Url": "https://www.example.com/api/cache-tags", "Secret": "<at least 32 characters>" }
    }
  }
}
```

## Documentation

[cache-tags.thebuilder.dk](https://cache-tags.thebuilder.dk/) has the quickstart, a complete Next.js setup, every key and setting, Cloudflare, and troubleshooting.
