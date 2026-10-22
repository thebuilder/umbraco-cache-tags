import { UmbElementMixin } from "@umbraco-cms/backoffice/element-api";
import { css, customElement, html, LitElement, state } from "@umbraco-cms/backoffice/external/lit";
import { umbConfirmModal } from "@umbraco-cms/backoffice/modal";
import { UMB_NOTIFICATION_CONTEXT } from "@umbraco-cms/backoffice/notification";
import { UmbTextStyles } from "@umbraco-cms/backoffice/style";
import { api } from "./api.js";
import { errorMessage, sharedStyles } from "./shared.js";

/** Purging by hand: keys at every target at once, or everything. */
@customElement("thebuilder-cache-tags-purge-view")
export class CacheTagsPurgeViewElement extends UmbElementMixin(LitElement) {
  @state() private _keys = "";
  @state() private _busy = false;

  #notification?: typeof UMB_NOTIFICATION_CONTEXT.TYPE;

  constructor() {
    super();
    this.consumeContext(UMB_NOTIFICATION_CONTEXT, (context) => {
      this.#notification = context;
    });
  }

  get #keys() {
    return [...new Set(this._keys.split(/[\s,]+/).map((key) => key.trim()).filter(Boolean))];
  }

  async #purge(keys: string[]): Promise<void> {
    this._busy = true;
    try {
      await api.purge(keys);
      this.#notification?.peek("positive", {
        data: { message: keys.includes("umbraco") ? "Everything is being purged." : `Purging ${keys.length} key${keys.length === 1 ? "" : "s"}. See Changes.` },
      });
      this._keys = "";
    } catch (error) {
      this.#notification?.peek("danger", { data: { message: errorMessage(error, "Could not purge.") } });
    } finally {
      this._busy = false;
    }
  }

  async #purgeEverything(): Promise<void> {
    try {
      await umbConfirmModal(this, {
        headline: "Purge everything?",
        content: "Every cached response at every target is purged, and fetched again from Umbraco as visitors ask for it.",
        color: "danger",
        confirmLabel: "Purge everything",
      });
    } catch {
      return;
    }
    await this.#purge(["umbraco"]);
  }

  render() {
    const keys = this.#keys;
    return html`
      <div class="stack">
        <uui-box headline="Purge keys">
          <p>
            Purges at every target at once, without waiting for servers. Use your own <span class="mono">x-</span> keys, or the
            <span class="mono">content-</span> and <span class="mono">media-</span> keys of items. One per line.
          </p>
          <uui-textarea
            label="Keys to purge"
            placeholder="x-dictionary-en-us"
            .value=${this._keys}
            @input=${(event: Event) => {
              this._keys = (event.target as HTMLTextAreaElement).value;
            }}></uui-textarea>
          <div class="row">
            <uui-button look="primary" label=${keys.length > 1 ? `Purge ${keys.length} keys` : "Purge"} ?disabled=${this._busy || keys.length === 0} @click=${() => this.#purge(keys)}></uui-button>
          </div>
        </uui-box>
        <uui-box headline="Purge everything">
          <p>For when something changed that no key covers. Every cached response is fetched again, so expect more load on Umbraco for a while.</p>
          <uui-button look="primary" color="danger" label="Purge everything" ?disabled=${this._busy} @click=${() => this.#purgeEverything()}></uui-button>
        </uui-box>
      </div>
    `;
  }

  static styles = [
    UmbTextStyles,
    sharedStyles,
    css`
      uui-textarea {
        width: 100%;
        --uui-textarea-min-height: 8rem;
      }
      .row {
        margin-top: var(--uui-size-space-4);
      }
    `,
  ];
}

export default CacheTagsPurgeViewElement;

declare global {
  interface HTMLElementTagNameMap {
    "thebuilder-cache-tags-purge-view": CacheTagsPurgeViewElement;
  }
}
