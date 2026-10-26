import { UmbElementMixin } from "@umbraco-cms/backoffice/element-api";
import { css, customElement, html, LitElement, nothing, state } from "@umbraco-cms/backoffice/external/lit";
import { UMB_NOTIFICATION_CONTEXT } from "@umbraco-cms/backoffice/notification";
import { UmbTextStyles } from "@umbraco-cms/backoffice/style";
import { UMB_ENTITY_WORKSPACE_CONTEXT } from "@umbraco-cms/backoffice/workspace";
import { api } from "./api.js";
import { errorMessage, sharedStyles } from "./shared.js";

/**
 * The Cache box on a document's or media item's Info tab: the item's key, which every cached response
 * that shows the item carries, and purging the item by hand.
 */
@customElement("thebuilder-cache-tags-info-app")
export class CacheTagsInfoAppElement extends UmbElementMixin(LitElement) {
  @state() private _key?: string;
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
        this._busy = false;
      });
    });
  }

  async #purge(): Promise<void> {
    const key = this._key;
    if (!key) {
      return;
    }
    this._busy = true;
    try {
      await api.purge([key]);
      this.#notification?.peek("positive", {
        data: { message: "Purge sent to every target. Cached responses that show this item render fresh on their next request." },
      });
    } catch (error) {
      this.#notification?.peek("danger", { data: { message: errorMessage(error, "Could not purge.") } });
    } finally {
      if (this._key === key) {
        this._busy = false;
      }
    }
  }

  render() {
    if (!this._key) {
      return nothing;
    }
    return html`
      <uui-box headline="Cache">
        <p class="muted">Every cached response that shows this item carries its key. Publishing or purging the item purges them.</p>
        <div class="row">
          <span class="mono">${this._key}</span>
          <uui-button compact look="secondary" label="Purge this item" ?disabled=${this._busy} @click=${() => this.#purge()}></uui-button>
        </div>
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
      .row {
        justify-content: space-between;
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
