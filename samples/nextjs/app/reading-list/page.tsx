import { notFound } from "next/navigation";
import { connection } from "next/server";
import { CacheInfo } from "@/app/cache-info";
import { type ApiLink, contentByRoute } from "@/lib/umbraco";

type ReadingList = {
  title: string;
  notes: ApiLink[] | null;
};

/**
 * Picks 70 notes, more items than a header lists, so its response carries one stored set
 * (set-<hash>) in place of their keys. Renaming a picked note shows here; renaming the note it does
 * not pick leaves it cached.
 */
export default async function ReadingListPage() {
  await connection();
  const page = await contentByRoute<ReadingList>("/reading-list/");
  if (!page.data) {
    notFound();
  }

  const { title, notes } = page.data.properties;
  return (
    <article>
      <h1>{title}</h1>
      <ul data-listing="notes">
        {notes?.map((note) => (
          <li key={note.id}>{note.name}</li>
        ))}
      </ul>
      <CacheInfo fetches={[page]} />
    </article>
  );
}
