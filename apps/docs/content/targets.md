---
title: Purge targets
description: Send purges to your frontend as a signed webhook, to Cloudflare, or to anything else through your own target.
seo:
  image: /og/targets.png
---

A target is where purges go. Configure any number, by name, under `CacheTags:Targets`. Each has a `Type`: `webhook` (the default) or `cloudflare` built in, or one you register.

```json appsettings.json
{
  "CacheTags": {
    "Targets": {
      "nextjs": { "Url": "https://www.example.com/api/cache-tags", "Secret": "<at least 32 characters>" },
      "cdn": {
        "Type": "cloudflare",
        "Settings": { "ZoneId": "<zone id>", "ApiToken": "<token>" }
      }
    }
  }
}
```

Every target gets every purge. Set `"Enabled": false` to pause one without removing it.

## The webhook

```http
POST https://www.example.com/api/cache-tags
Content-Type: application/json
User-Agent: TheBuilder.CacheTags/<version>
X-Cache-Tags-Timestamp: 1792663200
X-Cache-Tags-Signature: sha256=<lowercase hex>

{
  "version": 1,
  "id": "<purge id>",
  "changeId": "<change id>",
  "expire": ["response-3f9a...", "content-e000..."],
  "stale": ["type-article", "children-e000..."]
}
```

- **`expire`** holds keys that name one response or one item (`response-`, `content-`, `media-`, `x-`). Purge them now.
- **`stale`** holds listing keys and `umbraco`. Serve them stale while they refresh, if the receiver can; otherwise purge them too.
- **`id`** is the purge. A retry sends the same id, so a receiver can ignore one it has handled. **`changeId`** is the change it is for; one change can send several purges.
- **`version`** is 1. A receiver that does not know the version must refuse the purge with an error, so the package retries it.

The signature is HMAC-SHA256, keyed with the secret's UTF-8 bytes, over:

```text
POST\n<path>\n<timestamp>\n<raw body>
```

A receiver checks it against the raw body, refuses a timestamp more than 5 minutes off, and ignores ids it has seen.

- The secret must be at least 32 characters, such as `openssl rand -base64 32`, and differ between environments: the host is not signed, so a purge for one receiver is valid at another with the same secret and path.
- The URL must be https, except on localhost. Redirects are not followed.
- `MaxKeysPerRequest` (500) splits a large purge over several requests.

The [Next.js guide](/nextjs#the-purge-webhook) has a complete receiver.

## Cloudflare

The `cloudflare` type purges a Cloudflare zone by cache tag. Create an API token with the **Zone > Cache Purge** permission for the zone.

- Cloudflare reads tags from a `Cache-Tag` response header. Where Cloudflare caches Umbraco's Delivery API directly, set `CacheTags:HeaderName` to `Cache-Tag`. Where it caches your frontend, the frontend must pass the keys on in its own `Cache-Tag` header.
- Each purge posts the keys to `POST /client/v4/zones/{zone}/purge_cache` as `tags`, at most 100 per call.
- Cloudflare has no "serve stale" purge, so `stale` keys are purged too.
- A failed call, such as a bad token or a rate limit, is retried like any purge, and its error is shown with the purge in the backoffice.

## Your own target

Implement `IPurgeTarget` for another CDN or cache, and register it as a singleton:

```csharp
public sealed class FastlyTarget(IHttpClientFactory http) : IPurgeTarget
{
    public string Type => "fastly";

    public async Task<string?> SendAsync(PurgeRequest request, CacheTagsTargetOptions target, CancellationToken cancellationToken)
    {
        var keys = request.Expire.Concat(request.Stale);
        // Send the keys with target.Settings["ServiceId"] and target.Settings["ApiToken"].
        // Return null on success, or what went wrong: the purge is retried with backoff.
        return null;
    }
}

builder.Services.AddSingleton<IPurgeTarget, FastlyTarget>();
```

Then configure a target with `"Type": "fastly"` and its `Settings`.
