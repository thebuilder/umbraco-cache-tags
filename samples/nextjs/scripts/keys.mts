// Checks the X-Umbraco-Cache-Keys header itself, straight from Umbraco: every seeded response must
// carry the key of every item it shows, picks, links to or uses, and nothing it does not.
//
//   pnpm keys
//
// A key missing from a header is a page that a publish never purges: it stays stale until the
// frontend's safety revalidate, and nothing else notices. The e2e suite only finds such a miss for
// the pages it changes; this checks every kind of reference the sample seeds.
//
// The expected keys are written out by hand from the seeders (../TheBuilder.CacheTags.Example/Seeding),
// never read from the package. Needs only the Umbraco sample, freshly seeded, with its Delivery API
// index filled (about a minute after a first start). Reads UMBRACO_URL from the environment, then
// .env.local, then .env. Exits 1 when a check fails.
import { existsSync } from "node:fs";

for (const file of [".env.local", ".env"]) {
  if (existsSync(file)) {
    process.loadEnvFile(file);
  }
}

const umbracoUrl = (process.env.UMBRACO_URL ?? "http://localhost:5320").replace(/\/$/, "");
const header = "x-umbraco-cache-keys";
// The sample's Delivery API key (Umbraco:CMS:DeliveryApi:ApiKey), for preview requests.
const previewKey = process.env.UMBRACO_PREVIEW_KEY ?? "example-local-preview-key";

// The seeded keys, from ../TheBuilder.CacheTags.Example/Seeding/ExampleContent.cs.
const doc = (last: string) => `content-e0000000-0000-0000-0000-0000000000${last}`;
const media = (last: string) => `media-e1000000-0000-0000-0000-0000000000${last}`;
const children = (last: string) => `children-e0000000-0000-0000-0000-0000000000${last}`;

const home = doc("01");
const articles = doc("02");
const harbourMornings = doc("03");
const forestWalks = doc("04");
const eveningsAtDusk = doc("05");
const about = doc("06");
const guides = doc("10");
const gettingAround = doc("11");
const byBike = doc("12");
const bikeParking = doc("13");
const byFerry = doc("14");
const onFoot = doc("15");
const eatingOut = doc("16");
const breakfast = doc("17");
const lateDinners = doc("18");
const streetFood = doc("19");
const hiddenCourtyard = doc("20");
const blocks = doc("21");
const photoEssay = doc("22");
const events = doc("23");
const midsummer = doc("24");
const harbourFestival = doc("25");
const winterMarket = doc("26");

const harbourImage = media("01");
const forestImage = media("02");
const duskImage = media("03");
const cityFolder = "media-e1000000-0000-0000-0000-000000000011";
const nightFolder = media("12");
const bridge = media("20");
const market = media("21");
const rooftops = media("22");
const lanterns = media("23");
const lake = media("24");
const meadow = media("25");
const courtyard = media("26");
const ferryTimetable = media("30");

type Request = { path: string; culture?: string; headers?: Record<string, string> };

type Check = Request & {
  why: string;
  /** For an item: exactly these keys, besides `umbraco`. */
  exactly?: string[];
  /** For a listing: at least these keys (the items it lists depend on the index). */
  includes?: string[];
  /** Keys that must not be there. */
  excludes?: string[];
};

const item = (path: string) => `content/item/${path}`;

