---
title: Quickstart
description: Install Cache Tags, point it at your frontend, and watch a publish purge exactly the pages that changed.
seo:
  image: /og/quickstart.png
---

## 1. Install the package

```sh
dotnet add package TheBuilder.CacheTags --prerelease
```

Start the site. On the first start the package creates its tables. From then on, every cacheable Delivery API response carries its keys:

```http
GET /umbraco/delivery/api/v2/content/item/articles/harbour-mornings/

X-Umbraco-Cache-Keys: response-2de23a3a68340e675d917313029cab2e, umbraco
```

Preview requests, requests from a signed-in member or backoffice user, and responses other than 200 get no keys and are marked `no-store` or `private`.

## 2. Tag your cache entries

Read the header in your frontend and tag the cached response with each key. In Next.js with Cache Components:

```ts lib/umbraco.ts
import { cacheLife, cacheTag } from "next/cache";

export async function deliveryApi<T>(path: string): Promise<T> {
  "use cache";
  const response = await fetch(`${process.env.UMBRACO_URL}/umbraco/delivery/api/v2/${path}`);
  const keys = (response.headers.get("x-umbraco-cache-keys") ?? "")
    .split(",")
    .map((key) => key.trim())
    .filter(Boolean);
  cacheTag(...keys);
  cacheLife("days");
  return response.json();
}
```

The [Next.js guide](/nextjs) has the complete helper, with errors and culture handled.

## 3. Add the purge webhook

Your frontend needs an endpoint that receives purges and expires the tagged entries. The [Next.js guide](/nextjs#the-purge-webhook) has a complete route handler to copy. It checks the signature, then calls `revalidateTag` for each key.

## 4. Point the package at it

In the Umbraco site's `appsettings.json`:

```json appsettings.json
{
  "CacheTags": {
    "Targets": {
      "nextjs": {
        "Url": "https://www.example.com/api/cache-tags",
        "Secret": "<at least 32 random characters>"
      }
    }
  }
}
```

Give the frontend the same secret, as `CACHE_TAGS_WEBHOOK_SECRET` in the guide's route handler. Generate it with `openssl rand -base64 32`, and use a different one for each environment.

## 5. Publish something

Publish a page your frontend shows. Within a second or two:

- **Settings > Advanced > Cache tags > Changes** shows the change, the keys it planned, and the purge it sent.
- The page, and every listing that shows it, renders the new content on the next request.

Open the page's **Info** tab to see the cached responses it is in, or purge it by hand. See [Backoffice](/backoffice).
