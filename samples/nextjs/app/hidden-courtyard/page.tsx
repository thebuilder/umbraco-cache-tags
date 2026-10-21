import { Suspense } from "react";
import { notFound } from "next/navigation";
import { connection } from "next/server";
import { CacheInfo } from "@/app/cache-info";
import { type Guide, GuidePage } from "@/app/guide-page";
import { Pending } from "@/app/pending";
import { contentByRoute } from "@/lib/umbraco";

/** A guide page that only On foot's rich text links to. No listing or menu finds it, so it has its own route. */
export default function HiddenCourtyardPage() {
  return (
    <Suspense fallback={<Pending />}>
      <HiddenCourtyardContent />
    </Suspense>
  );
}

async function HiddenCourtyardContent() {
  await connection();
  const page = await contentByRoute<Guide>("/hidden-courtyard/");
  if (!page.data) {
    notFound();
  }

  return (
    <article>
      <GuidePage guide={page.data} />
      <CacheInfo fetches={[page]} />
    </article>
  );
}
