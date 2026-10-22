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
  createDate: string;
  updateDate: string;
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

/** Any media item. Files have no width or height; crops and focal point come from the picker. */
export type ApiMedia = {
  id: string;
  name: string;
  mediaType: string;
  url: string;
  extension: string | null;
  width: number | null;
  height: number | null;
  bytes: number | null;
  focalPoint: { left: number; top: number } | null;
  crops: ApiCrop[];
};

/** A crop: the output size, and the fractions cut from each edge (null when the editor set none). */
export type ApiCrop = {
  alias: string;
  width: number;
  height: number;
  coordinates: { x1: number; y1: number; x2: number; y2: number } | null;
};

/** A media item in a listing (`media?fetch=...`), which has a path of its own. */
export type ApiMediaItem = ApiMedia & { path: string };

/** Rich text as markup. Blocks sit in the markup as `umb-rte-block` placeholders. */
export type ApiRichText = { markup: string; blocks?: ApiBlock[] };

export type ApiBlock<P = Record<string, unknown>> = {
  content: { id: string; contentType: string; properties: P };
};

export type ApiBlockList<P = Record<string, unknown>> = { items: ApiBlock<P>[] };

export type ApiGridItem = ApiBlock & {
  columnSpan: number;
  rowSpan: number;
  areaGridColumns: number | null;
  areas: { alias: string; columnSpan: number; rowSpan: number; items: ApiGridItem[] }[];
};

export type ApiBlockGrid = { gridColumns: number; items: ApiGridItem[] };

/** A multi URL picker link. */
export type ApiMultiLink = {
  title: string | null;
  url: string | null;
  queryString: string | null;
  target: string | null;
  destinationId: string | null;
  destinationType: string | null;
  route: ApiRoute | null;
  linkType: "Content" | "Media" | "External";
};

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
export function deliveryApi<T>(request: string, culture: Culture | null = null): Promise<Fetched<T>> {
  return cachedUmbraco<T>(`/umbraco/delivery/api/v2/${request.replace(/^\//, "")}`, request, culture);
}

/**
 * Every dictionary item in one language, from the Umbraco sample's own API (Dictionary/ in the
 * sample), outside the Delivery API. TheBuilder.CacheTags tags it like a Delivery API response, and a
 * dictionary change purges it once every server's dictionary cache has the change.
 */
export function dictionary(locale: Culture) {
  return cachedUmbraco<Record<string, string>>(`/api/dictionary?locale=${locale}`, `dictionary?locale=${locale}`, null);
}

/** Fetches a path on Umbraco and caches the response under the cache keys it was tagged with. */
async function cachedUmbraco<T>(path: string, request: string, culture: Culture | null): Promise<Fetched<T>> {
  "use cache";

  let response: Response;
  try {
    response = await fetch(`${umbracoUrl}${path}`, {
      headers: culture ? { "Accept-Language": culture } : {},
    });
  } catch {
    // Thrown, so the failure is not cached. app/error.tsx shows it and tries again.
    throw new Error(`Umbraco is not reachable at ${umbracoUrl}; it may still be starting.`);
  }

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
export function mediaUrl(image: Pick<ApiImage, "url">) {
  return `${umbracoUrl}${image.url}`;
}

export const cultures: Record<string, Culture> = { "en-us": "en-US", "da-dk": "da-DK" };

/** The content types that vary by culture. Their pages sit under a locale prefix. */
const variantTypes = new Set(["aboutPage", "event"]);

/** Where a Delivery API item lives in this app. */
export function href(item: Pick<ApiLink, "contentType" | "route">, culture: Culture = "en-US") {
  if (variantTypes.has(item.contentType)) {
    return `/${culture.toLowerCase()}${item.route.path}`.replace(/\/$/, "");
  }

  return item.route.path === "/" ? "/" : item.route.path.replace(/\/$/, "");
}

/** Where a multi URL picker link goes: a page in this app, a file on Umbraco, or an external URL. */
export function linkHref(link: ApiMultiLink) {
  if (link.linkType === "Content" && link.route && link.destinationType) {
    return href({ contentType: link.destinationType, route: link.route }) + (link.queryString ?? "");
  }

  if (link.linkType === "Media" && link.url) {
    return mediaUrl({ url: link.url });
  }

  return link.url ?? "#";
}
