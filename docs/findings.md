# Milestone 1 spike: findings

Run on 2026-10-21 against Umbraco 17.7.1 (source read at tag `release-17.7.1`), SQLite, one server.
The probe was a throwaway Umbraco site with a Python driver, run against three site configurations.
It has since been removed; the probe, the driver and the raw results are in the git history, under
`spike/`, before the commit that removed them.
Four independent reviews checked this document: the Umbraco claims against source, the probe for
measurement bugs, the design consequences, and the repository conventions. Their corrections are
folded in.

## Short answers

| Question | Answer | Design impact |
|---|---|---|
| Does a per-request collector see every rendered item? | **Not if it hooks the builders.** Umbraco caches converted Delivery API property values. The builders run on the first request after an item changes; later requests skip most of them. | Collect from the published item's references (Umbraco's reference tracking) plus a walk of the response objects, in an MVC result filter. The mapping is learnt on render only. |
| Umbraco 17 output cache tag names | `umb-dapi-content-{key}`, `umb-dapi-media-{key}`, `umb-dapi-content-ancestor-{key}`, `umb-dapi-content-type-{alias}`, `umb-dapi-content-all`, `umb-dapi-media-all`, `umb-dapi-all`. Added in **17.4.0**. | The floor is 17.4.0 (Decision 1). |
| How does `umbracoServer` report active servers? | Only servers using the default `ElectedServerRoleAccessor` touch it. Umbraco's docs recommend explicit roles, and explicit subscribers can run on a **read-only database**. | The barrier needs its own registry; delivery servers must be able to write (Decision 2). |
| Published-culture filter on refresher payloads | Present on publish and unpublish only: `["*"]` for invariant, the culture(s) for variant. `null` for draft saves, sorts, moves and trashing. | Drive the ledger from source-side notifications, not refresher payloads. |

## 1. The per-request collector

### What was tested

A fixture page (`/fixture/`) renders a content picker (A), a tree picker (B, C), a URL picker (D,
image 2), a media picker (image 1), a block list whose block has a content picker (E), and rich text
with a document link (F), a media link (image 3) and an inline image (image 4). Expected keys: the
page, A to F, images 1 to 4. Expected per request otherwise:

- `ids` (page and A) and `expand=properties[picker]` add G, A's own picker.
- `query` lists every page; only the fixture page's keys are checked.
- A culture-variant document picks B in `en-US` and C in `da-DK`, so `Accept-Language: da-DK`
  expects {Variant, C} and `en-US` expects {Variant, B}.

Each request ran **cold** (the page was republished just before it, which replaces the published item
and drops the values cached on it), **warm** (again, without changes), and after publishing A,
publishing F and saving image 1. Endpoints: by route, by id, by ids, query, expanded, preview,
media by id, the variant per culture.

Strategies, side by side:

| Strategy | How |
|---|---|
| `render` | Decorators on every Delivery API builder: `IApiContentBuilder`, `IApiContentResponseBuilder`, `IApiContentRouteBuilder`, `IApiContentNameProvider`, `IApiMediaUrlProvider`, `IApiMediaBuilder`, `IApiMediaWithCropsBuilder`, `IApiMediaWithCropsResponseBuilder`, `IApiElementBuilder` |
| `objects` | Reflection walk of the `ObjectResult` value in an MVC result filter |
| `json` | Walk of the serialized response body in middleware |
| `refs` | `DataValueReferenceFactoryCollection.GetReferences` over each resolved item's published source values, every culture |
| `refsCulture` | The same, reading a culture-variant property in the request culture only |
| `resolved`, `refs2` | The controller's top-level items; references of referenced items (depth 2) |

The review confirmed the decorators wrap the instances Umbraco uses: all nine builders are
singletons (`UmbracoBuilder.CoreServices.cs:499-518`), and the cold rows show every path reaching them.

### Results (markup rich text, output cache off)

Cells show keys missing from the expected set, then `+n` extra keys where the expected set is exact.

