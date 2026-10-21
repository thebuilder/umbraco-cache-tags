import { Block } from "@/app/blocks";
import { type ApiRichText, umbracoUrl } from "@/lib/umbraco";

/** Where Umbraco puts a block in rich text markup. */
const blockPlaceholder = /<umb-rte-block(?:-inline)?\b[^>]*\bdata-content-id="([^"]+)"[^>]*><\/umb-rte-block(?:-inline)?>/;

/**
 * Rich text markup with its blocks rendered where they sit. Umbraco resolves links to pages to
 * routes, which match this app's, and links and images in media to paths on the Umbraco site.
 */
export function RichText({ value }: { value: ApiRichText | null }) {
  if (!value) {
    return null;
  }

  const markup = value.markup.replace(/(src|href)="\/media\//g, `$1="${umbracoUrl}/media/`);
  // split() with a capture group alternates markup and block ids.
  const parts = markup.split(new RegExp(blockPlaceholder, "g"));
  return (
    <>
      {parts.map((part, index) => {
        if (index % 2 === 0) {
          return part ? <div key={index} dangerouslySetInnerHTML={{ __html: part }} /> : null;
        }

        const block = value.blocks?.find((candidate) => candidate.content.id === part);
        return block ? <Block key={index} block={block} /> : null;
      })}
    </>
  );
}
