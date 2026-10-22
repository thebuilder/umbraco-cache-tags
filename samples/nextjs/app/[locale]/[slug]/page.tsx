import Link from "next/link";
import { notFound } from "next/navigation";
import { CacheInfo } from "@/app/cache-info";
import { type ApiRichText, contentByRoute, cultures } from "@/lib/umbraco";

type About = { title: string; body: ApiRichText | null };

/** A culture-variant page: `/en-us/about` and `/da-dk/om-os` are the same Umbraco item. */
export default async function VariantPage({ params }: PageProps<"/[locale]/[slug]">) {
  const { locale, slug } = await params;
  const culture = cultures[locale];
  if (!culture) {
    notFound();
  }

  const page = await contentByRoute<About>(`/${slug}/`, culture);
  if (!page.data) {
    notFound();
  }

  const { title, body } = page.data.properties;
  return (
    <article lang={culture}>
      <h1>{title}</h1>
      {body && <div dangerouslySetInnerHTML={{ __html: body.markup }} />}
      <p>
        {Object.entries(page.data.cultures)
          .filter(([other]) => other !== culture)
          .map(([other, route]) => (
            <Link key={other} href={`/${other.toLowerCase()}${route.path}`.replace(/\/$/, "")} hrefLang={other}>
              {other}
            </Link>
          ))}
      </p>
      <CacheInfo fetches={[page]} />
    </article>
  );
}