| request | render | objects | json | refs | refsCulture |
|---|---|---|---|---|---|
| route, cold | all | missing 1/11 | missing 1/11 | all | all |
| id, warm | missing 8/11 | missing 1/11 | missing 1/11 | all | all |
| ids, warm | missing 7/12 | missing 1/12 | missing 1/12 | all | all |
| query, warm | missing 5/11 | missing 1/11 | missing 1/11 | all | all |
| expanded, warm | missing 8/12 | missing 1/12 | missing 1/12 | missing 1/12 (in `refs2`) | missing 1/12 (in `refs2`) |
| variant da-DK, cold | all | all | all | all, +1 | all |
| variant en-US, warm | missing 1/2 | all | all | all, +1 | all |
| route, after publishing A | missing 1/11 | missing 1/11 | missing 1/11 | all | all |

Every cold request was complete for `render`. Rich text as JSON (`RichTextOutputAsJson: true`,
confirmed by the `#root` element in the body) gave the same table. Full tables:
The raw results were `results/markup-nocache.md`, `results/json-nocache.md` and `results/markup-outputcache.md`, in the git history.

### Why `render` fails

`PublishedProperty.GetDeliveryApiValue` caches the converted value at the property type's
`DeliveryApiCacheLevel` (`HybridCache/PublishedProperty.cs:185-240`):

- `Element` caches on the published item itself, until that item is refreshed. The block list uses it
  (`BlockListPropertyValueConverter.cs:131`), which is why E stayed hidden even after A, F and image 1
  were published: the page's own item was not replaced.
- `Elements` caches in the elements cache, which is cleared on **every** content or media refresher
  pass, draft saves and sorts included (`ContentCacheRefresher.cs:165`, `MediaCacheRefresher.cs:141`).
  Content picker, URL picker, media picker and rich text use it.
- `Snapshot` is treated as no cache. The tree picker uses it, so B and C rendered every time.

A builder-decorator collector passes a one-shot test and silently loses most keys in production.

### What does work

- **References are complete** on every request for this fixture, cold and warm, in both rich text
  modes. They read the published source values, which are never cached away, through the reference
  factories that drive Umbraco's relations, so block lists and rich text links and images are covered.
- **Read the request culture.** `refs` added one wrong-culture key to each variant response;
  `refsCulture` was exact. The request culture is `IVariationContextAccessor.VariationContext.Culture`.
- **Expansion needs depth 2**, and only for the expanded properties: G came from `refs2` alone.
- **`objects` and `json` agree exactly** and miss only the inline rich text image: both rich text
  parsers drop `data-udi` and emit only `src` (`ApiRichTextElementParser.cs:202`,
  `ApiRichTextMarkupParser.cs:134`). Links keep `destinationId` / `data-destination-id`.
- **Block element keys are not documents.** The walkers report the block's key; it must not become a
  `c-` key.
- **References over-approximate, which is safe.** A trashed item that a picker still points at stays
  in the set. A spare key costs one extra purge; a missing key leaves a cache stale.

### Recommendation

Collect in an MVC result filter on Delivery API controllers (`IAsyncResultFilter`), and union:

1. the resolved top-level items,
2. their references in the request culture, plus depth 2 for the properties the request expanded,
3. a walk of the result objects limited to known types (`IApiContent`, `IApiMedia`, `ApiLink`, rich
   text link attributes, block items, paged results). This catches custom value converters that
   emit content or media but have no reference factory.

Do not buffer the response body: `json` adds nothing over `objects`. Skip element keys.

Known gaps, for the docs: a value converter that queries content itself ("latest three articles") is
invisible to references and only partly visible to the walk. Those are listings, and belong to
selector keys and the second purge.

### Routes depend on ancestors (observed)

