# Samples

Two sites to check TheBuilder.CacheTags end to end:

- `TheBuilder.CacheTags.Example`: an Umbraco 17 site (SQLite) that installs itself and seeds a small
  site on first start. It serves the Delivery API on http://localhost:5320.
- `nextjs`: a Next.js 16 frontend that renders that content from the Delivery API, caches each
  response under the keys in `X-Umbraco-Cache-Keys`, and purges them when the webhook calls it.

The package itself (`src/TheBuilder.CacheTags`) does not exist yet. The places to add it are marked
`CACHE TAGS` in the sample's csproj and `Program.cs`. Until it is added, Umbraco sends no cache
keys, so see [Before the package exists](#before-the-package-exists).

`pnpm e2e` checks the whole loop automatically: see [Check it automatically](#check-it-automatically).

## Run Umbraco

Needs the .NET 10 SDK.

```bash
dotnet run --project samples/TheBuilder.CacheTags.Example
```

The first start creates `umbraco/Data/Umbraco.sqlite.db` and seeds the content. The backoffice is
http://localhost:5320/umbraco, user `local@example.invalid`, password `LocalOnly!42` (local only).

Every start also makes sure there is an API user for scripts (`Seeding/ExampleApiUserSeeder.cs`), so
a database seeded before it existed gets it too. It is in the Administrators group and signs in to
the Management API with client credentials, client id `umbraco-back-office-cache-tags-e2e` and
secret `example-local-e2e-client-secret` (local only, also in `nextjs/.env`):

```bash
curl -s http://localhost:5320/umbraco/management/api/v1/security/back-office/token -d 'grant_type=client_credentials&client_id=umbraco-back-office-cache-tags-e2e&client_secret=example-local-e2e-client-secret'
```

`Umbraco:CMS:Global:UseHttps` is `false`, so the token endpoint answers over plain HTTP on localhost.

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

Needs Node 22.18 or later (it runs the TypeScript scripts in `scripts/` directly) and pnpm. In a second
terminal:

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
  waits for fresh content. `t-`, `ch-` and `s-` keys and `umbraco` are marked stale
  (`revalidateTag(key, 'max')`): the next request still gets the old page and refreshes it in the
  background. Other keys are returned under `ignored`.

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

### Check it automatically

`samples/nextjs/scripts/e2e.mts` runs the steps above through the Management API. With Umbraco running
and the Next.js site built and served with `pnpm start` (not `pnpm dev`, which caches differently):

```bash
cd samples/nextjs
```

```bash
pnpm e2e
```

It reads each page's Cache box, publishes a change as the seeded API user, and waits for the pages
to refresh:

1. Harbour mornings' summary changes. The article and `/articles` must show it, `/` and Forest walks
   must refetch their response, and `/en-us/about` must keep its cache.
2. A new article is published under Articles. `/articles` must list it. The listing comes from the
   Delivery API index, so this case waits twice as long. Right after Umbraco starts, the index can
   take more than half a minute to pick up a publish, so give Umbraco a minute before the first run.
3. Only da-DK of the about page is published. `/da-dk/om-os` must show it. Whether `/en-us/about` kept
   its cache is reported, not judged.
4. The Harbour image is renamed. The article and `/articles` must show the new name (it is the
   image's alt text).

Each case prints `PASS` or `FAIL` with a line per check, and for a failed check every response on the
page with when it was fetched and its tags. The script exits 1 when a case fails. Each case undoes its
change when it ends, and a run first cleans up after one that was stopped halfway, so it can run
again and again against the same database. Before a change, each case waits until the pages it checks
are cached and steady, so a page that refetches only because its cache entry was empty cannot pass.

`E2E_TIMEOUT` sets how many seconds a case waits (default 30). `UMBRACO_URL`, `FRONTEND_URL`,
`UMBRACO_CLIENT_ID` and `UMBRACO_CLIENT_SECRET` come from `.env`, and can be overridden in the
environment or `.env.local`.

Until the package is added every case fails, with `tags: none` under each page: Umbraco sends no
cache keys and no purge.

### Check the packed package

To check the `.nupkg` that gets published rather than the project, pack it into `artifacts/` at the
repository root with a version of its own, and build the sample with that version:

```bash
dotnet pack src/TheBuilder.CacheTags -o artifacts -p:Version=0.0.0-local.1
```

```bash
dotnet run --project samples/TheBuilder.CacheTags.Example -p:CacheTagsPackageVersion=0.0.0-local.1
```

`CacheTagsPackageVersion` swaps the project reference for a package reference, restored from
`artifacts/` (or the folder in `CacheTagsPackageSource`). NuGet keeps every version it restores in its
global packages folder and does not look at the file again, so give each pack a new version. Then run
`pnpm e2e` as above.

### Before the package exists

Umbraco sends no `X-Umbraco-Cache-Keys` yet, so the Cache box says `tags: none` and nothing can be
purged by key. A publish then shows up only after the hourly revalidate, or after restarting
`pnpm start` (the cache is in memory). That is how a frontend behaves without the package.
`pnpm purge` still exercises the webhook: it should answer `200` with the keys under `expired` or
`stale`.
