import { connection } from "next/server";
import { CacheInfo } from "@/app/cache-info";
import { type ApiContent, type ApiPaged, deliveryApi } from "@/lib/umbraco";

/** The second site's root, next to Home (ExampleContent.SecondSite in the Umbraco sample). */
const secondSite = "e0000000-0000-0000-0000-000000000200";

/**
 * A second site in the same Umbraco, read by its root's id. Its guide listing is fetched from inside
 * its root, so it carries that root's keys (type-guidepage-<root>): editing its guide refreshes it,
 * and leaves the first site's guide listings on /guides cached.
 */
export default async function SecondSitePage() {
  await connection();
  const [site, guides] = await Promise.all([
    deliveryApi<ApiContent<{ title: string; intro: string }>>(`content/item/${secondSite}`),
    deliveryApi<ApiPaged<ApiContent>>(`content?fetch=descendants:${secondSite}&filter=contentType:guidePage&sort=name:asc`),
  ]);

  return (
    <article>
      <h1>{site.data?.properties.title ?? "Second site"}</h1>
      <p className="lead">{site.data?.properties.intro}</p>
      <h2>Its guides</h2>
      <ul data-listing="guides">
        {guides.data?.items.map((guide) => (
          <li key={guide.id}>{guide.name}</li>
        ))}
      </ul>
      <CacheInfo fetches={[site, guides]} />
    </article>
  );
}
