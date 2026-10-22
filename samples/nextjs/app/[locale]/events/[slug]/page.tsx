import Link from "next/link";
import { notFound } from "next/navigation";
import { CacheInfo } from "@/app/cache-info";
import { Picture } from "@/app/media";
import { type ApiLink, type ApiMedia, contentByRoute, cultures, href } from "@/lib/umbraco";

type Event = { title: string; description: string; date: string; image: ApiMedia[] | null; venue: ApiLink | null };

/**
 * One event in one culture. Title and description vary by culture; date, image and venue are shared,
 * so changing one of those changes both cultures, whichever culture is published.
 */
export default async function EventPage({ params }: PageProps<"/[locale]/events/[slug]">) {
  const { locale, slug } = await params;
  const culture = cultures[locale];
  if (!culture) {
    notFound();
  }

  const event = await contentByRoute<Event>(`/events/${slug}/`, culture);
  if (!event.data) {
    notFound();
  }

  const { title, description, date, image, venue } = event.data.properties;
  const picture = image?.[0];
  return (
    <article lang={culture}>
      <p>
        <Link href={`/${locale}/events`}>All events</Link>
      </p>
      <h1>{title}</h1>
      <p className="lead">{description}</p>
      <p>
        <time dateTime={date}>{date.replace("T", " ").slice(0, 16)}</time>
        {venue && (
          <>
            {" "}
            at <Link href={href(venue)}>{venue.name}</Link>
          </>
        )}
      </p>
      {picture && <Picture image={picture} />}
      <p>
        {Object.entries(event.data.cultures)
          .filter(([other]) => other !== culture)
          .map(([other, route]) => (
            <Link key={other} href={`/${other.toLowerCase()}${route.path}`.replace(/\/$/, "")} hrefLang={other}>
              {other}
            </Link>
          ))}
      </p>
      <CacheInfo fetches={[event]} />
    </article>
  );
}
