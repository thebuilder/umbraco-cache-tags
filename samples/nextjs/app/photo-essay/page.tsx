import { Suspense } from "react";
import { notFound } from "next/navigation";
import { connection } from "next/server";
import { CacheInfo } from "@/app/cache-info";
import { Downloads, Picture, editedCrops } from "@/app/media";
import { Pending } from "@/app/pending";
import { RichText } from "@/app/rich-text";
import { type ApiMedia, type ApiMediaItem, type ApiPaged, type ApiRichText, contentByRoute, deliveryApi, mediaUrl } from "@/lib/umbraco";

type PhotoEssay = {
  title: string;
  intro: string;
  gallery: ApiMedia[] | null;
  downloads: ApiMedia[] | null;
  body: ApiRichText | null;
};

/**
 * Seven picked images with crops, a PDF, rich text with an inline image, and a media folder listed
 * through the Media Delivery API. Renaming or recropping an image, or replacing the PDF, shows here.
 */
export default function PhotoEssayPage() {
  return (
    <Suspense fallback={<Pending />}>
      <PhotoEssayContent />
    </Suspense>
  );
}

async function PhotoEssayContent() {
  await connection();
  const [page, city] = await Promise.all([
    contentByRoute<PhotoEssay>("/photo-essay/"),
    deliveryApi<ApiPaged<ApiMediaItem>>("media?fetch=children:/photos/city/&sort=name:asc"),
  ]);
  if (!page.data) {
    notFound();
  }

  const { title, intro, gallery, downloads, body } = page.data.properties;
  return (
    <>
      <h1>{title}</h1>
      <p className="lead">{intro}</p>
      <div className="gallery">
        {gallery?.flatMap((image) => {
          const crops = editedCrops(image);
          return crops.length > 0
            ? crops.map((crop) => <Picture key={`${image.id}-${crop.alias}`} image={image} crop={crop} />)
            : [<Picture key={image.id} image={image} />];
        })}
      </div>
      <h2>Downloads</h2>
      <Downloads files={downloads} />
      <RichText value={body} />
      <h2>In the City folder</h2>
      <ul className="cards">
        {city.data?.items.map((item) => (
          <li key={item.id}>
            {item.mediaType === "Image" ? (
              <img src={mediaUrl(item)} alt={item.name} width={item.width ?? undefined} height={item.height ?? undefined} />
            ) : (
              <p>
                {item.mediaType}: <code>{item.path}</code>
              </p>
            )}
            <p>{item.name}</p>
          </li>
        ))}
      </ul>
      <CacheInfo fetches={[page, city]} />
    </>
  );
}
