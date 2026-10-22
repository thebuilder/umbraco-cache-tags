# Samples

Two sites to check TheBuilder.CacheTags end to end:

- `TheBuilder.CacheTags.Example`: an Umbraco 17 site (SQLite) that installs itself and seeds a site
  with nested sections, blocks, media folders and culture-variant pages on first start. It serves the
  Delivery API on http://localhost:5320.
- `nextjs`: a Next.js 16 frontend that renders that content from the Delivery API, caches each
  response under the keys in `X-Umbraco-Cache-Keys`, and purges them when the webhook calls it.

The Umbraco sample references the package (`src/TheBuilder.CacheTags`), which installs itself through
its composer. It tags every Delivery API response, and once a change has reached every server it sends
the signed purge webhook to the Next.js site (`CacheTags:Targets` in `appsettings.json`, with the
secret from `samples/nextjs/.env`).

`pnpm e2e` checks the whole loop automatically: see [Check it automatically](#check-it-automatically).

## Run Umbraco

Needs the .NET 10 SDK.

```bash
dotnet run --project samples/TheBuilder.CacheTags.Example
```

The first start creates `umbraco/Data/Umbraco.sqlite.db` and seeds the content. Every start also adds
any seeded item that is missing (checked by key), so a database seeded by an older version of the
sample gets the newer content without being deleted. The backoffice is
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

Every key is fixed, so cache keys are the same on every machine. Documents are `e0000000-...`, media
`e1000000-...`, blocks `e2000000-...`, content types `e3000000-...` and data types `e5000000-...`;
the full list is in `Seeding/ExampleContent.cs`. `Seeding/ExampleSeeder.cs` seeds the first part
and `Seeding/ExampleSiteSeeder.cs` the rest.

Documents (key `e0000000-0000-0000-0000-<last part>`):

| Item | Route | Last part of key |
|---|---|---|
| Home (hero image, featured article, a block list of two teasers) | `/` | `000000000001` |
| Articles (listing) | `/articles/` | `000000000002` |
| Harbour mornings (picks the other two, links to Forest walks in its body) | `/articles/harbour-mornings/` | `000000000003` |
| Forest walks (picks Harbour mornings) | `/articles/forest-walks/` | `000000000004` |
| Evenings at dusk (picks the other two, links to Harbour mornings) | `/articles/evenings-at-dusk/` | `000000000005` |
| About, culture variant | `/about/` (en-US), `/om-os/` (da-DK) | `000000000006` |
| Guides (section: image, picks By ferry and Breakfast) | `/guides/` | `000000000010` |
| Getting around (category, picks By ferry) | `/guides/getting-around/` | `000000000011` |
| By bike (links to Bike parking in its body) | `/guides/getting-around/by-bike/` | `000000000012` |
| Bike parking, a guide under a guide, five levels from the root | `/guides/getting-around/by-bike/bike-parking/` | `000000000013` |
| By ferry (rich text with a block and a PDF link, attachments, links of every kind, picks Harbour mornings) | `/guides/getting-around/by-ferry/` | `000000000014` |
| On foot (the only link to Hidden courtyard, in its body) | `/guides/getting-around/on-foot/` | `000000000015` |
| Eating out (category, picks Street food and Late dinners) | `/guides/eating-out/` | `000000000016` |
| Breakfast by the water | `/guides/eating-out/breakfast-by-the-water/` | `000000000017` |
| Late dinners (picks Street food) | `/guides/eating-out/late-dinners/` | `000000000018` |
| Street food, **saved but never published** | `/guides/eating-out/street-food/` | `000000000019` |
| Hidden courtyard, reachable only through On foot's rich text | `/hidden-courtyard/` | `000000000020` |
| Blocks (block grid with nested areas, block list with a block list inside, links) | `/blocks/` | `000000000021` |
| Photo essay (gallery with crops, a PDF, inline image) | `/photo-essay/` | `000000000022` |
| Events (invariant listing) | `/events/` | `000000000023` |
| Midsummer night / Sankthans, culture variant | `/events/midsummer-night/`, `/events/sankthans/` | `000000000024` |
| Harbour festival / Havnefest, culture variant | `/events/harbour-festival/`, `/events/havnefest/` | `000000000025` |
| Winter market, **published in en-US only** | `/events/winter-market/` | `000000000026` |

Media (key `e1000000-0000-0000-0000-<last part>`):

| Item | Path | Last part of key |
|---|---|---|
| Images Harbour, Forest, Dusk | `/harbour/`, `/forest/`, `/dusk/` | `000000000001` to `000000000003` |
| Folders Photos, Photos/City, Photos/City/Night, Photos/Nature, Documents | `/photos/`, `/photos/city/`, ... | `000000000010` to `000000000014` |
| Images Bridge, Market, Rooftops (City), Lanterns (Night), Lake, Meadow (Nature) | `/photos/city/bridge/`, ... | `000000000020` to `000000000025` |
| Image Courtyard, never picked, only inline in rich text | `/photos/courtyard/` | `000000000026` |
| Ferry timetable, a PDF | `/documents/ferry-timetable/` | `000000000030` |

The cases worth checking a purge against, and where they are:

- **Many pickers point at one item.** By ferry is picked by Guides, Getting around, Bike parking,
  Breakfast and the Harbour festival event, by two blocks and a link on the Blocks page, and linked
  from the Photo essay's rich text. Bridge is used by Guides, By ferry, the Blocks page, the Photo essay and an event.
  Harbour (from the first seed) is also picked by Breakfast, By ferry and an event.
- **Routes that depend on a parent.** Renaming Guides or Getting around changes the route of every
  page under it, down to Bike parking, and the breadcrumbs (`fetch=ancestors:`) on each.
- **Blocks.** The Blocks page has a block grid with a row inside a row's area, a block list inside a
  block, and a callout and a nested teaser that link back to the page itself. By ferry's rich text has
  a callout block that picks Breakfast. Home's teasers come from the first seed.
- **Unpublished pick.** Street food is picked by Eating out, Late dinners and a nested teaser on the
  Blocks page. The Delivery API leaves it out (or returns `null`) until it is published, and the
  responses that picked it should then refresh.
- **Only rich text reaches it.** Hidden courtyard is linked only from On foot's body; Courtyard is
  only an inline image (Hidden courtyard and the Photo essay).
- **Media.** Nested folders, crops set per pick on the Photo essay and the gallery block, a PDF picked
  as an attachment and linked from rich text and a multi URL picker, and a folder listing through the
  Media Delivery API.
- **Culture variants.** Events have a culture-variant title and description but a shared date, image
  and venue, so changing a shared value changes both cultures. Winter market has no da-DK version.
- **Listings.** Children (`fetch=children:/guides/`, `/events/`), one content type
  (`filter=contentType:guidePage` under Guides), a sort that only `updateDate` decides
  (`sort=updateDate:desc&take=3`: editing any guide can move it in), and ancestors.
- **Property editors.** Block list, block grid, rich text with blocks, links and images, multi URL
  picker (document with a query string, media, external), multinode tree picker, content picker,
  media picker with crops, tags, dropdown, date.

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
| `/guides` | `content/item/guides/`, `content?fetch=children:/guides/`, `content?fetch=descendants:/guides/&filter=contentType:guidePage`, `content?fetch=descendants:/guides/&sort=updateDate:desc&take=3` |
| `/guides/<path>` (category or guide, any depth) | `content/item/guides/<path>/`, `content?fetch=ancestors:/guides/<path>/`, `content?fetch=children:/guides/<path>/` |
| `/hidden-courtyard` | `content/item/hidden-courtyard/` |
| `/blocks` | `content/item/blocks/` |
| `/photo-essay` | `content/item/photo-essay/`, `media?fetch=children:/photos/city/` |
| `/en-us/events`, `/da-dk/events` | `content/item/events/`, `content?fetch=children:/events/`, with `Accept-Language` |
| `/en-us/events/<slug>`, `/da-dk/events/<slug>` | `content/item/events/<slug>/` with `Accept-Language` |

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

{"version":1,"id":"<purge id>","changeId":"<change id>","expire":["response-3f9a0c...","content-e0000000-0000-0000-0000-000000000003"],"stale":["type-article"]}
```

- `expire` keys are purged at once; `stale` keys are served stale while they refresh. A body of
  another version, or over 1 MB, is refused.

- The signature is HMAC-SHA256, keyed with the secret's UTF-8 bytes, over
  `POST\n/api/cache-tags\n<timestamp>\n<raw body>`: method, path, timestamp and body, joined by
  newlines.
- The timestamp is Unix seconds and must be within 5 minutes of the frontend's clock.
- `CACHE_TAGS_WEBHOOK_SECRET` and `CACHE_TAGS_WEBHOOK_SECRET_PREVIOUS` are both accepted, so the secret
  can be rotated: set the new one as current and the old one as previous, switch Umbraco, then clear
  previous.
- A purge id seen in the last few minutes is answered with `"duplicate": true` and not purged again.
  Ids are kept in memory, per process.
- `response-`, `content-` and `media-` keys are expired at once (`revalidateTag(key, { expire: 0 })`):
  the next request waits for fresh content. `type-`, `children-` and `search-` keys and `umbraco` are
  marked stale (`revalidateTag(key, 'max')`): the next request still gets the old page and refreshes
  it in the background. Other keys are returned under `ignored`. The key names are defined in
  `docs/design.md`.

`pnpm purge` sends a signed webhook, the same way:

```bash
pnpm purge response-<the key in a page's Cache box>
```

## Check it end to end

With the package added and both sites running:

1. Open http://localhost:3000/articles/harbour-mornings. The Cache box lists one `response-` tag per
   Delivery API call. Reload: the time stays the same.
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
5. The footer's dictionary item changes. `/articles` and `/en-us/about` must show it (see
   [The site's own API](#the-sites-own-api-the-dictionary)).
6. Evenings at dusk is unpublished. Its page must be not found, and `/articles` and Harbour mornings
   (which picks it as related) must stop showing it. A page whose shell is prerendered has already
   answered 200 when it finds the item gone, so "not found" also means Next.js streamed its 404.
7. The articles are sorted in reverse. `/articles` (sorted by sort order) must list them in the new
   order. Like case 2, it waits for the Delivery API index.

CI runs the same suite on every pull request (the `e2e` job in `.github/workflows/verify.yml`), on a
fresh database, after waiting for the index.

Each case prints `PASS` or `FAIL` with a line per check, and for a failed check every response on the
page with when it was fetched and its tags. The script exits 1 when a case fails. Each case undoes its
change when it ends, and a run first cleans up after one that was stopped halfway, so it can run
again and again against the same database. Before a change, each case waits until the pages it checks
are cached and steady, so a page that refetches only because its cache entry was empty cannot pass.

**Known issue:** on SQLite, the sample could lock up: requests and Umbraco's own background jobs
waited on a database lock until one of them timed out, sometimes failing the first start's seeding.
One cause was the package: after a publish, its delivery worker read the changed items through
`IContentService` while a save was still writing. Umbraco connects to SQLite with a shared cache,
where a read transaction keeps each table it has read locked until it ends, so the read and the save
each waited for a table the other held. The worker now takes the package's write lock before it
reads (`PackageDatabase.ReadUmbraco`). An earlier report had the lock-up with the package switched
off too, so Umbraco on SQLite under this load may have another cause: if it happens, restart Umbraco
between longer series of runs, or run the sample on SQL Server.

`E2E_TIMEOUT` sets how many seconds a case waits (default 30). `UMBRACO_URL`, `FRONTEND_URL`,
`UMBRACO_CLIENT_ID` and `UMBRACO_CLIENT_SECRET` come from `.env`, and can be overridden in the
environment or `.env.local`.

A run needs both sites started fresh enough that Umbraco's Delivery API index is filled (about a
minute after a first start).

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

### What the package logged

Each change is logged when it is planned and when it is released:
`Cache tags change <id> (Published) plans content-..., type-...` and
`Cache tags change <id> released: 10 keys in 2 purges.` A change that reaches listings is purged a
second time once every server's Delivery API index has it:
`Cache tags change <id> indexed: 6 listing keys purged.`

The admin-only lookup API answers what a key covers and what recent changes planned, sent and to whom:
`GET /umbraco/management/api/v1/cache-tags/lookup?key=<key>` and
`GET /umbraco/management/api/v1/cache-tags/changes`, with a backoffice or API user token (the seeded
API user works; see `scripts/e2e.mts` for getting a token).

`pnpm purge` exercises the webhook by hand: it should answer `200` with the keys under `expired` or
`stale`.

Right after Umbraco starts, its Delivery API index can take a minute or more to catch up, and a
change's listing purge waits for it, so run the checks with a longer timeout then:
`E2E_TIMEOUT=120 pnpm e2e`.

## Search

The Umbraco sample adds full-text search to the Delivery API (`TheBuilder.CacheTags.Example/Search`):
an index handler puts each item's published text (name, text fields, rich text without markup) into
the Delivery API content index as analyzed text, and a filter queries it. Each `search:` filter
matches words starting with any of its words; send one per word to require all of them:

```http
GET /umbraco/delivery/api/v2/content?filter=search:harbour&filter=search:morning
```

The Delivery API index is an Examine (Lucene) index, so this needs no second index. Umbraco does not
reindex existing items when a handler adds a field: after adding one to a site with content, rebuild
the Delivery API index once (Settings > Examine Management, or
`POST /umbraco/management/api/v1/indexer/DeliveryApiContentIndex/rebuild`).

A search result is a Delivery API listing, so the package tags it and purges it when a publish could
change it, once every server's index has the change. Searches come from user input, so the package
does not record them: each carries its own keys (its items, `search-deliveryapicontentindex` and
`umbraco`), and the one `search-` key purges every cached search at once. The Next.js page (`app/search/page.tsx`) normalises the words so the same search is one cache
entry. Searching Examine directly from a custom endpoint gives relevance ranking, but those responses
are not Delivery API responses: the package does not tag them, and nothing purges them.

## The site's own API: the dictionary

The Umbraco sample has an API of its own, outside the Delivery API: `GET /api/dictionary?locale=en-US`
returns every dictionary item in that language (`TheBuilder.CacheTags.Example/Dictionary`). It shows
how a site's own output joins in:

- `[CacheTagged(QueryParameters = ["locale"])]` tags its responses, keyed by `locale` only, and the
  action names `x-dictionary-<locale>` with `ICacheTagsResponse`.
- Saving or deleting a dictionary item records a change for every language's key with
  `ICacheTagsChanges.Record(..., "dictionary")`, and every server reports `Applied("dictionary")` when
  its dictionary cache is refreshed, so the purge waits for all of them.

The Next.js footer shows `Footer.Tagline` from it (seeded by `Dictionary/DictionarySeeder.cs`), and e2e
case 5 edits it.

## Check it on two servers

`two-servers/run.sh` runs the Umbraco sample as two servers on one SQL Server database, the way a
load-balanced site runs: A on http://localhost:5320 and B on http://localhost:5321. It needs Docker
(SQL Server 2022 Developer edition, in `two-servers/compose.yaml`, sa password `LocalOnly-Sql-42!`,
local only) and the .NET 10 SDK.

```bash
samples/two-servers/run.sh
```

It starts SQL Server on port 14330, publishes the sample twice into `two-servers/out/` (each server
needs its own content root for its Examine index and MainDom), starts A, which installs and seeds the
database, then B. Wait for A's article listing to fill (about a minute), then, with the Next.js site
running against A (the default `UMBRACO_URL`), make every change on B:

```bash
cd samples/nextjs && UMBRACO_URL=http://localhost:5321 pnpm e2e
```

A purge must now wait for A: B records each change and acknowledges it from its own cache refresh at
once, A applies the change when it next reads Umbraco's cache instructions (every few seconds), and
only then is the purge released. The cases take 3 to 8 seconds instead of 1. Without the wait, the
frontend would refetch from A before A had the change and cache the old content again.

The ledger shows it per change (`theBuilderCacheTagsEvent`, `theBuilderCacheTagsAck`):

```bash
docker compose -f samples/two-servers/compose.yaml exec sql /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P 'LocalOnly-Sql-42!' -d CacheTags -Q "SELECT e.Kind, e.CreatedUtc, a.Server, a.AckedUtc, e.ReleasedUtc FROM theBuilderCacheTagsEvent e JOIN theBuilderCacheTagsAck a ON a.EventId = e.Id ORDER BY e.CreatedUtc DESC, a.AckedUtc"
```

`samples/two-servers/run.sh stop` stops both servers and removes the SQL Server container, so the
next run starts from an empty database.
