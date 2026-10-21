import Link from "next/link";
import { Links, Picture, editedCrops } from "@/app/media";
import { RichText } from "@/app/rich-text";
import {
  type ApiBlock,
  type ApiBlockGrid,
  type ApiBlockList,
  type ApiGridItem,
  type ApiLink,
  type ApiMedia,
  type ApiMultiLink,
  type ApiRichText,
  href,
} from "@/lib/umbraco";

type Teaser = { heading: string; text: string; link: ApiLink | null };
type Hero = { heading: string; image: ApiMedia[] | null; links: ApiMultiLink[] | null };
type Text = { body: ApiRichText | null };
type FeatureList = { heading: string; items: ApiBlockList<Teaser> | null };
type Gallery = { heading: string; images: ApiMedia[] | null };
type Row = { heading: string };

/** One block from a block list, block grid or rich text, by its element type. */
export function Block({ block }: { block: ApiBlock }) {
  const { contentType, properties } = block.content;
  switch (contentType) {
    case "teaserBlock":
    case "calloutBlock": {
      const { heading, text, link } = properties as Teaser;
      return (
        <section className="block">
          <h3>{heading}</h3>
          <p>{text}</p>
          {/* An unpublished pick comes back as null: the block keeps its text but loses the link. */}
          {link ? <Link href={href(link)}>{link.name}</Link> : <em>No published link.</em>}
        </section>
      );
    }
    case "heroBlock": {
      const { heading, image, links } = properties as Hero;
      const picture = image?.[0];
      return (
        <section className="block">
          <h3>{heading}</h3>
          {picture && <Picture image={picture} />}
          <Links links={links} />
        </section>
      );
    }
    case "textBlock":
      return (
        <section className="block">
          <RichText value={(properties as Text).body} />
        </section>
      );
    case "featureListBlock": {
      const { heading, items } = properties as FeatureList;
      return (
        <section className="block">
          <h3>{heading}</h3>
          <BlockList list={items} />
        </section>
      );
    }
    case "galleryBlock": {
      const { heading, images } = properties as Gallery;
      return (
        <section className="block">
          <h3>{heading}</h3>
          <div className="gallery">
            {images?.map((image) => <Picture key={image.id} image={image} crop={editedCrops(image)[0]} />)}
          </div>
        </section>
      );
    }
    default:
      return (
        <section className="block">
          <p>
            <code>{contentType}</code>
          </p>
        </section>
      );
  }
}

export function BlockList({ list }: { list: ApiBlockList | null }) {
  return (
    <div className="block-list">
      {list?.items.map((block) => (
        <Block key={block.content.id} block={block} />
      ))}
    </div>
  );
}

/** A block grid: blocks span columns, and a row block lays out its areas, which hold blocks of their own. */
export function BlockGrid({ grid }: { grid: ApiBlockGrid | null }) {
  if (!grid) {
    return null;
  }

  return <GridItems items={grid.items} columns={grid.gridColumns} />;
}

function GridItems({ items, columns }: { items: ApiGridItem[]; columns: number }) {
  return (
    <div className="grid" style={{ gridTemplateColumns: `repeat(${columns}, 1fr)` }}>
      {items.map((item) => (
        <div key={item.content.id} style={{ gridColumn: `span ${item.columnSpan}` }}>
          {item.areas.length > 0 ? (
            <section className="block">
              <h3>{(item.content.properties as Row).heading}</h3>
              <div className="grid" style={{ gridTemplateColumns: `repeat(${item.areaGridColumns ?? columns}, 1fr)` }}>
                {item.areas.map((area) => (
                  <div key={area.alias} style={{ gridColumn: `span ${area.columnSpan}` }}>
                    <GridItems items={area.items} columns={area.columnSpan} />
                  </div>
                ))}
              </div>
            </section>
          ) : (
            <Block block={item} />
          )}
        </div>
      ))}
    </div>
  );
}
