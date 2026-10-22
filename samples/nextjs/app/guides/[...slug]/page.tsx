import Link from "next/link";
import { Suspense } from "react";
import { notFound } from "next/navigation";
import { CacheInfo } from "@/app/cache-info";
import { type Guide, GuidePage } from "@/app/guide-page";
import { Pending } from "@/app/pending";
import { type ApiContent, type ApiLink, type ApiPaged, contentByRoute, deliveryApi, href } from "@/lib/umbraco";

type Category = { title: string; intro: string; featured: ApiLink[] | null };

/**
 * A category or guide page under /guides, at any depth, with breadcrumbs (its ancestors) and the
 * pages under it (its children). Both are listings: renaming Guides changes every breadcrumb here.
 */
export default function GuideRoutePage({ params }: PageProps<"/guides/[...slug]">) {
  return (
    <Suspense fallback={<Pending />}>
      <GuideRouteContent params={params} />
    </Suspense>
  );
}

async function GuideRouteContent({ params }: Pick<PageProps<"/guides/[...slug]">, "params">) {
  const { slug } = await params;
  const path = `/guides/${slug.join("/")}/`;
  const [page, ancestors, children] = await Promise.all([
    contentByRoute<Category | Guide>(path),
    deliveryApi<ApiPaged<ApiContent>>(`content?fetch=ancestors:${path}&sort=level:asc`),
    deliveryApi<ApiPaged<ApiContent<Category>>>(`content?fetch=children:${path}&sort=sortOrder:asc`),
  ]);
  if (!page.data) {
    notFound();
  }

  const kids = children.data?.items ?? [];
  return (
    <article>
      <nav aria-label="Breadcrumbs" className="breadcrumbs">
        {ancestors.data?.items.map((ancestor) => (
          <Link key={ancestor.id} href={href(ancestor)}>
            {ancestor.name}
          </Link>
        ))}
      </nav>
      {page.data.contentType === "category" ? (
        <CategoryView category={page.data as ApiContent<Category>} pages={kids} />
      ) : (
        <GuidePage guide={page.data as ApiContent<Guide>} children={kids} />
      )}
      <CacheInfo fetches={[page, ancestors, children]} />
    </article>
  );
}

function CategoryView({ category, pages }: { category: ApiContent<Category>; pages: ApiContent[] }) {
  const { title, intro, featured } = category.properties;
  return (
    <>
      <h1>{title}</h1>
      <p className="lead">{intro}</p>
      {/* Eating out also picks Street food, which is unpublished, so the Delivery API leaves it out. */}
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
      <ul className="cards">
        {pages.map((page) => (
          <li key={page.id}>
            <h2>
              <Link href={href(page)}>{page.name}</Link>
            </h2>
          </li>
        ))}
      </ul>
    </>
  );
}
