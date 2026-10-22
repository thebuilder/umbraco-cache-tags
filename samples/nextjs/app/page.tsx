import Link from "next/link";
import { notFound } from "next/navigation";
import { connection } from "next/server";
import { CacheInfo } from "@/app/cache-info";
import { type ApiBlockList, type ApiImage, type ApiLink, contentByRoute, href, mediaUrl } from "@/lib/umbraco";

type Home = {
  title: string;
  intro: string;
  heroImage: ApiImage[];
  featuredArticle: ApiLink | null;
  teasers: ApiBlockList<{ heading: string; text: string; link: ApiLink | null }> | null;
};

export default async function HomePage() {
  // Rendered per request, from cached Delivery API responses, so the build does not need Umbraco.
  await connection();
  const home = await contentByRoute<Home>("/");
  if (!home.data) {
    notFound();
  }

  const { title, intro, heroImage, featuredArticle, teasers } = home.data.properties;
  const hero = heroImage?.[0];
  return (
    <>
      <h1>{title}</h1>
      <p className="lead">{intro}</p>
      {hero && <img src={mediaUrl(hero)} alt={hero.name} width={hero.width} height={hero.height} />}
      {featuredArticle && (
        <p>
          Featured: <Link href={href(featuredArticle)}>{featuredArticle.name}</Link>
        </p>
      )}
      <ul className="cards">
        {teasers?.items.map(({ content }) => (
          <li key={content.id}>
            <h2>{content.properties.heading}</h2>
            <p>{content.properties.text}</p>
            {content.properties.link && <Link href={href(content.properties.link)}>{content.properties.link.name}</Link>}
          </li>
        ))}
      </ul>
      <CacheInfo fetches={[home]} />
    </>
  );
}
