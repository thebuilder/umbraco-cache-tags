import Link from "next/link";
import { Suspense } from "react";
import { notFound } from "next/navigation";
import { connection } from "next/server";
import { CacheInfo } from "@/app/cache-info";
import { Pending } from "@/app/pending";
import { type ApiContent, type ApiImage, type ApiPaged, contentByRoute, deliveryApi, href, mediaUrl } from "@/lib/umbraco";

type ArticleList = { title: string; intro: string };
type ArticleSummary = { title: string; summary: string; image: ApiImage[] };

export default function ArticlesPage() {
  return (
    <Suspense fallback={<Pending />}>
      <ArticlesContent />
    </Suspense>
  );
}

async function ArticlesContent() {
  await connection();
  const [listing, children] = await Promise.all([
    contentByRoute<ArticleList>("/articles/"),
    deliveryApi<ApiPaged<ApiContent<ArticleSummary>>>("content?fetch=children:/articles/&sort=sortOrder:asc&take=50"),
  ]);
  if (!listing.data) {
    notFound();
  }

  const articles = children.data?.items ?? [];
  return (
    <>
      <h1>{listing.data.properties.title}</h1>
      <p className="lead">{listing.data.properties.intro}</p>
      {articles.length === 0 && (
        <p>
          No articles yet. On a fresh database Umbraco fills its Delivery API index about a minute after
          the first start.
        </p>
      )}
      <ul className="cards">
        {articles.map((article) => {
          const image = article.properties.image?.[0];
          return (
            <li key={article.id}>
              {image && <img src={mediaUrl(image)} alt={image.name} width={image.width} height={image.height} />}
              <h2>
                <Link href={href(article)}>{article.properties.title}</Link>
              </h2>
              <p>{article.properties.summary}</p>
            </li>
          );
        })}
      </ul>
      <CacheInfo fetches={[listing, children]} />
    </>
  );
}
