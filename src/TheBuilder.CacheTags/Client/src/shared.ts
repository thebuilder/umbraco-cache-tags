import { css } from "@umbraco-cms/backoffice/external/lit";

/** Where the documentation explains how a change is timed and purged. */
export const DOCS = "https://cache-tags.thebuilder.dk/timing";

/** A time from the API, which sends UTC, sometimes without a zone. */
export const parseUtc = (utc: string) => new Date(/[zZ]|[+-]\d\d:\d\d$/.test(utc) ? utc : `${utc}Z`);

/** A time in the reader's locale and time zone, on a 24-hour clock. */
export const time = (utc: string | null | undefined) =>
  utc
    ? new Intl.DateTimeFormat(undefined, { dateStyle: "medium", timeStyle: "medium", hourCycle: "h23" }).format(parseUtc(utc))
    : "";

export const errorMessage = (error: unknown, fallback: string) =>
  // fetch rejects with a TypeError when the server cannot be reached, worded differently by each browser.
  error instanceof TypeError ? "Could not reach Umbraco. Is it running?" : error instanceof Error ? error.message : fallback;

export const sharedStyles = css`
  :host {
    display: block;
    padding: var(--uui-size-layout-1);
  }
  .stack {
    display: grid;
    gap: var(--uui-size-layout-1);
  }
  .row {
    display: flex;
    gap: var(--uui-size-space-3);
    align-items: center;
    flex-wrap: wrap;
  }
  /* The box already pads its content. */
  uui-box > p:first-of-type {
    margin-top: 0;
  }
  uui-box > p:last-child {
    margin-bottom: 0;
  }
  .muted {
    color: var(--uui-color-text-alt);
  }
  .error {
    color: var(--uui-color-danger);
  }
  .mono {
    font-family: var(--uui-font-monospace, monospace);
    font-size: 0.85em;
  }
  .param {
    display: inline-flex;
    gap: var(--uui-size-space-1);
    max-width: 100%;
    overflow-wrap: anywhere;
    padding: 0 var(--uui-size-space-2);
    border-radius: var(--uui-border-radius);
    background: var(--uui-color-surface-alt);
    font-family: var(--uui-font-monospace, monospace);
    font-size: 0.85em;
  }
  dl {
    display: grid;
    grid-template-columns: max-content 1fr;
    gap: var(--uui-size-space-2) var(--uui-size-space-5);
    margin: 0;
  }
  dt {
    color: var(--uui-color-text-alt);
  }
  dd {
    margin: 0;
  }
  /* A table inside a box: rows divided by lines, no frame of its own. */
  uui-box uui-table {
    border: 0;
    border-radius: 0;
    background: none;
    --uui-table-cell-height: auto;
    --uui-table-cell-padding: var(--uui-size-space-3) var(--uui-size-space-4) var(--uui-size-space-3) 0;
  }
  uui-table-head-cell {
    color: var(--uui-color-text-alt);
    font-weight: 600;
  }
  uui-table-cell {
    vertical-align: top;
  }
`;
