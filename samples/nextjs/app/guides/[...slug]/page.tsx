import Link from "next/link";
import { notFound } from "next/navigation";
import { CacheInfo } from "@/app/cache-info";
import { type Guide, GuidePage } from "@/app/guide-page";
import { type ApiContent, type ApiLink, type ApiPaged, contentByRoute, deliveryApi, href } from "@/lib/umbraco";

type Category = { title: string; intro: string; featured: ApiLink[] | null };

/**
 * A category or guide page under /guides, at any depth, with breadcrumbs (its ancestors), the pages
 * under it (its children) and every guide under it in the order editors set (by content type, so a
 * sort reaches it through the guides' type). All three are listings: renaming Guides changes every
 * breadcrumb here.
 */
export default async function GuideRoutePage({ params }: PageProps<"/guides/[...slug]">) {
  const { slug } = await params;
  const path = `/guides/${slug.join("/")}/`;
  const [page, ancestors, children, guides] = await Promise.all([
    contentByRoute<Category | Guide>(path),
    deliveryApi<ApiPaged<ApiContent>>(`content?fetch=ancestors:${path}&sort=level:asc`),
    deliveryApi<ApiPaged<ApiContent<Category>>>(`content?fetch=children:${path}&sort=sortOrder:asc`),
    deliveryApi<ApiPaged<ApiContent>>(`content?fetch=descendants:${path}&filter=contentType:guidePage&sort=sortOrder:asc&take=50`),
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
        <CategoryView category={page.data as ApiContent<Category>} pages={kids} guides={guides.data?.items ?? []} />
      ) : (
        <GuidePage guide={page.data as ApiContent<Guide>} children={kids} />
      )}
      <CacheInfo fetches={[page, ancestors, children, guides]} />
    </article>
  );
}

function CategoryView({ category, pages, guides }: { category: ApiContent<Category>; pages: ApiContent[]; guides: ApiContent[] }) {
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
      <h2>Every guide here, in the editors' order</h2>
      <ul data-listing="editors-order">
        {guides.map((guide) => (
          <li key={guide.id}>
            <Link href={href(guide)}>{guide.name}</Link>
          </li>
        ))}
      </ul>
    </>
  );
}
