import { css, html, nothing } from "@umbraco-cms/backoffice/external/lit";

/** Settings > Advanced > Cache tags > Look up, showing a key. */
export const lookupHref = (key: string) =>
  `/umbraco/section/settings/workspace/thebuilder-cache-tags-root/view/lookup?key=${encodeURIComponent(key)}`;

/** Where the documentation explains keys, changes and purges. */
export const DOCS = "https://github.com/thebuilder/umbraco-cache-tags/blob/main/docs/design.md";

/** A time in the reader's locale and time zone, on a 24-hour clock. The API sends UTC, sometimes without a zone. */
export const time = (utc: string | null | undefined) =>
  utc
    ? new Intl.DateTimeFormat(undefined, { dateStyle: "medium", timeStyle: "medium", hourCycle: "h23" }).format(
        new Date(/[zZ]|[+-]\d\d:\d\d$/.test(utc) ? utc : `${utc}Z`)
      )
    : "";

export const errorMessage = (error: unknown, fallback: string) =>
  // fetch rejects with a TypeError when the server cannot be reached, worded differently by each browser.
  error instanceof TypeError ? "Could not reach Umbraco. Is it running?" : error instanceof Error ? error.message : fallback;

const DELIVERY_API = "/umbraco/delivery/api/v2/";

/** A recorded request, for reading: the Delivery API path without its prefix, and the query's parameters decoded. */
const describeRequest = (path: string, query: string) => {
  const delivery = path.startsWith(DELIVERY_API);
  const parameters = query
    ? query.split("&").map((pair) => {
        const [name, value = ""] = pair.split("=");
        const decode = (text: string) => {
          try {
            return decodeURIComponent(text);
          } catch {
            return text;
          }
        };
        return { name: decode(name ?? ""), value: decode(value) };
      })
    : [];
  return { delivery, path: delivery ? path.slice(DELIVERY_API.length) || "/" : path, parameters };
};

/** A recorded request: its path on one line, and its parameters as chips that wrap underneath. */
export const requestTemplate = (path: string, query: string) => {
  const request = describeRequest(path, query);
  return html`<div class="request">
    <div class="mono path">${request.path}</div>
    ${request.parameters.length > 0
      ? html`<div class="params">
          ${request.parameters.map((parameter) => html`<span class="param"><b>${parameter.name}</b>${parameter.value}</span>`)}
        </div>`
      : nothing}
  </div>`;
};

/** What a key names, from its prefix. */
export const describeKey = (key: string) => {
  const bare = key.replace(/^[a-z0-9-]*?(?=(response|content|media|type|children|search|schema|x)-|umbraco$)/, "");
  const kinds: [string, string][] = [
    ["response-", "Response"],
    ["content-", "Document"],
    ["media-", "Media"],
    ["type-", "Listings of type"],
    ["children-", "Children of"],
    ["search-", "Other listings"],
    ["schema-", "Renders type"],
    ["x-", "Custom"],
  ];
  if (bare === "umbraco") {
    return "Everything";
  }
  return kinds.find(([prefix]) => bare.startsWith(prefix))?.[1] ?? "Key";
};

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
  .key {
    border: 0;
    background: none;
    padding: 0;
    color: var(--uui-color-interactive);
    cursor: pointer;
    font-family: var(--uui-font-monospace, monospace);
    font-size: 0.85em;
    text-align: left;
  }
  .key:hover {
    text-decoration: underline;
  }
  .request .path {
    font-weight: 600;
    overflow-wrap: anywhere;
  }
  .params {
    display: flex;
    flex-wrap: wrap;
    gap: var(--uui-size-space-1);
    margin-top: var(--uui-size-space-1);
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
  .param b {
    font-weight: 600;
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
  uui-table-cell {
    vertical-align: top;
  }
`;
