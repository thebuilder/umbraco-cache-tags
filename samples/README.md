# Samples

Two sites to check TheBuilder.CacheTags end to end:

- `TheBuilder.CacheTags.Example`: an Umbraco 17 site (SQLite) that installs itself and seeds a small
  site on first start. It serves the Delivery API on http://localhost:5320.
- `nextjs`: a Next.js 16 frontend that renders that content from the Delivery API, caches each
  response under the keys in `X-Umbraco-Cache-Keys`, and purges them when the webhook calls it.

The package itself (`src/TheBuilder.CacheTags`) does not exist yet. The places to add it are marked
`CACHE TAGS` in the sample's csproj and `Program.cs`. Until it is added, Umbraco sends no cache
keys, so see [Before the package exists](#before-the-package-exists).

## Run Umbraco

Needs the .NET 10 SDK.

```bash
dotnet run --project samples/TheBuilder.CacheTags.Example
```

The first start creates `umbraco/Data/Umbraco.sqlite.db` and seeds the content. The backoffice is
http://localhost:5320/umbraco, user `local@example.invalid`, password `LocalOnly!42` (local only).

On a fresh database Umbraco fills its Delivery API index about a minute after the first start, so the
article listing is empty until then. This shows the articles once it is ready:

```bash
curl -s 'http://localhost:5320/umbraco/delivery/api/v2/content?fetch=children:/articles/'
```

To start again from nothing, stop the site and delete `samples/TheBuilder.CacheTags.Example/umbraco/Data`
and `samples/TheBuilder.CacheTags.Example/wwwroot/media`.

Settings worth knowing, all in `appsettings.json` under `Umbraco:CMS:DeliveryApi`:

- `OutputCache:Enabled` is `false`. To run with Umbraco's Delivery API output cache on:

  ```bash
  Umbraco__CMS__DeliveryApi__OutputCache__Enabled=true dotnet run --project samples/TheBuilder.CacheTags.Example
  ```

- `ApiKey` is `example-local-preview-key`, for preview requests (`Preview: true` and `Api-Key` headers).
- `RichTextOutputAsJson` is `false`, so rich text comes back as markup.

### What is seeded

Every key is fixed, so cache keys are the same on every machine.

| Item | Route | Key |
|---|---|---|
| Home (hero image, featured article, a block list of two teasers) | `/` | `e0000000-0000-0000-0000-000000000001` |
| Articles (listing) | `/articles/` | `e0000000-0000-0000-0000-000000000002` |
| Harbour mornings (picks the other two, links to Forest walks in its body) | `/articles/harbour-mornings/` | `e0000000-0000-0000-0000-000000000003` |
| Forest walks (picks Harbour mornings) | `/articles/forest-walks/` | `e0000000-0000-0000-0000-000000000004` |
| Evenings at dusk (picks the other two, links to Harbour mornings) | `/articles/evenings-at-dusk/` | `e0000000-0000-0000-0000-000000000005` |
| About, culture variant | `/about/` (en-US), `/om-os/` (da-DK) | `e0000000-0000-0000-0000-000000000006` |
| Images Harbour, Forest, Dusk | | `e1000000-0000-0000-0000-00000000000{1,2,3}` |

The home page's teasers link to the listing and the about page, so the block list's nested pickers
are covered. Dusk is used by both the home page and the third article.

## Run Next.js

Needs Node 22 or later and pnpm. In a second terminal:

```bash
cd samples/nextjs
```

```bash
pnpm install
```

```bash
pnpm build
```

```bash
pnpm start
```

Then open http://localhost:3000. The build does not need Umbraco: every page renders per request, from
cached Delivery API responses. `pnpm dev` works too, but check purges against `pnpm start`, which
caches the way production does.

Settings are in `samples/nextjs/.env` (local values only). Put your own in `.env.local`.

| Page | Delivery API calls |
|---|---|
| `/` | `content/item/` |
| `/articles` | `content/item/articles/`, `content?fetch=children:/articles/` |
| `/articles/<slug>` | `content/item/articles/<slug>/` |
| `/en-us/about`, `/da-dk/om-os` | `content/item/<slug>/` with `Accept-Language` |

Every call goes through `deliveryApi()` in `lib/umbraco.ts`. It caches the response with `'use cache'`,
tags it with each key in `X-Umbraco-Cache-Keys`, and keeps it for an hour at most as a safety net.

Each page ends with a Cache box: when each response was fetched from Umbraco, and the tags it got.
The time is set when the cache entry is filled, so a reload shows the same time until a purge (or
the hourly revalidate) refetches it.

## The purge webhook

`POST /api/cache-tags` (`samples/nextjs/app/api/cache-tags/route.ts`). This is the contract the package
has to send:

```http
POST /api/cache-tags
Content-Type: application/json
X-Cache-Tags-Timestamp: 1792612800
X-Cache-Tags-Signature: sha256=<lowercase hex>

{"id":"<event id>","keys":["c-e0000000-0000-0000-0000-000000000003","t-article"]}
```

- The signature is HMAC-SHA256, keyed with the secret's UTF-8 bytes, over
  `POST\n/api/cache-tags\n<timestamp>\n<raw body>`: method, path, timestamp and body, joined by
  newlines.
- The timestamp is Unix seconds and must be within 5 minutes of the frontend's clock.
- `CACHE_TAGS_WEBHOOK_SECRET` and `CACHE_TAGS_WEBHOOK_SECRET_PREVIOUS` are both accepted, so the secret
  can be rotated: set the new one as current and the old one as previous, switch Umbraco, then clear
  previous.
- An event id seen in the last few minutes is answered with `"duplicate": true` and not purged again.
  Ids are kept in memory, per process.
- `q-`, `c-` and `m-` keys are expired at once (`revalidateTag(key, { expire: 0 })`): the next request
  waits for fresh content. `t-` keys and `umbraco` are marked stale (`revalidateTag(key, 'max')`): the
  next request still gets the old page and refreshes it in the background. Other keys are returned
  under `ignored`.

`pnpm purge` sends a signed webhook, the same way:

```bash
pnpm purge c-e0000000-0000-0000-0000-000000000003
```

## Check it end to end

With the package added and both sites running:

1. Open http://localhost:3000/articles/harbour-mornings. The Cache box lists tags such as
   `c-e0000000-0000-0000-0000-000000000003`. Reload: the time stays the same.
2. In the backoffice, change the article's Summary and choose Save and publish.
3. Reload. The new summary shows, with a new time in the Cache box. The listing at
   http://localhost:3000/articles shows it too, since it renders the article.
4. The home page and Forest walks pick the article, so they get a new time as well.
   http://localhost:3000/en-us/about does not render it and keeps its time.
5. Publish only the Danish culture of Om os. http://localhost:3000/da-dk/om-os changes. Check whether
   http://localhost:3000/en-us/about kept its time: it should, if the package limits a purge to the
   cultures that were published.
6. Change the Harbour image's name in Media and save. The pages that show it refresh.

### Before the package exists

Umbraco sends no `X-Umbraco-Cache-Keys` yet, so the Cache box says `tags: none` and nothing can be
purged by key. A publish then shows up only after the hourly revalidate, or after restarting
`pnpm start` (the cache is in memory). That is how a frontend behaves without the package.
`pnpm purge` still exercises the webhook: it should answer `200` with the keys under `expired` or
`stale`.
