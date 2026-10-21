import type { Fetched } from "@/lib/umbraco";

/** Shows when each Delivery API response on the page was cached, and the keys it was tagged with. */
export function CacheInfo({ fetches }: { fetches: Fetched<unknown>[] }) {
  return (
    <aside className="cache-info">
      <h2>Cache</h2>
      <ul>
        {fetches.map((fetched) => (
          <li key={`${fetched.request} ${fetched.culture}`}>
            <code>
              {fetched.request}
              {fetched.culture ? ` (${fetched.culture})` : ""}
            </code>{" "}
            fetched from Umbraco at{" "}
            <time dateTime={fetched.fetchedAt} data-testid="fetched-at">
              {fetched.fetchedAt}
            </time>
            , tags:{" "}
            {fetched.cacheKeys.length > 0 ? <code>{fetched.cacheKeys.join(", ")}</code> : <em>none</em>}
          </li>
        ))}
      </ul>
    </aside>
  );
}
