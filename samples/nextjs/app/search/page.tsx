import Link from "next/link";
import { CacheInfo } from "@/app/cache-info";
import { type ApiContent, type ApiPaged, deliveryApi, href } from "@/lib/umbraco";

/** The most words one search sends; each is a filter Umbraco has to match. */
const maxWords = 5;

/**
 * Full-text search through the Delivery API, with the Umbraco sample's `search:` filter
 * (Search/SearchFilter.cs). A search result is a Delivery API listing like any other, so
 * TheBuilder.CacheTags tags it, and a publish that could change it purges it once every server's
 * index has the change.
 *
 * Each distinct search is its own cached response. The words are normalised (lowercased,
 * deduplicated, sorted) so the same search is one entry.
 */
export default async function SearchPage({ searchParams }: PageProps<"/search">) {
  const { q } = await searchParams;
  const query = (Array.isArray(q) ? q[0] : (q ?? "")).trim();
  const words = [
    ...new Set(
      query
        .toLowerCase()
        .split(/\s+/)
        .map((word) => word.replace(/[^\p{L}\p{N}]/gu, ""))
        .filter(Boolean),
    ),
  ]
    .sort()
    .slice(0, maxWords);

  const results =
    words.length === 0
      ? null
      : await deliveryApi<ApiPaged<ApiContent>>(
          `content?${words.map((word) => `filter=search:${encodeURIComponent(word)}`).join("&")}&take=20`,
        );
  const items = results?.data?.items ?? [];

  return (
    <>
      <h1>Search</h1>
      <form action="/search" className="search">
        <input type="search" name="q" defaultValue={query} placeholder="harbour, ferry, festival…" aria-label="Search" />
        <button type="submit">Search</button>
      </form>
      {results && (
        <>
          <p className="lead">
            {items.length === 0 ? "Nothing matches" : `${results.data?.total ?? items.length} found for`} “{words.join(" ")}”.
          </p>
          <ul className="cards">
            {items.map((item) => (
              <li key={item.id}>
                <h2>
                  <Link href={href(item)}>{item.name}</Link>
                </h2>
                <p>{item.contentType}</p>
              </li>
            ))}
          </ul>
          <CacheInfo fetches={[results]} />
        </>
      )}
    </>
  );
}
