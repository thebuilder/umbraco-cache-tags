import { UmbElementMixin } from "@umbraco-cms/backoffice/element-api";
import { css, customElement, html, LitElement, nothing, state } from "@umbraco-cms/backoffice/external/lit";
import { UmbTextStyles } from "@umbraco-cms/backoffice/style";
import { api, type KeyLookup, type SearchResult } from "./api.js";
import { describeKey, errorMessage, requestTemplate, sharedStyles, time } from "./shared.js";

/** How long typing pauses before the view searches. */
const SEARCH_DELAY = 250;

/**
 * Finds keys without knowing them exactly, by item name or part of a key, path or query, and shows
 * what a key stands for: a response key's request and keys, or the responses that cover another key.
 */
@customElement("thebuilder-cache-tags-lookup-view")
export class CacheTagsLookupViewElement extends UmbElementMixin(LitElement) {
  @state() private _key = "";
  @state() private _lookup?: KeyLookup | null;
  @state() private _loading = false;
  @state() private _error?: string;
  @state() private _search?: SearchResult;

  #searchTimer?: ReturnType<typeof setTimeout>;
  /** Bumped by every lookup, search and reset, so an answer that arrives late is dropped. */
  #generation = 0;
  #fromUrl = () => this.#show(new URLSearchParams(window.location.search));

