import Link from "next/link";
import { Suspense } from "react";
import { notFound } from "next/navigation";
import { CacheInfo } from "@/app/cache-info";
import { Pending } from "@/app/pending";
import { type ApiContent, type ApiMedia, type ApiPaged, contentByRoute, cultures, deliveryApi, href, mediaUrl } from "@/lib/umbraco";

type EventList = { title: string; intro: string };
type EventSummary = { title: string; description: string; date: string; image: ApiMedia[] | null };

/**
 * The events in one culture. The list is invariant and the events vary by culture, so /da-dk/events
 * leaves out the winter market, which is published in en-US only.
 */
export default function EventsPage({ params }: PageProps<"/[locale]/events">) {
  return (
    <Suspense fallback={<Pending />}>
      <EventsContent params={params} />
    </Suspense>
  );
}

async function EventsContent({ params }: Pick<PageProps<"/[locale]/events">, "params">) {
  const { locale } = await params;
  const culture = cultures[locale];
  if (!culture) {
    notFound();
  }

  const [list, events] = await Promise.all([
    contentByRoute<EventList>("/events/", culture),
    deliveryApi<ApiPaged<ApiContent<EventSummary>>>("content?fetch=children:/events/&sort=sortOrder:asc", culture),
  ]);
  if (!list.data) {
    notFound();
  }

  return (
    <div lang={culture}>
      <h1>{list.data.properties.title}</h1>
      <p className="lead">{list.data.properties.intro}</p>
      <ul className="cards">
        {events.data?.items.map((event) => {
          const image = event.properties.image?.[0];
          return (
            <li key={event.id}>
              {image && <img src={mediaUrl(image)} alt={image.name} width={image.width ?? undefined} height={image.height ?? undefined} />}
              <h2>
                <Link href={href(event, culture)}>{event.properties.title}</Link>
              </h2>
              <p>
                <time dateTime={event.properties.date}>{event.properties.date.slice(0, 10)}</time>
              </p>
              <p>{event.properties.description}</p>
            </li>
          );
        })}
      </ul>
      <CacheInfo fetches={[list, events]} />
    </div>
  );
}
