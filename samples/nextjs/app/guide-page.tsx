import Link from "next/link";
import { Downloads, Links, Picture } from "@/app/media";
import { RichText } from "@/app/rich-text";
import { type ApiContent, type ApiLink, type ApiMedia, type ApiMultiLink, type ApiRichText, href } from "@/lib/umbraco";

export type Guide = {
  title: string;
  summary: string;
  image: ApiMedia[] | null;
  body: ApiRichText | null;
  difficulty: string | null;
  tags: string[] | null;
  related: ApiLink[] | null;
  attachments: ApiMedia[] | null;
  links: ApiMultiLink[] | null;
};

/** A guide page: rich text with a block, tags, a dropdown, pickers and links of every kind. */
export function GuidePage({ guide, children }: { guide: ApiContent<Guide>; children?: ApiContent[] }) {
  const { title, summary, image, body, difficulty, tags, related, attachments, links } = guide.properties;
  const picture = image?.[0];
  return (
    <>
      <h1>{title}</h1>
      <p className="lead">{summary}</p>
      <p>
        {difficulty && <span className="tag">{difficulty}</span>}
        {tags?.map((tag) => (
          <span key={tag} className="tag">
            #{tag}
          </span>
        ))}
      </p>
      {picture && <Picture image={picture} />}
      <RichText value={body} />
      {children && children.length > 0 && (
        <>
          <h2>Pages under this one</h2>
          <ul>
            {children.map((child) => (
              <li key={child.id}>
                <Link href={href(child)}>{child.name}</Link>
              </li>
            ))}
          </ul>
        </>
      )}
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
      {attachments && attachments.length > 0 && (
        <>
          <h2>Attachments</h2>
          <Downloads files={attachments} />
        </>
      )}
      {links && links.length > 0 && (
        <>
          <h2>Links</h2>
          <Links links={links} />
        </>
      )}
    </>
  );
}
