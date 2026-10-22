// Checks TheBuilder.CacheTags end to end: publishes changes through Umbraco's Management API and
// waits for the pages that render them to refresh, and for the pages that do not to keep their cache.
//
//   pnpm e2e
//
// Needs both samples running: Umbraco (dotnet run) and this site built and served with `pnpm start`
// (`pnpm dev` caches differently). Reads UMBRACO_URL, FRONTEND_URL, UMBRACO_CLIENT_ID,
// UMBRACO_CLIENT_SECRET and E2E_TIMEOUT (seconds per case, default 30) from the environment, then
// .env.local, then .env. Every change is undone when its case ends, so it can run again and again
// against the same database. Exits 1 when a case fails.
import { existsSync } from "node:fs";
import { setTimeout as sleep } from "node:timers/promises";

/** One Delivery API response in a page's Cache box. */
type Fetched = { request: string; fetchedAt: string; tags: string };
type Page = { path: string; html: string; fetches: Fetched[]; footerFetchedAt: string | null };
type Check = { path: string; label: string; test: (current: Page) => boolean };
type Result = { check: Check; ok: boolean; seconds?: number; current: Page };
type Report = { checks: (results: Result[]) => void; note: (text: string) => void };
type Case = { name: string; run: (report: Report) => Promise<void>; undo: () => Promise<void> };

/** What the Management API takes to update a document or media item. */
type Update = {
  values: { alias: string; culture: string | null; segment: string | null; value: unknown }[];
  variants: { culture: string | null; segment: string | null; name: string }[];
};
type Item = Update & { template: unknown; isTrashed: boolean };

for (const file of [".env.local", ".env"]) {
  if (existsSync(file)) {
    process.loadEnvFile(file);
  }
}

const umbracoUrl = (process.env.UMBRACO_URL ?? "http://localhost:5320").replace(/\/$/, "");
const frontendUrl = (process.env.FRONTEND_URL ?? "http://localhost:3000").replace(/\/$/, "");
const clientId = process.env.UMBRACO_CLIENT_ID ?? "";
const clientSecret = process.env.UMBRACO_CLIENT_SECRET ?? "";
const timeoutMs = Number(process.env.E2E_TIMEOUT ?? 30) * 1000;

// The seeded keys, from ../TheBuilder.CacheTags.Example/Seeding/ExampleContent.cs.
const keys = {
  articles: "e0000000-0000-0000-0000-000000000002",
  harbour: "e0000000-0000-0000-0000-000000000003",
  about: "e0000000-0000-0000-0000-000000000006",
  harbourImage: "e1000000-0000-0000-0000-000000000001",
  articleType: "e3000000-0000-0000-0000-000000000003",
  guideType: "e3000000-0000-0000-0000-000000000012",
  gettingAround: "e0000000-0000-0000-0000-000000000011",
  byBike: "e0000000-0000-0000-0000-000000000012",
  byFerry: "e0000000-0000-0000-0000-000000000014",
  onFoot: "e0000000-0000-0000-0000-000000000015",
  // The dictionary item the footer shows (Dictionary/DictionarySeeder.cs in the Umbraco sample).
  footerTagline: "e7000000-0000-0000-0000-000000000001",
  // Not seeded: the article case 2 creates, and deletes again. e9 keys are kept for this script.
  newArticle: "e9000000-0000-0000-0000-000000000001",
};

// What this run appends to the text it changes. Restoring strips any such suffix, so a run that was
// stopped halfway is cleaned up by the next one.
const runId = new Date().toISOString().replace(/\D/g, "").slice(0, 14);
const marker = ` [e2e ${runId}]`;
const markerPattern = / \[e2e \d+\]$/;

// ---------------------------------------------------------------------------------------------------
// Umbraco's Management API, as the API user the sample seeds (Seeding/ExampleApiUserSeeder.cs).

let token = { value: "", expiresAt: 0 };