const checks: Check[] = [
  {
    path: item(""),
    why: "Home: a hero image, a featured article, and a block list of teasers that link to Articles and About",
    exactly: [home, duskImage, harbourMornings, articles, about],
  },
  {
    path: item("articles/harbour-mornings/"),
    why: "an article: its image, the two articles it picks, and Forest walks again from a link in its body",
    exactly: [harbourMornings, harbourImage, forestWalks, eveningsAtDusk],
  },
  {
    path: item("guides/"),
    why: "a section: its image and the two guides it features, but not its children",
    exactly: [guides, bridge, byFerry, breakfast],
    excludes: [gettingAround, eatingOut],
  },
  {
    path: item("guides/getting-around/by-bike/bike-parking/"),
    why: "five levels down: no image, one related guide, and none of its ancestors",
    exactly: [bikeParking, byFerry],
    excludes: [guides, gettingAround, byBike],
  },
  {
    path: item("guides/getting-around/by-ferry/"),
    why: "every kind of reference: a PDF linked in rich text, a block in rich text that picks Breakfast, a picked article, attachments, and a multi URL picker with a document and media",
    exactly: [byFerry, bridge, ferryTimetable, breakfast, harbourMornings, harbourImage, harbourFestival],
  },
  {
    path: item("guides/getting-around/on-foot/"),
    why: "the only reference to Hidden courtyard is a link in its rich text",
    exactly: [onFoot, meadow, hiddenCourtyard, byBike],
  },
  {
    path: item("hidden-courtyard/"),
    why: "an image used only inline in rich text",
    exactly: [hiddenCourtyard, courtyard],
  },
  {
    path: item("guides/eating-out/"),
    why: "a category that features an unpublished guide: its key must be there, so publishing it later purges this",
    exactly: [eatingOut, streetFood, lateDinners],
  },
  {
    path: item("guides/eating-out/late-dinners/"),
    why: "a guide that picks the unpublished Street food",
    exactly: [lateDinners, lanterns, streetFood, breakfast],
  },
  {
    path: item("blocks/"),
    why: "a block grid with areas in areas, a block list inside a block, a gallery block with crops, and links",
    exactly: [blocks, guides, byFerry, breakfast, streetFood, events, bridge, market, lanterns, meadow, ferryTimetable],
  },
  {
    path: item("photo-essay/"),
    why: "a gallery with crops, a PDF, and rich text with a document link, a media link and an inline image",
    exactly: [photoEssay, bridge, market, rooftops, lanterns, lake, meadow, duskImage, ferryTimetable, byFerry, courtyard],
  },
  {
    path: item("events/harbour-festival/"),
    culture: "en-US",
    why: "a culture-variant event: its image and its venue",
    exactly: [harbourFestival, harbourImage, byFerry],
  },
  {
    path: item("events/havnefest/"),
    culture: "da-DK",
    why: "the same event in da-DK carries the same keys, since keys carry no culture",
    exactly: [harbourFestival, harbourImage, byFerry],
  },
  {
    path: item("events/winter-market/"),
    culture: "en-US",
    why: "published in en-US only",
    exactly: [winterMarket, market, lateDinners],
  },
  {
    path: `content/items?id=${streetFood.slice("content-".length)}&id=${breakfast.slice("content-".length)}`,
    why: "asking by id for an unpublished item still carries its key, so publishing it purges this",
    includes: [streetFood, breakfast],
  },
  {
    path: "content?fetch=children:/articles/",
    why: "a children listing: the parent's children- key and each article",
    includes: [`children-${articles.slice("content-".length)}`, harbourMornings, forestWalks, eveningsAtDusk],
  },
  {
    path: "content?fetch=children:/guides/",
    why: "a children listing of categories",
    includes: [children("10"), gettingAround, eatingOut],
  },
  {
    path: "content?fetch=descendants:/guides/&filter=contentType:guidePage",
    why: "a listing filtered by type: the type- key",
    includes: ["type-guidepage", byBike, bikeParking, byFerry, onFoot, breakfast, lateDinners],
  },
  {
    path: "content?fetch=descendants:/guides/&sort=updateDate:desc&take=3",
    why: "a page of a sorted listing: any guide can move into it, so it is reached through the index's catch-all key",
    includes: ["search-deliveryapicontentindex"],
  },
  {
    path: "content?filter=search:harbour",
    why: "a free-text search: the catch-all search- key",
    includes: ["search-deliveryapicontentindex", harbourMornings],
  },
  {
    path: "content?fetch=children:/events/",
    culture: "da-DK",
    why: "a listing in da-DK, which leaves out Winter market",
    includes: [children("23"), midsummer, harbourFestival],
  },
  {
    path: "media?fetch=children:/photos/city/",
    why: "a media folder listing",
    includes: [`children-${cityFolder.slice("media-".length)}`, bridge, market, rooftops, nightFolder],
  },
];

type Outcome = { ok: boolean; lines: string[] };

