"use client";

import { useEffect } from "react";

/** How often a page that could not reach Umbraco tries again. */
const retrySeconds = 5;

/**
 * Shown when a page could not load its Delivery API responses: usually Umbraco is not running yet,
 * or is still starting. Tries again on its own, so the page fills in once Umbraco answers.
 */
export default function UmbracoUnavailable({ error, retry }: { error: unknown; retry: () => void }) {
  useEffect(() => {
    const timer = setTimeout(retry, retrySeconds * 1000);
    return () => clearTimeout(timer);
  }, [error, retry]);

  return (
    <section className="unavailable">
      <h1>Waiting for Umbraco</h1>
      <p>
        This page could not load its content from Umbraco. If Umbraco is starting, it shows up here on its own:
        this page tries again every {retrySeconds} seconds.
      </p>
      <button type="button" onClick={retry}>
        Try again now
      </button>
    </section>
  );
}