async function management<T = unknown>(method: string, path: string, body?: unknown): Promise<T> {
  if (Date.now() >= token.expiresAt) {
    const response = await fetch(`${umbracoUrl}/umbraco/management/api/v1/security/back-office/token`, {
      method: "POST",
      body: new URLSearchParams({ grant_type: "client_credentials", client_id: clientId, client_secret: clientSecret }),
    });
    if (!response.ok) {
      throw new Error(`Umbraco refused a token for ${clientId}: ${response.status} ${await response.text()}`);
    }
    const { access_token, expires_in } = (await response.json()) as { access_token: string; expires_in: number };
    token = { value: access_token, expiresAt: Date.now() + (expires_in - 30) * 1000 };
  }

  const response = await fetch(`${umbracoUrl}/umbraco/management/api/v1/${path}`, {
    method,
    headers: { Authorization: `Bearer ${token.value}`, ...(body ? { "Content-Type": "application/json" } : {}) },
    body: body ? JSON.stringify(body) : undefined,
  });
  if (!response.ok) {
    throw new Error(`${method} ${path} answered ${response.status}: ${await response.text()}`);
  }
  const text = await response.text();
  return (text ? JSON.parse(text) : null) as T;
}

type DictionaryItem = { name: string; translations: { isoCode: string; translation: string }[] };

/** Changes the English text of a dictionary item through the Management API. */
async function updateDictionary(id: string, change: (text: string) => string) {
  const { name, translations } = await management<DictionaryItem>("GET", `dictionary/${id}`);
  await management("PUT", `dictionary/${id}`, {
    name,
    translations: translations.map(({ isoCode, translation }) => ({
      isoCode,
      translation: isoCode === "en-US" ? change(translation) : translation,
    })),
  });
}

/** The GET shape carries read-only fields the PUT rejects, so keep only what an update takes. */
function forUpdate({ values, variants }: Update): Update {
  return {
    values: values.map(({ alias, culture, segment, value }) => ({ alias, culture, segment, value })),
    variants: variants.map(({ culture, segment, name }) => ({ culture, segment, name })),
  };
}

async function updateDocument(id: string, change: (update: Update) => void) {
  const document = await management<Item>("GET", `document/${id}`);
  const update = forUpdate(document);
  change(update);
  await management("PUT", `document/${id}`, { ...update, template: document.template });
}

async function publish(id: string, cultures: (string | null)[] = [null]) {
  await management("PUT", `document/${id}/publish`, { publishSchedules: cultures.map((culture) => ({ culture })) });
}

/** Puts a parent's children in the given order. */
async function sort(parent: string, children: string[]) {
  await management("PUT", "document/sort", {
    parent: { id: parent },
    sorting: children.map((id, sortOrder) => ({ id, sortOrder })),
  });
}

/** Waits until the Delivery API index has something matching a filter again, for the next case. */
async function indexed(filter: string, ms = 120_000) {
  const started = Date.now();
  while (Date.now() - started < ms) {
    const response = await fetch(`${umbracoUrl}/umbraco/delivery/api/v2/content?filter=${filter}&take=1`);
    if (response.ok && ((await response.json()) as { total: number }).total > 0) {
      return;
    }
    await sleep(2000);
  }
  throw new Error(`Umbraco's index has nothing for ${filter} after ${ms / 1000}s.`);
}

/** Changes a document type's alias. */
async function setTypeAlias(id: string, alias: string) {
  const { id: _, ...type } = await management<{ id: string; alias: string }>("GET", `document-type/${id}`);
  if (type.alias !== alias) {
    await management("PUT", `document-type/${id}`, { ...type, alias });
  }
}

function setValue(update: Update, alias: string, culture: string | null, map: (text: string) => string) {
  const value = update.values.find((v) => v.alias === alias && v.culture === culture);
  if (!value) {
    throw new Error(`No ${alias} value${culture ? ` in ${culture}` : ""}.`);
  }
  value.value = map(String(value.value));
}

