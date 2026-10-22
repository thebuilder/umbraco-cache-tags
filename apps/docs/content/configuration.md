---
title: Configuration
description: Every Cache Tags setting, under CacheTags in appsettings.json.
seo:
  image: /og/configuration.png
---

Everything lives under `CacheTags` in `appsettings.json`. Only `Targets` needs setting; the rest has defaults that suit most sites.

```json appsettings.json
{
  "CacheTags": {
    "Targets": {
      "nextjs": { "Url": "https://www.example.com/api/cache-tags", "Secret": "<at least 32 characters>" }
    }
  }
}
```

## Settings

| Setting | Default | What it does |
| --- | --- | --- |
| `Enabled` | `true` | Whether responses are tagged, and changes planned and sent. |
| `Targets` | none | Where purges are sent, by name. See [Purge targets](/targets). Without any, changes are still planned and recorded. |
| `HeaderName` | `X-Umbraco-Cache-Keys` | The response header that carries the keys. `Cache-Tag` for Cloudflare in front of Umbraco. |
| `KeyPrefix` | none | Prefixed to every key, such as `shop-`, for a site sharing a CDN zone with another. Lowercase letters, digits and hyphens, ending in a hyphen. |
| `KeyMode` | `ResponseKey` | `ItemKeys` lists the item keys in every header too, for a CDN that purges by tag itself. See [Keys](/keys#item-keys-mode). |
| `MaxItemKeys` | `64` | The most item keys a header lists before it lists their `type-` keys instead. |
| `MaxDescendants` | `500` | The most descendants a rename, move or unpublish purges one by one before it purges everything instead. |
| `MappingRetention` | 7 days | How long the package remembers which keys a response covers after it was last served. Do not cache a response longer than this. |
| `AckTimeout` | 60 seconds | How long a change waits for every server to apply it before it is sent anyway. |
| `IndexTimeout` | 5 minutes | How long a change's listing purge waits for every server's index. |
| `ServerTimeout` | 2 minutes | How long since its last heartbeat a server still counts as running. Servers send one every 30 seconds. |
| `PollInterval` | 1 second | How often each server looks for changes to release and purges to send while there are some. |
| `MaxAttempts` | `10` | How many times a purge is tried before it is kept as failed. |
| `LedgerRetention` | 7 days | How long released changes and sent purges are kept for the backoffice. |

Times are written as .NET time spans, such as `"00:01:00"` for a minute.

## Target settings

| Setting | Default | What it does |
| --- | --- | --- |
| `Type` | `webhook` | `webhook`, `cloudflare`, or the type of a target you registered. |
| `Enabled` | `true` | Whether purges are sent to this target. |
| `Url` | none | For a webhook: where purges are posted. https, except on localhost. |
| `Secret` | none | For a webhook: the signing secret, at least 32 characters. |
| `MaxKeysPerRequest` | `500` | The most keys one request carries; more are split over several. |
| `Settings` | none | Settings for other types, such as Cloudflare's `ZoneId` and `ApiToken`. |

Keep secrets and tokens out of `appsettings.json` in source control: use environment variables (`CacheTags__Targets__nextjs__Secret`) or your host's secret store.
