import Link from "next/link";
import { Suspense } from "react";
import { notFound } from "next/navigation";
import { CacheInfo } from "@/app/cache-info";
import { Pending } from "@/app/pending";
import { type ApiImage, type ApiLink, type ApiRichText, contentByRoute, href, mediaUrl } from "@/lib/umbraco";

type Article = {
  title: string;
  summary: string;
  image: ApiImage[];
  body: ApiRichText | null;
  related: ApiLink[] | null;
};

export default function ArticlePage({ params }: PageProps<"/articles/[slug]">) {
  return (
    <Suspense fallback={<Pending />}>
      <ArticleContent params={params} />
    </Suspense>
  );
}

async function ArticleContent({ params }: Pick<PageProps<"/articles/[slug]">, "params">) {
  const { slug } = await params;
  const article = await contentByRoute<Article>(`/articles/${slug}/`);
  if (!article.data) {
    notFound();
  }

  const { title, summary, image, body, related } = article.data.properties;
  const picture = image?.[0];
  return (
    <article>
      <p>
        <Link href="/articles">All articles</Link>
      </p>
      <h1>{title}</h1>
      <p className="lead">{summary}</p>
      {picture && <img src={mediaUrl(picture)} alt={picture.name} width={picture.width} height={picture.height} />}
      {/* Umbraco resolves rich text links to routes, which match this app's routes. */}
      {body && <div dangerouslySetInnerHTML={{ __html: body.markup }} />}
      {related && related.length > 0 && (
        <>
          <h2>Related</h2>
          <ul>
            {related.map((item) => (
              <li key={item.id}>
                <Link href={href(item)}>{item.name}</Link>
              </li>
            ))}
          </ul>
        </>
      )}
      <CacheInfo fetches={[article]} />
    </article>
  );
}
