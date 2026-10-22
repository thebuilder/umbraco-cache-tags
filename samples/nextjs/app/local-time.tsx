"use client";

import { useEffect, useState } from "react";

const utc = new Intl.DateTimeFormat("en-GB", { dateStyle: "medium", timeStyle: "medium", timeZone: "UTC" });

/**
 * A timestamp in the reader's own locale and time zone. The server does not know them, so its HTML
 * shows UTC, and the browser swaps in local time once the page is running. The exact time is in
 * `dateTime` (for scripts) and the tooltip.
 */
export function LocalTime({ iso }: { iso: string }) {
  const [local, setLocal] = useState<string | null>(null);
  useEffect(() => {
    setLocal(new Intl.DateTimeFormat(undefined, { dateStyle: "medium", timeStyle: "medium", hourCycle: "h23" }).format(new Date(iso)));
  }, [iso]);

  return (
    <time dateTime={iso} data-testid="fetched-at" title={iso}>
      {local ?? `${utc.format(new Date(iso))} UTC`}
    </time>
  );
}
