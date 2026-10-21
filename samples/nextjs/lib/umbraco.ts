import { cacheLife, cacheTag } from "next/cache";

export const umbracoUrl = (process.env.UMBRACO_URL ?? "http://localhost:5320").replace(/\/$/, "");

/** The header TheBuilder.CacheTags puts its cache keys in, comma separated. */
const cacheKeysHeader = "x-umbraco-cache-keys";

/**
 * How long a Delivery API response is cached. Purges keep pages fresh; the hourly revalidate is only
 * a safety net for a purge that never arrives.
 */
const safetyNet = { stale: 300, revalidate: 3600, expire: 86400 };

export type Culture = "en-US" | "da-DK";

export type ApiRoute = { path: string };

export type ApiLink = {
  id: string;
  name: string;
  contentType: string;
  route: ApiRoute;
};

export type ApiContent<P = Record<string, unknown>> = ApiLink & {
  properties: P;
  cultures: Record<string, ApiRoute>;
};

export type ApiImage = {
  id: string;
  name: string;
  url: string;
  width: number;
  height: number;
};

export type ApiRichText = { markup: string };

export type ApiBlockList<P> = { items: { content: { id: string; properties: P } }[] };

export type ApiPaged<T> = { total: number; items: T[] };

/** One Delivery API call, as cached: what Umbraco answered and when the cache entry was filled. */
export type Fetched<T> = {
  request: string;
  culture: Culture | null;
  status: number;
  data: T | null;
  cacheKeys: string[];
  fetchedAt: string;
};

/**
 * Fetches a Delivery API path (with its query string) and caches the response, tagged with the
 * cache keys Umbraco sent. The keys are what a purge webhook names, so app/api/cache-tags can
 * expire exactly the entries that rendered a changed item. Without the header the entry gets no
 * tags and lives until the safety revalidate.
 *
 * `fetchedAt` is set inside the cache, so it shows when the entry was filled, not when the page
 * was served. A purge is visible as a new time.
 */
export async function deliveryApi<T>(request: string, culture: Culture | null = null): Promise<Fetched<T>> {
  "use cache";

  const response = await fetch(`${umbracoUrl}/umbraco/delivery/api/v2/${request.replace(/^\//, "")}`, {
    headers: culture ? { "Accept-Language": culture } : {},
  });

  if (!response.ok && response.status !== 404) {
    // Thrown, so the failure is not cached.
    throw new Error(`Umbraco answered ${response.status} for ${request}.`);
  }

  const cacheKeys = (response.headers.get(cacheKeysHeader) ?? "")
    .split(",")
    .map((key) => key.trim())
    .filter(Boolean);
  if (cacheKeys.length > 0) {
    cacheTag(...cacheKeys);
  }

  // A missing page is cached briefly, so content created later shows up without a purge.
  if (response.ok) {
    cacheLife(safetyNet);
  } else {
    cacheLife("seconds");
  }

  return {
    request,
    culture,
    status: response.status,
    data: response.ok ? ((await response.json()) as T) : null,
    cacheKeys,
    fetchedAt: new Date().toISOString(),
  };
}

/** A content item by its route, such as `/articles/harbour-mornings/`. */
export function contentByRoute<P>(path: string, culture: Culture | null = null) {
  return deliveryApi<ApiContent<P>>(`content/item${encodeURI(path)}`, culture);
}

/** Media URLs are relative to the Umbraco site. */
export function mediaUrl(image: ApiImage) {
  return `${umbracoUrl}${image.url}`;
}

export const cultures: Record<string, Culture> = { "en-us": "en-US", "da-dk": "da-DK" };

/** Where a Delivery API item lives in this app. Culture-variant pages sit under a locale prefix. */
export function href(item: ApiLink, culture: Culture = "en-US") {
  if (item.contentType === "aboutPage") {
    return `/${culture.toLowerCase()}${item.route.path}`.replace(/\/$/, "");
  }

  return item.route.path === "/" ? "/" : item.route.path.replace(/\/$/, "");
}
