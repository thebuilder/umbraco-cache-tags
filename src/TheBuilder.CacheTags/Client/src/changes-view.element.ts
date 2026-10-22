import { UmbElementMixin } from "@umbraco-cms/backoffice/element-api";
import { css, customElement, html, LitElement, nothing, state } from "@umbraco-cms/backoffice/external/lit";
import { UMB_NOTIFICATION_CONTEXT } from "@umbraco-cms/backoffice/notification";
import { UmbTextStyles } from "@umbraco-cms/backoffice/style";
import { api, type ChangeLookup, type Purge } from "./api.js";
import { DOCS, errorMessage, sharedStyles, time } from "./shared.js";

const STAGES: Record<ChangeLookup["status"], { label: string; color: string; hint: string }> = {
  Pending: { label: "Waiting for servers", color: "warning", hint: "Waiting for every server to apply it." },
  Indexing: { label: "Waiting for index", color: "warning", hint: "Purged; listings follow once every server's index has it." },
  Released: { label: "Purged", color: "positive", hint: "Every purge for it is queued or sent." },
};

/** How often the view refreshes while a change or purge is still on its way. */
const REFRESH_DELAY = 2000;

const stageOf = (change: ChangeLookup) =>
  change.status === "Pending" && !change.waitsForServers
    ? { label: "Queued", color: "default", hint: "Released on the next pass; it does not wait for servers." }
    : STAGES[change.status];

const plural = (count: number, word: string) => `${count} ${word}${count === 1 ? "" : "s"}`;

/** Recent changes, what each planned and where its purges are, and the purges that failed. */
@customElement("thebuilder-cache-tags-changes-view")
export class CacheTagsChangesViewElement extends UmbElementMixin(LitElement) {
  @state() private _changes: ChangeLookup[] = [];
  @state() private _failed: Purge[] = [];
  @state() private _loading = true;
  @state() private _busy = false;
  @state() private _error?: string;

  #notification?: typeof UMB_NOTIFICATION_CONTEXT.TYPE;
  #refreshTimer?: ReturnType<typeof setTimeout>;

  constructor() {
    super();
    this.consumeContext(UMB_NOTIFICATION_CONTEXT, (context) => {
      this.#notification = context;
    });
  }

  connectedCallback(): void {
    super.connectedCallback();
    this.#load();
  }

  disconnectedCallback(): void {
    super.disconnectedCallback();
    clearTimeout(this.#refreshTimer);
  }

  async #load(): Promise<void> {
    clearTimeout(this.#refreshTimer);
    this._loading = true;
    this._error = undefined;
    try {
      [this._changes, this._failed] = await Promise.all([api.changes(30), api.purges("Dead")]);
    } catch (error) {
      this._error = errorMessage(error, "Could not load the changes.");
    } finally {
      this._loading = false;
    }

    // Follow a change until it is released and its purges are sent.
    const inProgress = this._changes.some((change) => change.status !== "Released" || change.purges.some((purge) => purge.state === "Pending"));
    if (inProgress && !this._error && this.isConnected) {
      this.#refreshTimer = setTimeout(() => this.#load(), REFRESH_DELAY);
    }
  }

  async #act(action: "retry" | "discard", purge: Purge): Promise<void> {
    this._busy = true;
    try {
      await (action === "retry" ? api.retry(purge.id) : api.discard(purge.id));
      this.#notification?.peek("positive", { data: { message: action === "retry" ? "The purge is sent again." : "The purge is set aside." } });
      await this.#load();
    } catch (error) {
      this.#notification?.peek("danger", { data: { message: errorMessage(error, "Could not change the purge.") } });
    } finally {
      this._busy = false;
    }
  }

