---
title: Purge targets
description: Send purges to your frontend as a signed webhook, to Cloudflare, or to anything else through your own target.
seo:
  image: /og/targets.png
---

A target is where purges go. Add them in the backoffice, under **Settings > Advanced > Cache tags > Targets**, or by name under `CacheTags:Targets` in `appsettings.json`. Each has a type: `webhook` (the default) or `cloudflare` built in, or one you register.

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

## In the backoffice

Administrators add, edit, test and remove targets under **Settings > Advanced > Cache tags > Targets**. Targets in `appsettings.json` are listed there too, read-only, without their secrets or header values.

- **A secret is a typed value or an app setting.** Every value, the signing secret, a header or a Cloudflare token, is either typed in or names an app setting, such as `CacheTags:Vercel:Secret`, which is read when a purge is sent. A typed value is stored in the database in plain text and shown to administrators, so keep secrets in a setting: an environment variable (`CacheTags__Vercel__Secret`) or your host's secret store.
- **A missing setting fails the purge, not the save.** If a setting a target names is not set on a server, the target shows a problem, and its purges from that server fail with an error naming the header or secret, never the setting. They are retried, and go through once the setting is deployed.
- **Send test purge** sends the target, saved or not, a purge of `x-cache-tags-test`, a key nothing carries, and says whether it was accepted.
- **The name never changes.** Purges are queued under it. Deleting a target sets its unsent purges aside.
- **A name in `appsettings.json` wins.** A backoffice target with the same name is ignored, and the log says so.
- **Public addresses only.** A backoffice target may only connect to public internet addresses, so an administrator cannot point one at a service inside your network. Set `CacheTags:AllowPrivateNetworkTargets` to `true` to allow private ones, such as `localhost` in development. Targets in `appsettings.json` are not limited. A target type you register does its own requests, so the limit covers only the built-in types.
- **No outbound proxy.** To check the address it connects to, a backoffice target connects directly, not through `HTTPS_PROXY` or the system proxy. A site that reaches the internet only through a proxy must configure its targets in `appsettings.json`, which use the proxy as usual.
- Other servers see a change to a target within a few seconds.

## Request headers

A webhook can send headers of its own with every purge, such as a gateway's API key, or `x-vercel-protection-bypass` for a protected Vercel deployment. In `appsettings.json`:

```json appsettings.json
"nextjs": {
  "Url": "https://www.example.com/api/cache-tags",
  "Secret": "<at least 32 characters>",
  "Headers": { "x-vercel-protection-bypass": "<bypass secret>" }
}
```

At most 20, with values on one line. Headers the purge sets itself (`Host`, `Content-Type`, `User-Agent`, the signature headers and the like) are refused. The webhook's signature is still what proves a purge came from Umbraco.

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

Then configure a target with `"Type": "fastly"` and its `Settings`, in `appsettings.json` or the backoffice. Its `Headers` are there too if your target wants them.