  connectedCallback(): void {
    super.connectedCallback();
    // The search or key is in the URL (?q= or ?key=), so Back returns to the results, and the
    // Info tab links straight to a key.
    window.addEventListener("popstate", this.#fromUrl);
    this.#fromUrl();
  }

  disconnectedCallback(): void {
    super.disconnectedCallback();
    window.removeEventListener("popstate", this.#fromUrl);
    clearTimeout(this.#searchTimer);
  }

  #show(params: URLSearchParams): void {
    const key = params.get("key");
    const query = params.get("q");
    if (key) {
      this.#lookUp(key, false);
    } else if (query) {
      clearTimeout(this.#searchTimer);
      this._key = query;
      this._lookup = undefined;
      this.#searchFor(query);
    } else {
      this.#reset();
    }
  }

  /** Puts the search or key in the URL: a new history entry for a key, the same one while typing. */
  #remember(name: "q" | "key" | undefined, value: string, push: boolean): void {
    const url = new URL(window.location.href);
    url.searchParams.delete("q");
    url.searchParams.delete("key");
    if (name) {
      url.searchParams.set(name, value);
    }
    if (url.href !== window.location.href) {
      window.history[push ? "pushState" : "replaceState"](window.history.state, "", url);
    }
  }

  #reset(): void {
    clearTimeout(this.#searchTimer);
    this.#generation++;
    this._loading = false;
    this._key = "";
    this._search = undefined;
    this._lookup = undefined;
    this._error = undefined;
  }

  #clear(): void {
    this.#reset();
    this.#remember(undefined, "", false);
  }

  #typed(text: string): void {
    this._key = text;
    this._lookup = undefined;
    this._loading = false;
    // A lookup or search still loading is for what was there before.
    this.#generation++;
    clearTimeout(this.#searchTimer);
    const query = text.trim();
    if (query.length < 2) {
      this._search = undefined;
      this.#remember(undefined, "", false);
      return;
    }
    this.#searchTimer = setTimeout(() => {
      this.#remember("q", query, false);
      this.#searchFor(query);
    }, SEARCH_DELAY);
  }

  async #searchFor(query: string): Promise<void> {
    const generation = ++this.#generation;
    try {
      const result = await api.search(query);
      // A slower, older search must not replace a newer one's results.
      if (generation === this.#generation) {
        this._search = result;
        this._error = undefined;
      }
    } catch (error) {
      if (generation === this.#generation) {
        this._error = errorMessage(error, "Could not search.");
      }
    }
  }

  async #lookUp(key: string, push = true): Promise<void> {
    clearTimeout(this.#searchTimer);
    const generation = ++this.#generation;
    this._search = undefined;
    this._lookup = undefined;
    this._key = key.trim();
    if (!this._key) {
      return;
    }
    if (push) {
      this.#remember("key", this._key, true);
    }
    this._loading = true;
    this._error = undefined;
    try {
      const lookup = await api.lookup(this._key);
      // Another lookup, a search, Back or Clear may have come since; only the latest counts.
      if (generation === this.#generation) {
        this._lookup = lookup;
      }
    } catch (error) {
      if (generation === this.#generation) {
        this._error = errorMessage(error, "Could not look the key up.");
      }
    } finally {
      if (generation === this.#generation) {
        this._loading = false;
      }
    }
  }

  render() {
    return html`
      <div class="stack">
        <uui-box headline="Find a key">
          <form
            class="row"
            @submit=${(event: Event) => {
              event.preventDefault();
              this.#lookUp(this._key);
            }}>
            <uui-input
              label="Search or key"
              placeholder="Search by name, path or key"
              .value=${this._key}
              @input=${(event: Event) => this.#typed((event.target as HTMLInputElement).value)}>
              <uui-icon slot="prepend" name="icon-search"></uui-icon>
              ${this._key
                ? html`<uui-button slot="append" compact look="placeholder" label="Clear" @click=${() => this.#clear()}>
                    <uui-icon name="icon-wrong"></uui-icon>
                  </uui-button>`
                : nothing}
            </uui-input>
            <uui-button type="submit" look="secondary" label="Look up key" ?disabled=${this._loading}></uui-button>
          </form>
          <p class="hint muted">Results appear as you type. Enter looks up the exact key.</p>
          ${this._error ? html`<p class="error">${this._error}</p>` : nothing}
          ${this._lookup === null ? html`<p>Nothing is recorded under this key: no response with it has been rendered, or it has aged out.</p>` : nothing}
        </uui-box>
        ${this._search ? this.#renderSearch(this._search) : this._lookup ? this.#renderLookup(this._lookup) : nothing}
      </div>
    `;
  }

  #renderLookup(lookup: KeyLookup) {
    return lookup.response ? this.#renderResponse(lookup) : this.#renderCovered(lookup);
  }

  #renderSearch(search: SearchResult) {
    const found = search.items.length + search.keys.length + search.responses.length;
    if (found === 0) {
      return html`<uui-box><p>Nothing matches. Press Enter to look the text up as a whole key.</p></uui-box>`;
    }
    return html`
      ${search.items.length > 0
        ? html`<uui-box headline="Items">
            <uui-table>
              <uui-table-head>
                <uui-table-head-cell>Name</uui-table-head-cell>
                <uui-table-head-cell style="width: 8rem">Kind</uui-table-head-cell>
                <uui-table-head-cell>Key</uui-table-head-cell>
              </uui-table-head>
              ${search.items.map(
                (item) => html`
                  <uui-table-row>
                    <uui-table-cell><button type="button" class="key name" @click=${() => this.#lookUp(item.key)}>${item.name}</button></uui-table-cell>
                    <uui-table-cell>${item.kind === "media" ? "Media" : "Document"}</uui-table-cell>
                    <uui-table-cell class="mono muted">${item.key}</uui-table-cell>
                  </uui-table-row>
                `
              )}
            </uui-table>
          </uui-box>`
        : nothing}
      ${search.keys.length > 0
        ? html`<uui-box headline="Keys">
            <uui-table>
              <uui-table-head>
                <uui-table-head-cell>Key</uui-table-head-cell>
                <uui-table-head-cell style="width: 10rem">Kind</uui-table-head-cell>
                <uui-table-head-cell>Name</uui-table-head-cell>
              </uui-table-head>
              ${search.keys.map(
                (key) => html`
                  <uui-table-row>
                    <uui-table-cell><button type="button" class="key" @click=${() => this.#lookUp(key.key)}>${key.key}</button></uui-table-cell>
                    <uui-table-cell>${describeKey(key.key)}</uui-table-cell>
                    <uui-table-cell>${key.name ?? ""}</uui-table-cell>
                  </uui-table-row>
                `
              )}
            </uui-table>
          </uui-box>`
        : nothing}
      ${search.responses.length > 0
        ? html`<uui-box headline="Responses">
            <uui-table>
              <uui-table-head>
                <uui-table-head-cell>Request</uui-table-head-cell>
                <uui-table-head-cell style="width: 6rem">Culture</uui-table-head-cell>
                <uui-table-head-cell style="width: 12rem">Last served</uui-table-head-cell>
              </uui-table-head>
              ${search.responses.map(
                (response) => html`
                  <uui-table-row>
                    <uui-table-cell>
                      <button type="button" class="open" @click=${() => this.#lookUp(response.key)}>
                        ${requestTemplate(response.path, response.query)}
                      </button>
                    </uui-table-cell>
                    <uui-table-cell>${response.culture || "invariant"}</uui-table-cell>
                    <uui-table-cell>${time(response.seenUtc)}</uui-table-cell>
                  </uui-table-row>
                `
              )}
            </uui-table>
          </uui-box>`
        : nothing}
    `;
  }

  #renderResponse(lookup: KeyLookup) {
    const response = lookup.response!;
    return html`
      <uui-box headline="Response">
        <span slot="header-actions" class="mono muted">${lookup.key}</span>
        <dl>
          <dt>Request</dt>
          <dd>${requestTemplate(response.path, response.query)}</dd>
          <dt>Culture</dt>
          <dd>${response.culture || "invariant"}</dd>
          ${response.segment ? html`<dt>Segment</dt><dd>${response.segment}</dd>` : nothing}
          ${response.startItem ? html`<dt>Start item</dt><dd>${response.startItem}</dd>` : nothing}
          <dt>Rendered</dt>
          <dd>${time(response.renderedUtc)}</dd>
          <dt>Last served</dt>
          <dd>${time(response.seenUtc)}</dd>
        </dl>
      </uui-box>
      <uui-box headline=${`Covers ${response.keys.length} key${response.keys.length === 1 ? "" : "s"}`}>
        <uui-table>
          <uui-table-head>
            <uui-table-head-cell>Key</uui-table-head-cell>
            <uui-table-head-cell>Kind</uui-table-head-cell>
            <uui-table-head-cell>Name</uui-table-head-cell>
          </uui-table-head>
          ${response.keys.map(
            (key) => html`
              <uui-table-row>
                <uui-table-cell><button type="button" class="key" @click=${() => this.#lookUp(key.key)}>${key.key}</button></uui-table-cell>
                <uui-table-cell>${describeKey(key.key)}</uui-table-cell>
                <uui-table-cell>${key.name ?? ""}</uui-table-cell>
              </uui-table-row>
            `
          )}
        </uui-table>
      </uui-box>
    `;
  }

  #renderCovered(lookup: KeyLookup) {
    const headline = `${lookup.name ?? lookup.key}`;
    const count = lookup.responseCount;
    return html`
      <uui-box headline=${headline}>
        <span slot="header-actions" class="muted">${describeKey(lookup.key)}</span>
        <p>
          ${count === 0
            ? "No served response includes this key."
            : `Included in ${count} served response${count === 1 ? "" : "s"}${lookup.responses.length < count ? `. The first ${lookup.responses.length} are shown` : ""}.`}
          Searches and other catch-all listings carry their keys themselves and are not recorded.
        </p>
        ${lookup.responses.length > 0
          ? html`
              <uui-table>
                <uui-table-head>
                  <uui-table-head-cell>Request</uui-table-head-cell>
                  <uui-table-head-cell style="width: 6rem">Culture</uui-table-head-cell>
                  <uui-table-head-cell style="width: 12rem">Last served</uui-table-head-cell>
                </uui-table-head>
                ${lookup.responses.map(
                  (response) => html`
                    <uui-table-row>
                      <uui-table-cell>
                        ${requestTemplate(response.path, response.query)}
                        <button type="button" class="key response" @click=${() => this.#lookUp(response.key)}>${response.key}</button>
                      </uui-table-cell>
                      <uui-table-cell>${response.culture || "invariant"}</uui-table-cell>
                      <uui-table-cell>${time(response.seenUtc)}</uui-table-cell>
                    </uui-table-row>
                  `
                )}
              </uui-table>
            `
          : nothing}
      </uui-box>
    `;
  }

  static styles = [
    UmbTextStyles,
    sharedStyles,
    css`
      form uui-input {
        flex: 1;
      }
      form uui-icon[slot="prepend"] {
        margin-left: var(--uui-size-space-3);
        color: var(--uui-color-text-alt);
      }
      uui-box > p.hint {
        margin-top: var(--uui-size-space-3);
      }
      .key.name {
        font-family: inherit;
        font-size: inherit;
      }
      .response {
        margin-top: var(--uui-size-space-1);
        color: var(--uui-color-text-alt);
      }
      .open {
        display: block;
        width: 100%;
        border: 0;
        background: none;
        padding: 0;
        color: inherit;
        font: inherit;
        text-align: left;
        cursor: pointer;
      }
      .open:hover .path {
        text-decoration: underline;
      }
    `,
  ];
}

export default CacheTagsLookupViewElement;

declare global {
  interface HTMLElementTagNameMap {
    "thebuilder-cache-tags-lookup-view": CacheTagsLookupViewElement;
  }
}