  render() {
    return html`
      <div class="stack">
        ${this._failed.length > 0
          ? html`
              <uui-box headline="Failed purges">
                <p>These were given up after their last attempt. Send them again once the target answers, or set them aside.</p>
                ${this._failed.map((purge) => this.#renderPurge(purge))}
              </uui-box>
            `
          : nothing}
        <uui-box headline="Recent changes">
          <uui-button slot="header-actions" look="secondary" label="Refresh" ?disabled=${this._loading} @click=${() => this.#load()}></uui-button>
          ${this._error ? html`<p class="error">${this._error}</p>` : nothing}
          ${this._loading && this._changes.length === 0 ? html`<uui-loader></uui-loader>` : nothing}
          ${!this._loading && this._changes.length === 0 && !this._error ? html`<p class="muted">No changes recorded yet.</p>` : nothing}
          ${this._changes.map((change) => this.#renderChange(change))}
        </uui-box>
        <p class="muted">How a change becomes a purge: <a href=${DOCS} target="_blank" rel="noopener">the documentation</a>.</p>
      </div>
    `;
  }

  #renderChange(change: ChangeLookup) {
    const stage = stageOf(change);
    const what = change.change.reason ?? plural(change.change.items.length, "item");
    const planned = change.plan.everything ? ["umbraco"] : change.plan.keys;
    const sent = change.purges.filter((purge) => purge.state === "Sent").length;
    const failed = change.purges.some((purge) => purge.state === "Dead");
    return html`
      <details class="change">
        <summary>
          <span class="when muted">${time(change.createdUtc)}</span>
          <span class="what"><strong>${change.kind}</strong> <span class="muted">${what}</span></span>
          <span class="tags">
            <uui-tag look="secondary" color=${stage.color} title=${stage.hint}>${stage.label}</uui-tag>
            ${change.notAcknowledgedBy.length > 0 ? html`<uui-tag look="secondary" color="danger" title="Some servers had not applied it when it was purged">Ack timeout</uui-tag>` : nothing}
            ${failed ? html`<uui-tag look="secondary" color="danger">Purge failed</uui-tag>` : nothing}
          </span>
          <span class="muted purges">${change.purges.length === 0 ? "no purges" : `${sent} of ${plural(change.purges.length, "purge")} sent`}</span>
        </summary>
        <div class="details">
          <dl>
            <dt>Planned</dt>
            <dd>${planned.map((key) => html`<span class="param">${key}</span>`)}</dd>
            <dt>Reaches</dt>
            <dd>${plural(change.responses.length, "recorded response")}, and every response carrying a planned key</dd>
            ${change.plan.cultures ? html`<dt>Cultures</dt><dd>${change.plan.cultures.join(", ")}</dd>` : nothing}
            <dt>Applied by</dt>
            <dd>
              ${change.waitsForServers
                ? change.acknowledgedBy.join(", ") || html`<span class="muted">no server yet</span>`
                : html`<span class="muted">purged without waiting for servers</span>`}
            </dd>
            ${change.notAcknowledgedBy.length > 0 ? html`<dt>Purged without</dt><dd class="error">${change.notAcknowledgedBy.join(", ")}</dd>` : nothing}
            ${change.indexedBy.length > 0 ? html`<dt>Indexed by</dt><dd>${change.indexedBy.join(", ")}</dd>` : nothing}
            ${change.change.waitFor ? html`<dt>Waits for</dt><dd><span class="param">${change.change.waitFor}</span></dd>` : nothing}
          </dl>
          ${change.purges.length > 0 ? html`<h4>Purges</h4>${change.purges.map((purge) => this.#renderPurge(purge))}` : nothing}
        </div>
      </details>
    `;
  }

  #renderPurge(purge: Purge) {
    const color = purge.state === "Sent" ? "positive" : purge.state === "Dead" ? "danger" : "default";
    return html`
      <div class="purge">
        <uui-tag look="secondary" color=${color}>${purge.state}</uui-tag>
        <span><strong>${purge.target}</strong> <span class="muted">${plural(purge.keys.length, "key")}, ${plural(purge.attempts, "attempt")}${purge.sentUtc ? `, sent ${time(purge.sentUtc)}` : ""}</span></span>
        ${purge.lastError ? html`<span class="error">${purge.lastError}</span>` : nothing}
        <span class="actions">
          ${purge.state === "Dead" || purge.state === "Discarded"
            ? html`<uui-button compact look="secondary" label="Send again" ?disabled=${this._busy} @click=${() => this.#act("retry", purge)}></uui-button>`
            : nothing}
          ${purge.state === "Dead" || purge.state === "Pending"
            ? html`<uui-button compact look="secondary" label="Set aside" ?disabled=${this._busy} @click=${() => this.#act("discard", purge)}></uui-button>`
            : nothing}
        </span>
      </div>
    `;
  }

  static styles = [
    UmbTextStyles,
    sharedStyles,
    css`
      .change {
        border-top: 1px solid var(--uui-color-border);
      }
      .change:first-of-type {
        border-top: 0;
      }
      summary {
        display: grid;
        grid-template-columns: 11rem 1fr auto 9rem;
        gap: var(--uui-size-space-4);
        align-items: center;
        padding: var(--uui-size-space-3) 0;
        cursor: pointer;
      }
      .when,
      .purges {
        font-variant-numeric: tabular-nums;
      }
      .purges {
        text-align: right;
      }
      .tags {
        display: flex;
        gap: var(--uui-size-space-2);
      }
      .details {
        padding: 0 0 var(--uui-size-space-4) 11rem;
      }
      h4 {
        margin: var(--uui-size-space-4) 0 var(--uui-size-space-2);
      }
      .purge {
        display: flex;
        gap: var(--uui-size-space-3);
        align-items: center;
        flex-wrap: wrap;
        padding: var(--uui-size-space-2) 0;
      }
      .actions {
        margin-left: auto;
        display: flex;
        gap: var(--uui-size-space-2);
      }
    `,
  ];
}

export default CacheTagsChangesViewElement;

declare global {
  interface HTMLElementTagNameMap {
    "thebuilder-cache-tags-changes-view": CacheTagsChangesViewElement;
  }
}
