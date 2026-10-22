import { UmbElementMixin } from "@umbraco-cms/backoffice/element-api";
import { css, customElement, html, LitElement, nothing, state } from "@umbraco-cms/backoffice/external/lit";
import { UMB_NOTIFICATION_CONTEXT } from "@umbraco-cms/backoffice/notification";
import { UmbTextStyles } from "@umbraco-cms/backoffice/style";
import { UMB_ENTITY_WORKSPACE_CONTEXT } from "@umbraco-cms/backoffice/workspace";
import { api, type KeyLookup } from "./api.js";
import { errorMessage, lookupHref, requestTemplate, sharedStyles } from "./shared.js";

/** How many of the responses including the item the box lists before linking to the rest. */
const SHOWN = 5;

/**
 * The Cache box on a document's or media item's Info tab: which served responses include the item,
 * a link to all of them under Settings > Advanced > Cache tags, and purging the item by hand.
 */
@customElement("thebuilder-cache-tags-info-app")
export class CacheTagsInfoAppElement extends UmbElementMixin(LitElement) {
  @state() private _key?: string;
  @state() private _lookup?: KeyLookup | null;
  @state() private _error?: string;
  @state() private _busy = false;

  #notification?: typeof UMB_NOTIFICATION_CONTEXT.TYPE;

  constructor() {
    super();
    this.consumeContext(UMB_NOTIFICATION_CONTEXT, (context) => {
      this.#notification = context;
    });
    this.consumeContext(UMB_ENTITY_WORKSPACE_CONTEXT, (context) => {
      if (!context) {
        return;
      }
      const prefix = context.getEntityType() === "media" ? "media-" : "content-";
      this.observe(context.unique, (unique) => {
        this._key = unique ? `${prefix}${unique}` : undefined;
        this.#load();
      });
    });
  }

  async #load(): Promise<void> {
    this._lookup = undefined;
    this._error = undefined;
    if (!this._key) {
      return;
    }
    try {
      this._lookup = await api.lookup(this._key);
    } catch (error) {
      this._error = errorMessage(error, "Could not read the cache.");
    }
  }

  async #purge(): Promise<void> {
    if (!this._key) {
      return;
    }
    this._busy = true;
    try {
      await api.purge([this._key]);
      const count = this._lookup?.responseCount ?? 0;
      this.#notification?.peek("positive", {
        data: {
          message:
            count === 0
              ? "Purge sent to every target."
              : `Purge sent to every target. The ${count} response${count === 1 ? "" : "s"} below render fresh on their next request.`,
        },
      });
    } catch (error) {
      this.#notification?.peek("danger", { data: { message: errorMessage(error, "Could not purge.") } });
    } finally {
      this._busy = false;
    }
  }

  render() {
    if (!this._key) {
      return nothing;
    }
    const count = this._lookup?.responseCount ?? 0;
    return html`
      <uui-box headline="Cache">
        <a slot="header-actions" href=${lookupHref(this._key)}>Show in Cache tags</a>
        ${this._error ? html`<p class="error">${this._error}</p>` : nothing}
        ${this._lookup === undefined && !this._error ? html`<uui-loader-bar></uui-loader-bar>` : nothing}
        ${this._lookup !== undefined
          ? html`
              <p>
                ${count === 0
                  ? "No served response includes this item yet."
                  : `Included in ${count} served response${count === 1 ? "" : "s"}.`}
                <span class="muted">Publishing or purging it drops them from the caches until they are next requested.</span>
              </p>
              ${count > 0
                ? html`<ul>
                    ${this._lookup?.responses.slice(0, SHOWN).map((response) => html`<li>${requestTemplate(response.path, response.query)}</li>`)}
                  </ul>
                  ${count > SHOWN ? html`<a href=${lookupHref(this._key)}>and ${count - SHOWN} more</a>` : nothing}`
                : nothing}
              <div class="row">
                <span class="mono muted">${this._key}</span>
                <uui-button compact look="secondary" label="Purge this item" ?disabled=${this._busy} @click=${() => this.#purge()}></uui-button>
              </div>
            `
          : nothing}
      </uui-box>
    `;
  }

  static styles = [
    UmbTextStyles,
    sharedStyles,
    css`
      :host {
        padding: 0;
      }
      ul {
        margin: 0 0 var(--uui-size-space-3);
        padding: 0;
        list-style: none;
      }
      li {
        padding: var(--uui-size-space-2) 0;
        border-bottom: 1px solid var(--uui-color-divider);
      }
      li:last-child {
        border-bottom: 0;
      }
      .row {
        justify-content: space-between;
        margin-top: var(--uui-size-space-3);
      }
    `,
  ];
}

export default CacheTagsInfoAppElement;

declare global {
  interface HTMLElementTagNameMap {
    "thebuilder-cache-tags-info-app": CacheTagsInfoAppElement;
  }
}