const original = (text: string) => text.replace(markerPattern, "");

// ---------------------------------------------------------------------------------------------------
// The Next.js site. Each page ends with a Cache box (app/cache-info.tsx): one line per Delivery API
// response, with when it was fetched from Umbraco and the tags it got.

async function page(path: string): Promise<Page> {
  const response = await fetch(`${frontendUrl}${path}`, { cache: "no-store" });
  const html = await response.text();
  if (!response.ok) {
    throw new Error(`${path} answered ${response.status}.`);
  }

  const fetches: Fetched[] = [];
  const item = /<li><code>(.*?)<\/code>.*?<time dateTime="([^"]+)"[^>]*>.*?, tags:(?:<!-- -->)?\s*(?:<em>none<\/em>|<code>(.*?)<\/code>)<\/li>/g;
  for (const [, request, fetchedAt, tags] of html.matchAll(item)) {
    fetches.push({ request: text(request), fetchedAt, tags: tags ? text(tags) : "none" });
  }
  if (fetches.length === 0) {
    throw new Error(`${path} has no Cache box. Is it this sample, served with pnpm start?`);
  }
  // The footer's dictionary response (app/layout.tsx), cached apart from the page's own.
  const footerFetchedAt = html.match(/<footer data-fetched-at="([^"]+)"/)?.[1] ?? null;
  return { path, html, fetches, footerFetchedAt };
}