A child of C was requested, then C was renamed (which changes the child's route). The payload was a
single `RefreshNode` for C. With output cache on, the next request for the child was served from the
cache with the **old** route (`/target-c/child-of-c/` while the real route was
`/target-c-.../child-of-c/`). Umbraco evicts ancestor tags only on `RefreshBranch`. Any response that
shows a descendant's route goes stale the same way. See Decision 3.

## 2. Umbraco's Delivery API output cache

- **Tags** are listed above (`Constants-DeliveryApi.cs:39-69`). Extension points:
  `IDeliveryApiOutputCacheTagProvider`, `IDeliveryApiOutputCacheEvictionProvider`,
  `IDeliveryApiOutputCacheRequestFilter`, `IDeliveryApiOutputCacheVaryByProvider` and
  `IDeliveryApiOutputCacheManager.EvictByTagAsync`.
- **Version:** these types are absent from 17.0 to 17.3.x and present from 17.4.0, as is the
  controller's stash of resolved items. That stash key is `internal`
  (`"Umbraco.DeliveryApi.OutputCache.ResolvedContentItems"`); copy it, and fall back to resolving
  items ourselves if it is missing.
- **Registered only when output caching is on** (`Enabled` and a duration above zero): the eviction
  handlers, `IDeliveryApiOutputCacheManager` and `IDeliveryApiOutputCacheRequestFilter`. Inject them
  optionally, or startup fails on sites with output caching off.
- **Tag providers** are resolved per response from `HttpContext.RequestServices` and called only when
  the resolved items are non-empty, so an empty listing gets no extra tag.
- **Umbraco caches only public responses.** `IsCacheable` is `!IsPreview && HasPublicAccess`, so a
  Delivery API behind an API key gets no output caching at all.
- **Vary:** content varies by `Accept-Language`, `Accept-Segment` and `Start-Item`, media by
  `Start-Item` only. The observed header was `Vary: Accept-Language, Accept-Segment, Preview, Start-Item`.
- **What Umbraco evicts:** the item's own tag; one hop of references through relations
  (`umbDocument`, `umbMedia`), which are rewritten on save, publish and unpublish from both draft and
  published values (`ContentRelationsUpdate.cs:51-61, 96`); ancestor tags on `RefreshBranch` only.
  Nothing evicts listings when a new item is published. In the output-cache run, publishing A and
  saving image 1 both evicted the page.
- **Order of handlers:** async handlers run one after another in DI registration order, after the
  sync handlers (`EventAggregator.Notifications.cs:106-116`). Observed for
  `ContentCacheRefresherNotification`: `DeliveryApiDocumentOutputCacheEvictionHandler`,
  `PreviewHubUpdater`, then the probe. That holds only because `AddDeliveryApi()` comes before
  `AddComposers()`; the ack handler should evict what it needs itself rather than rely on the order.
- **Headers survive an output cache hit.** A header set during the first render was replayed unchanged
  on later requests, and the controller did not run. So `X-Umbraco-Cache-Keys` works with Umbraco's
  output cache, but nothing of ours runs on a hit (see the TTL gap below).
- **Preview** was never output-cached in these runs. `IRequestPreviewService.IsPreview()` was true with
  `Preview: true` and the API key, and no response carried `Cache-Control`, so `no-store` on preview
  is ours to add.

## 3. Delivery servers and the refresh pipeline

- `TouchServerJob.RunJobAsync` returns at once unless `IServerRoleAccessor is ElectedServerRoleAccessor`
  (`TouchServerJob.cs:83`), so with explicit roles a server never appears in `umbracoServer`.
- Umbraco's load balancing docs ("Flexible load balancing, advanced") recommend an explicit
  SchedulingPublisher, and say an explicit subscriber does not touch `umbracoServer`, which is what
  lets front-end servers run with **read-only database access**.
- **The barrier's core assumption is backed by code.** On every server the refresher runs
  `RefreshInternal` then `Refresh` (`CacheInstructionService.cs:554-555`). `ContentCacheRefresher.Refresh`
  updates navigation, publish status, the HybridCache memory cache, the elements cache and routing,
  and only then raises the notification (`ContentCacheRefresher.cs:192-235`, `CacheRefresherBase.cs:104`).
  A handler on that notification therefore runs after the server serves the new content. It still
  needs a two-process test in Milestone 3.
- **Exception: the Delivery API index.** Its update is queued to a background task
  (`DeliveryApiContentIndexHandleContentChanges.cs:50`), so an ack never covers listings. The second
  purge is best effort, and the docs must say so. On the first boot of the spike the index stayed
  empty for about a minute, until Umbraco's delayed startup rebuild (`RebuildOnStartupHandler.cs:70`).
- **HybridCache second level:** Umbraco registers no `IDistributedCache`, so there is a second level
  only if the host adds one. The converted-item and elements caches are always per server.
- **Subscribers poll** for instructions every 5 seconds by default (`TimeBetweenSyncOperations`), up to
  1000 at a time. A **cold boot** skips the queued instructions, so no per-event notifications fire on
  that server: it saves the newest instruction id as synced and rebuilds instead
  (`DatabaseServerMessenger.cs:334-346`). The barrier needs its timeout and must treat a cold-booted
  server as caught up.

## 4. Notifications and the ledger

Observed per operation on the source server (a single server sees the same refresher payloads):

| Operation | Refresher payload(s) | Source-side notifications |
|---|---|---|
| Draft save | `RefreshNode`, cultures `null` | `ContentSaved`, `ContentTreeChange` (`null`) |
| Publish, invariant | two: `RefreshNode` `null` (the save), then `RefreshNode` `PublishedCultures ["*"]` | `ContentSaved`, two `ContentTreeChange`, `ContentPublished` with `PublishedCultures {key: ["*"]}` |
| Publish one culture | second payload `PublishedCultures ["da-DK"]` | `ContentPublished` with `PublishedCultures {key: ["da-DK"]}` |
| Unpublish, invariant | `RefreshBranch`, `UnpublishedCultures ["*"]` | `ContentTreeChange`, `ContentUnpublished` with `UnpublishedCultures {key: ["*"]}` |
| Unpublish one culture | `RefreshNode`, `UnpublishedCultures ["da-DK"]` | `ContentTreeChange`, and `ContentPublished` with an empty published map and `UnpublishedCultures {key: ["da-DK"]}` |
| Rename (URL segment) | `RefreshNode` for the item only | as publish |
| Move | `RefreshBranch`, `null` | `ContentTreeChange`, `ContentMoved` (with the original path); no publish |
| Sort | `RefreshNode` per child, `null` | `ContentSorted`, `ContentSaved` for every child, `ContentPublished` for each published child whose sort order changed (from source; the run reversed the order, so every child changed) |
| Move to recycle bin | `RefreshBranch`, `null` | `ContentTreeChange`, `ContentMovedToRecycleBin` (with the original path) |
| Media save | `RefreshNode` (media refresher) | |

Consequences:

- A draft save and a sort look the same in a refresher payload, so "ignore draft-only saves" cannot be
  decided on the delivery servers. The ledger belongs on the source server, as planned.
- **Cultures:** the per-key maps on `ContentPublishedNotification` and `ContentUnpublishedNotification`
  are exactly what the operation changed, but they exist only in 17.6.0+ and 18.1.0+.
  `IContent.PublishedCultures` is every culture the item has published, not what changed. On 17.4 and
  17.5, read cultures from `ContentTreeChangeNotification` (`null` means all).
- **Sort** re-publishes the children whose order changed. Collapse it into one `ch-<parent>` event
  (`ContentSortedNotification`), dropping the per-child publishes in the same scope.
- **Moves and trashing** need `ContentMovedNotification` and `ContentMovedToRecycleBinNotification`
  for the old parent.
- A publish produces two refresher payloads for one change. Acks should match by ledger event, not by
  counting payloads.

## 5. Design gaps the spike surfaced

- **Mappings must outlive frontend caches.** A Next.js `revalidateTag` entry does not expire by itself.
  If a mapping row ages out while the frontend still holds the response, that response is stale for
  good and nothing reports it. Refreshing the TTL "when served" must therefore run in middleware in
  front of Umbraco's output cache, reading the replayed header. Send an `s-maxage` no longer than the
  mapping TTL, and document it.
- **Writes must be additive.** If each render replaced a q-key's rows, two servers rendering the same
  q-key could race, and an older render could win. Write (q, item, last seen) rows additively and prune
  by age; when a response shrinks, the old rows linger until they age out, which only over-purges.
- **Render-then-publish race.** A render reads old content, a publish and its purge complete, and only
  then is the response cached and the mapping written. That entry is never purged. Note the last acked
  ledger event when the request starts; if an event touching the collected items was acked during the
  render, send `no-store`.
- **Listings need selector rows.** A newly published item is in no existing mapping. For query
  requests, parse `fetch` and `filter` and store selector rows (`ch-<parent>`, `t-<alias>`, otherwise
  `s-<index>`) against the query's q-key. A publish then purges by selector.
- **Write load and key count.** A cold render writes tens of rows. Batch writes through a background
  channel with an in-memory "seen recently" filter. Normalize the query string before hashing (order,
  defaults), and cap rows per q-key with overflow to selector keys.
- **Authenticated responses** (member tokens, protected content) vary by `Authorization`, which the
  q-key does not include. Send no keys and `private` on those.
- **Segments:** Umbraco's output cache already varies by `Accept-Segment`, so hash it into the q-key.
- **Media:** replacing a file usually creates a new path, so purging by URL matters mainly for deleting
  or trashing media (takedown). Check Cloudflare's per-plan rate limits and tags per call for chunking.

## Decisions (Daniel, 2026-10-21)

1. **Version floor: 17.4.0.** Output cache eviction and the resolved-items stash exist from 17.4. The
   range stays `[17.4.0,19.0.0)`, as the family states its ranges.
2. **Delivery servers need write access** to the package's own tables (mapping, heartbeat, acks).
   Read-only delivery servers are not supported in v1; document the requirement. Acks get their own
   seam (`IAckTransport`), separate from `ICacheTagStore`, since they are coordination rather than
   storage.
3. **Descendant routes are expanded at purge time** on the source server with
   `INavigationQueryService.TryGetDescendantsKeys` for route-changing events (URL segment change,
   move, unpublish, domain change), overflowing to `t-` or `umbraco` past the budget. The mapping does
   not store ancestor keys.

## Limits of the spike

- One server. The barrier is backed by code, not by a two-process test.
- One editor of each kind. Not covered: block grid, nested blocks, blocks in rich text, member picker,
  `fields=`, `expand=all`, depth above 2, segments, content behind member protection.
- Over-purging was measured only for the variant case; the walkers ignore single-quoted attributes.
- Ops run through `/spike/op` with an Umbraco context, not through the backoffice API.

## Milestone 2: build and test first

Build: the collector (result filter), q-key normalization and hashing, the mapping store with batched
additive writes, the source-side ledger, and an in-process purge planner (ledger event to key set).
Leave the outbox, targets and barrier for Milestone 3.

Test, by turning the spike fixture into an integration suite:

- **Differential check:** snapshot every endpoint, run an operation, fetch everything again. Every
  response whose body changed must have its q-key in the planned purge set. Report the over-purge ratio.
- **Collector:** the mapping contains at least objects and culture-scoped references, no element keys,
  depth 2 only when expanded.
- **Planner,** table-driven from section 4: a sort is one `ch-` event; a move purges old and new
  parents; a rename expands to descendants with overflow; a draft save produces nothing; a two-payload
  publish is one event.
- **Mapping:** a shrinking response keeps old rows until they age out; the render-then-publish race
  gives `no-store`; an output-cache hit replays the header and refreshes the TTL; the segment changes
  the hash; listings get selector rows.
- **Load:** rows and write throughput at about 10,000 cold renders.
- **One-server ordering:** the ack handler runs after the memory cache refresh.
