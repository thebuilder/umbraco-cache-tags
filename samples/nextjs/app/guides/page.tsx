import Link from "next/link";
import { Suspense } from "react";
import { notFound } from "next/navigation";
import { connection } from "next/server";
import { CacheInfo } from "@/app/cache-info";
import { Picture } from "@/app/media";
import { Pending } from "@/app/pending";
import { type ApiContent, type ApiLink, type ApiMedia, type ApiPaged, contentByRoute, deliveryApi, href } from "@/lib/umbraco";

type Section = { title: string; intro: string; image: ApiMedia[] | null; featured: ApiLink[] | null };
type Category = { title: string; intro: string };

/**
 * The Guides section and four listings of it: its children, every guide page under it (found by
 * content type), and the three most recently updated items (found only by sorting). Renaming a
 * category changes the routes of every page under it; editing any guide can change the last list.
 */
export default function GuidesPage() {
  return (
    <Suspense fallback={<Pending />}>
      <GuidesContent />
    </Suspense>
  );
}

async function GuidesContent() {
  await connection();
  const [section, categories, guides, recent] = await Promise.all([
    contentByRoute<Section>("/guides/"),
    deliveryApi<ApiPaged<ApiContent<Category>>>("content?fetch=children:/guides/&sort=sortOrder:asc"),
    deliveryApi<ApiPaged<ApiContent>>("content?fetch=descendants:/guides/&filter=contentType:guidePage&sort=name:asc&take=50"),
    deliveryApi<ApiPaged<ApiContent>>("content?fetch=descendants:/guides/&sort=updateDate:desc&take=3"),
  ]);
  if (!section.data) {
    notFound();
  }

  const { title, intro, image, featured } = section.data.properties;
  const picture = image?.[0];
  return (
    <>
      <h1>{title}</h1>
      <p className="lead">{intro}</p>
      {picture && <Picture image={picture} />}
      {featured && featured.length > 0 && (
        <p>
          Featured:{" "}
          {featured.map((item, index) => (
            <span key={item.id}>
              {index > 0 && ", "}
              <Link href={href(item)}>{item.name}</Link>
            </span>
          ))}
        </p>
      )}
      <h2>Categories</h2>
      <ul className="cards">
        {categories.data?.items.map((category) => (
          <li key={category.id}>
            <h3>
              <Link href={href(category)}>{category.properties.title}</Link>
            </h3>
            <p>{category.properties.intro}</p>
          </li>
        ))}
      </ul>
      <h2>Every guide, A to Z</h2>
      <ul>
        {guides.data?.items.map((guide) => (
          <li key={guide.id}>
            <Link href={href(guide)}>{guide.name}</Link> <small>{guide.route.path}</small>
          </li>
        ))}
      </ul>
      <h2>Recently updated</h2>
      <ol>
        {recent.data?.items.map((item) => (
          <li key={item.id}>
            <Link href={href(item)}>{item.name}</Link> <small>{item.updateDate}</small>
          </li>
        ))}
      </ol>
      {(categories.data?.total ?? 0) === 0 && (
        <p>
          No listings yet. On a fresh database Umbraco fills its Delivery API index about a minute after the
          first start.
        </p>
      )}
      <CacheInfo fetches={[section, categories, guides, recent]} />
    </>
  );
}