function text(html: string) {
  return html
    .replace(/<!-- -->/g, "")
    .replace(/&amp;/g, "&")
    .replace(/&quot;/g, '"')
    .replace(/&#x27;/g, "'")
    .replace(/&lt;/g, "<")
    .replace(/&gt;/g, ">");
}

function describe(fetched: Fetched) {
  return `${fetched.request} fetched at ${fetched.fetchedAt}, tags: ${fetched.tags}`;
}

/**
 * Reads the pages until two reads a second apart agree, and returns that read as the baseline. A
 * check only means something if the page was cached before the change: an empty entry (after the
 * previous case's undo, say) would be filled with fresh content whether or not anything was purged.
 */
async function settle(paths: string[]) {
  const fingerprint = (pages: Page[]) =>
    pages.map((p) => [...p.fetches.map((f) => f.fetchedAt), p.footerFetchedAt].join()).join("|");
  let previous = await Promise.all(paths.map(page));
  for (let tries = 0; tries < 15; tries++) {
    await sleep(1000);
    const current = await Promise.all(paths.map(page));
    if (fingerprint(current) === fingerprint(previous)) {
      return current;
    }
    previous = current;
  }
  throw new Error(`${paths.join(", ")} kept refreshing on their own, so there is no baseline to compare with.`);
}

// Checks, each judged against a fresh read of its page.

/** The items of one listing on a page (its `<ul data-listing="...">`), as HTML. */
function listing(html: string, name: string) {
  const start = html.indexOf(`data-listing="${name}"`);
  if (start < 0) {
    throw new Error(`The page has no ${name} listing.`);
  }
  return html.slice(start, html.indexOf("</ul>", start));
}

/** The listing shows `first` before `second`. */
const lists = (path: string, name: string, first: string, second: string): Check => ({
  path,
  label: `${path} lists ${first} before ${second} (${name})`,
  test: (current) => {
    const items = listing(current.html, name);
    return items.includes(`>${first}<`) && items.indexOf(`>${first}<`) < items.indexOf(`>${second}<`);
  },
});

/** The listing shows (or no longer shows) `item`. */
const contains = (path: string, name: string, item: string, shown: boolean): Check => ({
  path,
  label: `${path} ${shown ? "lists" : "no longer lists"} ${item} (${name})`,
  test: (current) => listing(current.html, name).includes(`>${item}<`) === shown,
});

/** A check made directly against Umbraco's Delivery API, rather than a page of this site. */
function direct(label: string, ok: boolean, detail: string): Result {
  const current: Page = { path: "", html: "", fetches: [], footerFetchedAt: null };
  return { check: { path: "", label: ok ? label : `${label}: ${detail}`, test: () => ok }, ok, current };
}

/** The page shows `expected`, which only the change put there. */
const shows = (path: string, expected: string, what: string): Check => ({
  path,
  label: `${path} shows ${what}`,
  test: (current) => current.html.includes(expected),
});

/** The response that renders the change was fetched again since `before`. */
const refetched = (before: Page, request: string, why: string): Check => ({
  path: before.path,
  label: `${before.path} refreshed (${why})`,
  test: (current) => {
    const then = before.fetches.find((f) => f.request === request);
    const now = current.fetches.find((f) => f.request === request);
    return Boolean(then && now && now.fetchedAt !== then.fetchedAt);
  },
});

/** The footer's dictionary response was fetched again since `before`. */
const footerRefetched = (before: Page): Check => ({
  path: before.path,
  label: `${before.path} refetched its footer (cached at ${before.footerFetchedAt})`,
  test: (current) => Boolean(before.footerFetchedAt && current.footerFetchedAt && current.footerFetchedAt !== before.footerFetchedAt),
});

/** The response for `request` is still the one cached at `before`. */
const keptResponse = (before: Page, request: string, why: string): Check => ({
  path: before.path,
  label: `${before.path} kept ${request} (${why})`,
  test: (current) => {
    const then = before.fetches.find((f) => f.request === request);
    const now = current.fetches.find((f) => f.request === request);
    return Boolean(then && now && now.fetchedAt === then.fetchedAt);
  },
});

/** Every response on the page is still the one cached at `before`. */
const kept = (before: Page): Check => ({
  path: before.path,
  label: `${before.path} kept its cache from ${before.fetches.map((f) => f.fetchedAt).join(", ")}`,
  test: (current) =>
    current.fetches.every((now) => before.fetches.some((then) => then.request === now.request && then.fetchedAt === now.fetchedAt)),
});

/**
 * Reads the pages until every check holds or the time is up. Each read is what lets a stale entry
 * (a type, children or search key purge) refresh in the background, so the next read can see it.
 */
async function waitFor(checks: Check[], ms = timeoutMs): Promise<Result[]> {
  const started = Date.now();
  const results = new Map<Check, Result>();
  while (true) {
    const pages = new Map<string, Page>();
    for (const check of checks) {
      if (results.get(check)?.ok) {
        continue;
      }
      const current = pages.get(check.path) ?? (await page(check.path));
      pages.set(check.path, current);
      results.set(check, { check, ok: check.test(current), seconds: (Date.now() - started) / 1000, current });
    }
    if ([...results.values()].every((r) => r.ok) || Date.now() - started >= ms) {
      return [...results.values()];
    }
    await sleep(1000);
  }
}

/**
 * For checks that something did not happen, after the things that should have. Reads twice, since a
 * stale purge still serves the old entry once and only the next read shows the refresh.
 */
async function holdsNow(checks: Check[]): Promise<Result[]> {
  await Promise.all(checks.map((check) => page(check.path)));
  await sleep(1500);
  return Promise.all(
    checks.map(async (check) => {
      const current = await page(check.path);
      return { check, ok: check.test(current), current };
    }),
  );
}

// ---------------------------------------------------------------------------------------------------
// The cases, from samples/README.md.

const cases: Case[] = [
  {
    name: "Editing an article's summary refreshes it, the listing and the pages that pick it, and nothing else",
    async run(report) {
      const summary = `What the harbour looks like before the city wakes up.${marker}`;
      const [, , home, forest, about] = await settle(["/articles/harbour-mornings", "/articles", "/", "/articles/forest-walks", "/en-us/about"]);

      await updateDocument(keys.harbour, (update) => setValue(update, "summary", null, (v) => original(v) + marker));
      await publish(keys.harbour);

      report.checks(
        await waitFor([
          shows("/articles/harbour-mornings", summary, "the new summary"),
          shows("/articles", summary, "the new summary"),
          refetched(home, "content/item/", "it features the article"),
          refetched(forest, "content/item/articles/forest-walks/", "it picks the article as related"),
        ]),
      );
      report.checks(await holdsNow([kept(about)]));
    },
    async undo() {
      await updateDocument(keys.harbour, (update) => setValue(update, "summary", null, original));
      await publish(keys.harbour);
    },
  },
  {
    name: "Publishing a new article under Articles adds it to the listing and to search results",
    async run(report) {
      const title = `A new article${marker}`;
      // A word only this run's article has. Its search page is cached first, finding nothing.
      const word = `e2e${runId}`;
      const search = `/search?q=${word}`;
      await settle(["/articles", search]);
      const values = [
        { alias: "title", culture: null, segment: null, value: title },
        { alias: "summary", culture: null, segment: null, value: `Published by the end-to-end check (${word}), and deleted again.` },
      ];
      await management("POST", "document", {
        id: keys.newArticle,
        parent: { id: keys.articles },
        documentType: { id: keys.articleType },
        template: null,
        values,
        variants: [{ culture: null, segment: null, name: title }],
      });
      await publish(keys.newArticle);

      // The listing comes from the Delivery API index, which picks a publish up a little later.
      report.checks(
        await waitFor(
          [shows("/articles", title, "the new article"), shows(search, title, "the new article in the search results")],
          timeoutMs * 2,
        ),
      );
    },
    async undo() {
      await deleteNewArticle();
    },
  },
  {
    name: "Publishing only da-DK of the about page refreshes /da-dk/om-os",
    async run(report) {
      const [, english] = await settle(["/da-dk/om-os", "/en-us/about"]);

      await updateDocument(keys.about, (update) => setValue(update, "title", "da-DK", (v) => original(v) + marker));
      await publish(keys.about, ["da-DK"]);

      const [danish] = await waitFor([shows("/da-dk/om-os", `Om dette site${marker}`, "the new Danish title")]);
      report.checks([danish]);

      // Reported, not judged: whether a purge can be limited to the cultures that were published.
      const [{ ok }] = await holdsNow([kept(english)]);
      if (!danish.ok) {
        report.note(`/en-us/about ${ok ? "kept its cache" : "refreshed"}, but with no Danish purge that says nothing.`);
      } else {
        report.note(
          ok
            ? "/en-us/about kept its cache: the purge was limited to da-DK."
            : "/en-us/about refreshed too: the purge was not limited to da-DK.",
        );
      }
    },
    async undo() {
      await updateDocument(keys.about, (update) => setValue(update, "title", "da-DK", original));
      await publish(keys.about, ["da-DK"]);
    },
  },
  {
    name: "Renaming an image refreshes the pages that show it",
    async run(report) {
      await settle(["/articles/harbour-mornings", "/articles"]);
      const media = await management<Item>("GET", `media/${keys.harbourImage}`);
      const update = forUpdate(media);
      const name = original(update.variants[0].name) + marker;
      update.variants[0].name = name;
      await management("PUT", `media/${keys.harbourImage}`, update);

      // The image's name is its alt text.
      report.checks(
        await waitFor([
          shows("/articles/harbour-mornings", `alt="${name}"`, "the new image name"),
          shows("/articles", `alt="${name}"`, "the new image name"),
        ]),
      );
    },
    async undo() {
      const update = forUpdate(await management<Item>("GET", `media/${keys.harbourImage}`));
      update.variants[0].name = original(update.variants[0].name);
      await management("PUT", `media/${keys.harbourImage}`, update);
    },
  },
  {
    name: "Editing a dictionary item refreshes the pages that show it, through the site's own API",
    async run(report) {
      // The footer comes from /api/dictionary, which the package tags only because the sample's
      // controller asks it to (CacheTaggedAttribute), and which a dictionary save purges.
      const [articles] = await settle(["/articles", "/en-us/about"]);
      let tagline = "";
      await updateDictionary(keys.footerTagline, (text) => {
        tagline = original(text) + marker;
        return tagline;
      });

      report.checks(
        await waitFor([
          shows("/articles", tagline, "the new footer text"),
          footerRefetched(articles),
          shows("/en-us/about", tagline, "the new footer text"),
        ]),
      );
    },
    async undo() {
      await updateDictionary(keys.footerTagline, original);
    },
  },
  {
    name: "Sorting a category's guides reorders the listings they are ordered in, and not the items",
    async run(report) {
      const category = "/guides/getting-around";
      const [, guides, onFoot] = await settle([category, "/guides", "/guides/getting-around/on-foot"]);
      report.checks(await holdsNow([lists(category, "editors-order", "By bike", "On foot")]));

      await sort(keys.gettingAround, [keys.onFoot, keys.byFerry, keys.byBike]);

      // Every guide under the category by type, in sort order; and the recently updated guides,
      // whose order the index decides and which only the catch-all search- key reaches.
      report.checks(
        await waitFor([
          lists(category, "editors-order", "On foot", "By bike"),
          refetched(guides, "content?fetch=descendants:/guides/&sort=updateDate:desc&take=3", "it lists guides in an order the index decides"),
        ]),
      );
      report.checks(await holdsNow([keptResponse(onFoot, "content/item/guides/getting-around/on-foot/", "a sort changes no item")]));
    },
    async undo() {
      await sort(keys.gettingAround, [keys.byBike, keys.byFerry, keys.onFoot]);
    },
  },
  {
    name: "Renaming a document type's alias refreshes the pages that render it under the old alias",
    async run(report) {
      const [harbour] = await settle(["/articles/harbour-mornings"]);

      await setTypeAlias(keys.articleType, `articleE2e${runId}`);

      report.checks(await waitFor([refetched(harbour, "content/item/articles/harbour-mornings/", "it renders an article")]));
    },
    async undo() {
      await setTypeAlias(keys.articleType, "article");
    },
  },
  {
    name: "Unpublishing a category takes its guides out of the guide listing, and publishing it again puts them back",
    async run(report) {
      // The guides stay published: they leave and rejoin listings with the category above them.
      await settle(["/guides"]);
      report.checks(await holdsNow([contains("/guides", "a-to-z", "On foot", true)]));

      await management("PUT", `document/${keys.gettingAround}/unpublish`, { cultures: null });
      report.checks(await waitFor([contains("/guides", "a-to-z", "On foot", false)], timeoutMs * 2));

      // Umbraco publishes the guides again in a notification of their own, whose change can wait for
      // the ack timeout (60 seconds) when no refresh is matched to it.
      await settle(["/guides"]);
      await publish(keys.gettingAround);
      report.checks(await waitFor([contains("/guides", "a-to-z", "On foot", true)], timeoutMs * 3));
    },
    async undo() {
      await publish(keys.gettingAround);
    },
  },
  {
    name: "An item's old route answers a redirect that no cache keeps",
    async run(report) {
      const oldRoute = `${umbracoUrl}/umbraco/delivery/api/v2/content/item/guides/getting-around/on-foot/`;
      await updateDocument(keys.onFoot, (update) => {
        update.variants[0].name = original(update.variants[0].name) + marker;
      });
      await publish(keys.onFoot);

      const response = await fetch(oldRoute, { redirect: "manual" });
      const cacheControl = response.headers.get("cache-control");
      const cacheKeys = response.headers.get("x-umbraco-cache-keys");
      report.checks([
        direct("The old route answers 301", response.status === 301, `it answered ${response.status}`),
        direct("The redirect has Cache-Control: no-store", cacheControl === "no-store", `Cache-Control: ${cacheControl}`),
        direct("The redirect has no cache keys", cacheKeys === null, `X-Umbraco-Cache-Keys: ${cacheKeys}`),
      ]);
    },
    async undo() {
      await updateDocument(keys.onFoot, (update) => {
        update.variants[0].name = original(update.variants[0].name);
      });
      await publish(keys.onFoot);
    },
  },
  {
    name: "Renaming a document type's alias empties the listings filtered by the old one",
    async run(report) {
      await settle(["/guides"]);
      report.checks(await holdsNow([contains("/guides", "a-to-z", "On foot", true)]));

      // contentType:guidePage now matches nothing. Purged before Umbraco has indexed the guides again,
      // the listing would be cached with them again, and no later change would purge it.
      // Umbraco indexes a renamed type's documents again up to a minute later.
      await setTypeAlias(keys.guideType, `guidePageE2e${runId}`);
      report.checks(await waitFor([contains("/guides", "a-to-z", "On foot", false)], timeoutMs * 4));
    },
    async undo() {
      await setTypeAlias(keys.guideType, "guidePage");
      await indexed("contentType:guidePage");
    },
  },
];

/** Deletes the article case 2 creates, wherever a previous run left it. */
async function deleteNewArticle() {
  let document: Item;
  try {
    document = await management<Item>("GET", `document/${keys.newArticle}`);
  } catch (error) {
    if (message(error).includes("answered 404")) {
      return;
    }
    throw error;
  }
  if (!document.isTrashed) {
    await management("PUT", `document/${keys.newArticle}/move-to-recycle-bin`);
  }
  await management("DELETE", `recycle-bin/document/${keys.newArticle}`);
}

function message(error: unknown) {
  return error instanceof Error ? error.message : String(error);
}

// ---------------------------------------------------------------------------------------------------

async function main() {
  if (!clientId || !clientSecret) {
    console.error("Set UMBRACO_CLIENT_ID and UMBRACO_CLIENT_SECRET (see .env).");
    return 2;
  }

  // Both sites must answer before anything is changed.
  try {
    await management("GET", `document/${keys.harbour}`);
    await page("/");
  } catch (error) {
    console.error(`Start both samples first (Umbraco on ${umbracoUrl}, pnpm start on ${frontendUrl}).`);
    console.error(message(error));
    return 2;
  }

  // Put back anything a stopped run left behind.
  await deleteNewArticle();
  await setTypeAlias(keys.articleType, "article");
  await setTypeAlias(keys.guideType, "guidePage");
  await indexed("contentType:guidePage");

  console.log(`Checking ${frontendUrl} against ${umbracoUrl}, ${timeoutMs / 1000}s per case.\n`);
  let failed = 0;
  for (const [index, testCase] of cases.entries()) {
    const lines: string[] = [];
    let ok = true;
    const report: Report = {
      checks(results) {
        for (const { check, ok: passed, seconds, current } of results) {
          ok &&= passed;
          lines.push(passed ? `  ok    ${check.label}${seconds === undefined ? "" : ` (${seconds.toFixed(1)}s)`}` : `  FAIL  ${check.label}`);
          if (!passed) {
            lines.push(...current.fetches.map((f) => `          ${describe(f)}`));
          }
        }
      },
      note(text) {
        lines.push(`  info  ${text}`);
      },
    };

    try {
      await testCase.run(report);
    } catch (error) {
      ok = false;
      lines.push(`  FAIL  ${message(error)}`);
    } finally {
      try {
        await testCase.undo();
        // Let the undo's own purge land before the next case takes its baseline.
        await sleep(2000);
      } catch (error) {
        lines.push(`  undo failed, the next run retries it: ${message(error)}`);
      }
    }

    failed += ok ? 0 : 1;
    console.log(`${ok ? "PASS" : "FAIL"}  ${index + 1}. ${testCase.name}`);
    console.log(`${lines.join("\n")}\n`);
  }

  console.log(failed === 0 ? `All ${cases.length} cases passed.` : `${failed} of ${cases.length} cases failed.`);
  return failed === 0 ? 0 : 1;
}

process.exit(await main());
