import Link from "next/link";
import { type ApiCrop, type ApiMedia, type ApiMultiLink, linkHref, mediaUrl } from "@/lib/umbraco";

/**
 * An image, cut to a crop when one is given. Umbraco's image processor wants a signed URL, so the
 * crop is drawn with CSS from its coordinates (the fractions cut from each edge), and an editor's
 * change to a crop shows up as a different cut.
 */
export function Picture({ image, crop }: { image: ApiMedia; crop?: ApiCrop }) {
  const src = mediaUrl(image);
  if (!crop) {
    return <img src={src} alt={image.name} width={image.width ?? undefined} height={image.height ?? undefined} />;
  }

  const { x1, y1, x2, y2 } = crop.coordinates ?? { x1: 0, y1: 0, x2: 0, y2: 0 };
  const keptX = 1 - x1 - x2;
  const keptY = 1 - y1 - y2;
  return (
    <figure className="crop" style={{ aspectRatio: `${crop.width} / ${crop.height}` }}>
      <img
        src={src}
        alt={image.name}
        style={{
          width: `${100 / keptX}%`,
          height: `${100 / keptY}%`,
          left: `${(-x1 * 100) / keptX}%`,
          top: `${(-y1 * 100) / keptY}%`,
        }}
      />
      <figcaption>
        {image.name}, {crop.alias} {crop.width}×{crop.height}
      </figcaption>
    </figure>
  );
}

/**
 * The crops an editor set on a pick. Umbraco lists every crop the data type defines on each pick, with
 * null coordinates for the ones nobody set.
 */
export function editedCrops(image: ApiMedia) {
  return image.crops.filter((crop) => crop.coordinates);
}

/** Files to download, such as the ferry timetable PDF. */
export function Downloads({ files }: { files: ApiMedia[] | null }) {
  if (!files || files.length === 0) {
    return null;
  }

  return (
    <ul>
      {files.map((file) => (
        <li key={file.id}>
          <a href={mediaUrl(file)}>{file.name}</a> ({file.extension}
          {file.bytes ? `, ${file.bytes} bytes` : ""})
        </li>
      ))}
    </ul>
  );
}

/** Multi URL picker links: pages in this app, files on Umbraco and external sites. */
export function Links({ links }: { links: ApiMultiLink[] | null }) {
  if (!links || links.length === 0) {
    return null;
  }

  return (
    <ul>
      {links.map((link, index) => (
        <li key={`${link.destinationId ?? link.url}-${index}`}>
          {link.linkType === "Content" ? (
            <Link href={linkHref(link)}>{link.title}</Link>
          ) : (
            <a href={linkHref(link)}>{link.title}</a>
          )}{" "}
          <small>({link.linkType.toLowerCase()})</small>
        </li>
      ))}
    </ul>
  );
}