async function fetchKeys({ path, culture, headers }: Request) {
  const response = await fetch(`${umbracoUrl}/umbraco/delivery/api/v2/${path}`, {
    headers: { ...(culture ? { "Accept-Language": culture } : {}), ...headers },
  });
  await response.arrayBuffer();
  const value = response.headers.get(header);
  return {
    status: response.status,
    cacheControl: response.headers.get("cache-control"),
    keys: value === null ? null : value.split(",").map((key) => key.trim()).filter(Boolean),
  };
}

async function check(expected: Check): Promise<Outcome> {
  const { status, keys } = await fetchKeys(expected);
  const lines: string[] = [];
  if (status !== 200) {
    return { ok: false, lines: [`answered ${status}`] };
  }
  if (keys === null) {
    return { ok: false, lines: [`has no ${header} header`] };
  }

  const got = new Set(keys);
  if (!got.has("umbraco")) {
    lines.push("missing umbraco, which a site-wide change purges");
  }
  const wanted = expected.exactly ?? expected.includes ?? [];
  for (const key of wanted) {
    if (!got.has(key)) {
      lines.push(`missing ${key}`);
    }
  }
  if (expected.exactly) {
    const allowed = new Set(["umbraco", ...expected.exactly]);
    for (const key of got) {
      if (!allowed.has(key)) {
        lines.push(`unexpected ${key}`);
      }
    }
  }
  for (const key of expected.excludes ?? []) {
    if (got.has(key)) {
      lines.push(`should not carry ${key}`);
    }
  }
  if (lines.length > 0) {
    lines.push(`header: ${keys.join(", ")}`);
  }
  return { ok: lines.length === 0, lines };
}

/** Responses that must never be cached by a shared cache, so they carry no keys at all. */
async function uncached(): Promise<{ name: string; outcome: Outcome }[]> {
  const preview = await fetchKeys({
    path: item("guides/getting-around/by-ferry/"),
    headers: { Preview: "true", "Api-Key": previewKey },
  });
  const lines: string[] = [];
  if (preview.status !== 200) {
    lines.push(`answered ${preview.status}; is ${previewKey} the site's Delivery API key?`);
  }
  if (preview.keys !== null) {
    lines.push(`carries keys: ${preview.keys.join(", ")}`);
  }
  if (!preview.cacheControl?.includes("no-store")) {
    lines.push(`Cache-Control is ${preview.cacheControl ?? "missing"}, not no-store`);
  }

  // A never-published item answers 404. A frontend that cached that 404 would keep it after the
  // item is published, unless the 404 is not cacheable or carries the item's own key.
  const draft = await fetchKeys({ path: item("guides/eating-out/street-food/") });
  const draftLines: string[] = [];
  if (draft.status !== 404) {
    draftLines.push(`answered ${draft.status}, not 404, for an item that was never published`);
  }
  if (!draft.cacheControl?.includes("no-store") && !draft.keys?.includes(streetFood)) {
    draftLines.push(`is cacheable (Cache-Control: ${draft.cacheControl ?? "none"}) without ${streetFood}, so publishing it would not purge the 404`);
  }

  return [
    { name: "A preview response is no-store and carries no keys", outcome: { ok: lines.length === 0, lines } },
    { name: "A never-published item's 404 is purged when it is published", outcome: { ok: draftLines.length === 0, lines: draftLines } },
  ];
}

console.log(`Checking the ${header} header at ${umbracoUrl}.\n`);

let failed = 0;
for (const expected of checks) {
  const outcome = await check(expected);
  const name = `${expected.path}${expected.culture ? ` (${expected.culture})` : ""}`;
  console.log(`${outcome.ok ? "PASS" : "FAIL"}  ${name}\n      ${expected.why}`);
  for (const line of outcome.lines) {
    console.log(`      ${line}`);
  }
  failed += outcome.ok ? 0 : 1;
}
for (const { name, outcome } of await uncached()) {
  console.log(`${outcome.ok ? "PASS" : "FAIL"}  ${name}`);
  for (const line of outcome.lines) {
    console.log(`      ${line}`);
  }
  failed += outcome.ok ? 0 : 1;
}

console.log(failed === 0 ? "\nAll checks passed." : `\n${failed} check${failed === 1 ? "" : "s"} failed.`);
process.exit(failed === 0 ? 0 : 1);
