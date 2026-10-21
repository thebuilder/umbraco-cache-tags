import { Suspense } from "react";
import { notFound } from "next/navigation";
import { connection } from "next/server";
import { BlockGrid, BlockList } from "@/app/blocks";
import { CacheInfo } from "@/app/cache-info";
import { Links } from "@/app/media";
import { Pending } from "@/app/pending";
import { type ApiBlockGrid, type ApiBlockList, type ApiMultiLink, contentByRoute } from "@/lib/umbraco";

type BlocksPage = {
  title: string;
  intro: string;
  grid: ApiBlockGrid | null;
  sections: ApiBlockList | null;
  links: ApiMultiLink[] | null;
};

/**
 * One response with everything nested in it: grid areas two levels deep, a block list inside a
 * block, blocks that link back to this page, a pick of an unpublished page, and every kind of link.
 */
export default function BlocksRoutePage() {
  return (
    <Suspense fallback={<Pending />}>
      <BlocksContent />
    </Suspense>
  );
}

async function BlocksContent() {
  await connection();
  const page = await contentByRoute<BlocksPage>("/blocks/");
  if (!page.data) {
    notFound();
  }

  const { title, intro, grid, sections, links } = page.data.properties;
  return (
    <>
      <h1>{title}</h1>
      <p className="lead">{intro}</p>
      <h2>Block grid</h2>
      <BlockGrid grid={grid} />
      <h2>Block list</h2>
      <BlockList list={sections} />
      <h2>Links</h2>
      <Links links={links} />
      <CacheInfo fetches={[page]} />
    </>
  );
}
