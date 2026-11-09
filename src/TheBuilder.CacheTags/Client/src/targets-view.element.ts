import { UmbElementMixin } from "@umbraco-cms/backoffice/element-api";
import { css, customElement, html, LitElement, nothing, state } from "@umbraco-cms/backoffice/external/lit";
import { umbConfirmModal } from "@umbraco-cms/backoffice/modal";
import { UMB_NOTIFICATION_CONTEXT } from "@umbraco-cms/backoffice/notification";
import { UmbTextStyles } from "@umbraco-cms/backoffice/style";
import "@umbraco-cms/backoffice/document";
import { api, type NamedValue, type TargetDraft, type TargetValue, type TargetView } from "./api.js";
import { errorMessage, sharedStyles } from "./shared.js";

const DOCS = "https://cache-tags.thebuilder.dk/targets";

const newTarget = (): TargetDraft => ({
  name: "",
  type: "webhook",
  enabled: true,
  url: "",
  secret: { value: "" },
  headers: [],
  settings: [],
  roots: [],
});

/** The settings a Cloudflare target needs, in the order the form shows them. */
const CLOUDFLARE_SETTINGS = ["ZoneId", "ApiToken"];

/** Where a target's named secrets are read from. */
const SECRETS = "CacheTags:Secrets";

/** Whether a value names a secret rather than holding a typed value. */
const fromSetting = (value: TargetValue | null | undefined) => value?.setting !== undefined && value?.setting !== null;

const describe = (target: TargetView) =>
  target.type === "webhook"
    ? target.url
    : target.type === "cloudflare"
      ? "Cloudflare zone"
      : `${target.settings.length} setting${target.settings.length === 1 ? "" : "s"}`;

/**
 * Where purges go: the targets in configuration, read-only here, and those kept in the backoffice,
 * which this view adds, edits, tests and removes.
 */
@customElement("thebuilder-cache-tags-targets-view")
export class CacheTagsTargetsViewElement extends UmbElementMixin(LitElement) {
  @state() private _types: string[] = [];
  @state() private _targets: TargetView[] = [];
  @state() private _loading = true;
  @state() private _error?: string;
  /** The target being edited, or null while the list shows. */
  @state() private _draft: TargetDraft | null = null;
  /** Whether the draft is a target that exists, whose name is fixed. */
  @state() private _editing = false;
  /** A configured target whose roots are being edited, or null. */
  @state() private _rootsOf: TargetView | null = null;
  @state() private _roots: string[] = [];
  @state() private _busy = false;
  @state() private _formError?: string;

  #notification?: typeof UMB_NOTIFICATION_CONTEXT.TYPE;

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

  async #load(): Promise<void> {
    this._loading = true;
    this._error = undefined;
    try {
      ({ types: this._types, targets: this._targets } = await api.targets());
    } catch (error) {
      this._error = errorMessage(error, "Could not load the targets.");
    } finally {
      this._loading = false;
    }
  }

  #open(target: TargetView | null): void {
    this._formError = undefined;
    this._editing = target !== null;
    this._draft = target
      ? {
          name: target.name,
          type: target.type,
          enabled: target.enabled,
          url: target.url,
          secret: target.secret ?? { value: "" },
          headers: target.headers.map((header) => ({ ...header })),
          settings: target.settings.map((setting) => ({ ...setting })),
          roots: [...target.roots],
        }
      : newTarget();
  }

  #patch(patch: Partial<TargetDraft>): void {
    if (this._draft) {
      this._draft = { ...this._draft, ...patch };
      this._formError = undefined;
    }
  }

  /** The draft as the API takes it: only what its type uses. */
  get #request(): TargetDraft {
    const draft = this._draft!;
    const type = draft.type;
    return {
      ...draft,
      url: type === "webhook" ? draft.url : "",
      secret: type === "webhook" ? draft.secret : null,
      headers: type === "webhook" ? draft.headers : type === "cloudflare" ? [] : draft.headers,
      settings:
        type === "webhook"
          ? []
          : type === "cloudflare"
            ? CLOUDFLARE_SETTINGS.map((name) => draft.settings.find((setting) => setting.name === name) ?? { name, value: "" })
            : draft.settings,
    };
  }

  async #save(): Promise<void> {
    this._busy = true;
    try {
      await (this._editing ? api.updateTarget(this.#request) : api.addTarget(this.#request));
      this.#notification?.peek("positive", { data: { message: `Saved ${this._draft!.name}.` } });
      this._draft = null;
      await this.#load();
    } catch (error) {
      this._formError = errorMessage(error, "Could not save the target.");
    } finally {
      this._busy = false;
    }
  }

  #openRoots(target: TargetView): void {
    this._formError = undefined;
    this._rootsOf = target;
    this._roots = [...target.roots];
  }

  async #saveRoots(): Promise<void> {
    const name = this._rootsOf!.name;
    this._busy = true;
    try {
      await api.setTargetRoots(name, this._roots);
      this.#notification?.peek("positive", { data: { message: `Saved the roots of ${name}.` } });
      this._rootsOf = null;
      await this.#load();
    } catch (error) {
      this._formError = errorMessage(error, "Could not save the roots.");
    } finally {
      this._busy = false;
    }
  }

  async #test(target: TargetDraft): Promise<void> {
    this._busy = true;
    try {
      const result = await api.testTarget(target);
      if (result.success) {
        this.#notification?.peek("positive", { data: { headline: "Test purge accepted", message: `${target.name || "The target"} answered the test purge.` } });
      } else {
        this.#notification?.peek("danger", { data: { headline: "Test purge failed", message: result.error ?? "The target did not accept it." } });
      }
    } catch (error) {
      this.#notification?.peek("danger", { data: { message: errorMessage(error, "Could not test the target.") } });
    } finally {
      this._busy = false;
    }
  }

  async #delete(): Promise<void> {
    const name = this._draft!.name;
    try {
      await umbConfirmModal(this, {
        headline: `Delete ${name}?`,
        content: "Purges stop going to it, and its unsent purges are set aside.",
        color: "danger",
        confirmLabel: "Delete",
      });
    } catch {
      return;
    }
    this._busy = true;
    try {
      await api.deleteTarget(name);
      this.#notification?.peek("positive", { data: { message: `Deleted ${name}.` } });
      this._draft = null;
      await this.#load();
    } catch (error) {
      this._formError = errorMessage(error, "Could not delete the target.");
    } finally {
      this._busy = false;
    }
  }

  render() {
    return html`<div class="stack">
      ${this._draft ? this.#renderEditor(this._draft) : this._rootsOf ? this.#renderRootsEditor(this._rootsOf) : this.#renderList()}
    </div>`;
  }

  #renderList() {
    return html`
      <uui-box headline="Targets">
        <uui-button slot="header-actions" look="primary" label="Add target" @click=${() => this.#open(null)}></uui-button>
        <p class="muted">
          Where purges go once every server has applied a change. Targets in <span class="mono">appsettings.json</span> show here
          too, read-only. <a href=${DOCS} target="_blank" rel="noopener">How targets work</a>.
        </p>
        ${this._error ? html`<p class="error">${this._error}</p>` : nothing}
        ${this._loading && this._targets.length === 0 ? html`<uui-loader></uui-loader>` : nothing}
        ${!this._loading && this._targets.length === 0 && !this._error
          ? html`<p>No targets yet, so nothing is purged.</p>`
          : nothing}
        ${this._targets.length > 0
          ? html`
              <uui-table>
                <uui-table-head>
                  <uui-table-head-cell>Name</uui-table-head-cell>
                  <uui-table-head-cell>Sends to</uui-table-head-cell>
                  <uui-table-head-cell style="width: 14rem">Status</uui-table-head-cell>
                  <uui-table-head-cell style="width: 11rem"></uui-table-head-cell>
                </uui-table-head>
                ${this._targets.map((target) => this.#renderRow(target))}
              </uui-table>
            `
          : nothing}
      </uui-box>
    `;
  }

  #renderRow(target: TargetView) {
    return html`
      <uui-table-row>
        <uui-table-cell>
          <strong>${target.name}</strong>
          <div class="muted">${target.type}</div>
        </uui-table-cell>
        <uui-table-cell>
          <span class="mono wrap">${describe(target)}</span>
          ${target.problem ? html`<div class="error">${target.problem}</div>` : nothing}
        </uui-table-cell>
        <uui-table-cell>
          <div class="tags">
            <uui-tag look="secondary" color=${target.enabled ? "positive" : "default"}>${target.enabled ? "Enabled" : "Disabled"}</uui-tag>
            ${target.fromConfiguration ? html`<uui-tag look="secondary" title="Set in appsettings.json; change it there">appsettings</uui-tag>` : nothing}
            ${target.roots.length > 0
              ? html`<uui-tag look="secondary" title="Sent only changes to content under these site roots">${target.roots.length} ${target.roots.length === 1 ? "root" : "roots"}</uui-tag>`
              : nothing}
            ${target.problem ? html`<uui-tag look="secondary" color="danger">Problem</uui-tag>` : nothing}
          </div>
        </uui-table-cell>
        <uui-table-cell>
          <div class="actions">
            ${target.fromConfiguration
              ? target.rootsFromConfiguration
                ? nothing
                : html`<uui-button compact look="secondary" label="Edit roots" @click=${() => this.#openRoots(target)}></uui-button>`
              : html`<uui-button compact look="secondary" label="Edit" @click=${() => this.#open(target)}></uui-button>`}
            ${target.fromConfiguration
              ? nothing
              : html`<uui-button compact look="secondary" label="Test" ?disabled=${this._busy} @click=${() => this.#test(target)}></uui-button>`}
          </div>
        </uui-table-cell>
      </uui-table-row>
    `;
  }

  #renderEditor(draft: TargetDraft) {
    const types = this._types.length > 0 ? this._types : ["webhook", "cloudflare"];
    return html`
      <uui-box headline=${this._editing ? draft.name : "New target"}>
        <fieldset ?disabled=${this._busy}>
          ${this._editing
            ? nothing
            : html`
                <uui-form-layout-item>
                  <uui-label slot="label" for="name" required>Name</uui-label>
                  <span slot="description">Lowercase letters, digits and hyphens. Purges are queued under it, so it cannot change later.</span>
                  <uui-input
                    id="name"
                    label="Name"
                    placeholder="vercel"
                    maxlength="100"
                    .value=${draft.name}
                    @input=${(event: Event) => this.#patch({ name: (event.target as HTMLInputElement).value })}></uui-input>
                </uui-form-layout-item>
                <uui-form-layout-item>
                  <uui-label slot="label" for="type">Type</uui-label>
                  <uui-select
                    id="type"
                    label="Type"
                    .options=${types.map((type) => ({ name: this.#typeName(type), value: type, selected: type === draft.type }))}
                    @change=${(event: Event) => this.#patch({ type: (event.target as HTMLSelectElement).value })}></uui-select>
                </uui-form-layout-item>
              `}
          <uui-form-layout-item>
            <uui-label slot="label" for="enabled">Enabled</uui-label>
            <uui-toggle id="enabled" label="Send purges to this target" .checked=${draft.enabled} @change=${(event: Event) => this.#patch({ enabled: (event.target as HTMLInputElement).checked })}></uui-toggle>
          </uui-form-layout-item>
          ${draft.type === "webhook" ? this.#renderWebhook(draft) : draft.type === "cloudflare" ? this.#renderCloudflare(draft) : this.#renderSettings(draft)}
          ${this.#renderRoots(draft.roots, (roots) => this.#patch({ roots }))}
          ${this._formError ? html`<p class="error">${this._formError}</p>` : nothing}
          <div class="row buttons">
            <uui-button look="primary" label="Save" @click=${() => this.#save()}></uui-button>
            <uui-button look="secondary" label="Send test purge" @click=${() => this.#test(this.#request)}></uui-button>
            <uui-button look="secondary" label="Cancel" @click=${() => (this._draft = null)}></uui-button>
            ${this._editing ? html`<uui-button class="delete" look="secondary" color="danger" label="Delete" @click=${() => this.#delete()}></uui-button>` : nothing}
          </div>
        </fieldset>
      </uui-box>
    `;
  }

  /** The roots of a target from appsettings.json, the one thing about it the backoffice can change. */
  #renderRootsEditor(target: TargetView) {
    return html`
      <uui-box headline=${target.name}>
        <fieldset ?disabled=${this._busy}>
          <p class="muted">
            Set in <span class="mono">appsettings.json</span>. Roots picked here are saved in the database.
          </p>
          ${this.#renderRoots(this._roots, (roots) => {
            this._roots = roots;
            this._formError = undefined;
          })}
          ${this._formError ? html`<p class="error">${this._formError}</p>` : nothing}
          <div class="row buttons">
            <uui-button look="primary" label="Save" @click=${() => this.#saveRoots()}></uui-button>
            <uui-button look="secondary" label="Cancel" @click=${() => (this._rootsOf = null)}></uui-button>
          </div>
        </fieldset>
      </uui-box>
    `;
  }

  #renderRoots(roots: string[], change: (roots: string[]) => void) {
    return html`
      <uui-form-layout-item>
        <uui-label slot="label" for="roots">Site roots</uui-label>
        <span slot="description">
          Only send changes under these roots. Include shared roots such as settings or a footer. Leave empty to send every change.
        </span>
        <umb-input-document
          id="roots"
          .selection=${roots}
          @change=${(event: Event) => change([...(event.target as HTMLElement & { selection: string[] }).selection])}></umb-input-document>
      </uui-form-layout-item>
    `;
  }

  #typeName(type: string) {
    return type === "webhook" ? "Webhook" : type === "cloudflare" ? "Cloudflare" : type;
  }

  #renderWebhook(draft: TargetDraft) {
    return html`
      <uui-form-layout-item>
        <uui-label slot="label" for="url" required>URL</uui-label>
        <span slot="description">Where purges are posted. https, except on localhost.</span>
        <uui-input
          id="url"
          label="URL"
          type="url"
          placeholder="https://www.example.com/api/cache-tags"
          .value=${draft.url}
          @input=${(event: Event) => this.#patch({ url: (event.target as HTMLInputElement).value })}></uui-input>
      </uui-form-layout-item>
      <uui-form-layout-item>
        <uui-label slot="label" for="secret" required>Signing secret</uui-label>
        <span slot="description">At least 32 characters, the same as the receiver checks with.</span>
        ${this.#renderValue("secret", "Signing secret", draft.secret ?? { value: "" }, (secret) => this.#patch({ secret }), "VercelSecret")}
      </uui-form-layout-item>
      ${this.#renderNamedValues("Request headers", "Sent with every purge, such as a gateway's API key or x-vercel-protection-bypass.", draft.headers, (headers) => this.#patch({ headers }), "x-vercel-protection-bypass", "Add header")}
      <p class="muted small">
        A value typed here is stored in plain text. Keep secrets under <span class="mono">${SECRETS}</span> in configuration instead.
      </p>
    `;
  }

  #renderCloudflare(draft: TargetDraft) {
    const setting = (name: string) => draft.settings.find((item) => item.name === name) ?? { name, value: "" };
    const update = (name: string, value: TargetValue) =>
      this.#patch({ settings: [...CLOUDFLARE_SETTINGS.map(setting).filter((item) => item.name !== name), { name, ...value }] });
    return html`
      <uui-form-layout-item>
        <uui-label slot="label" for="zone" required>Zone ID</uui-label>
        <span slot="description">On the zone's Overview page in Cloudflare.</span>
        ${this.#renderValue("zone", "Zone ID", setting("ZoneId"), (value) => update("ZoneId", value), "CloudflareZone")}
      </uui-form-layout-item>
      <uui-form-layout-item>
        <uui-label slot="label" for="token" required>API token</uui-label>
        <span slot="description">A token with the Zone, Cache Purge permission for this zone.</span>
        ${this.#renderValue("token", "API token", setting("ApiToken"), (value) => update("ApiToken", value), "CloudflareToken")}
      </uui-form-layout-item>
      <p class="muted small">
        A token typed here is stored in plain text. Keep it under <span class="mono">${SECRETS}</span> in configuration instead.
      </p>
    `;
  }

  #renderSettings(draft: TargetDraft) {
    return this.#renderNamedValues("Settings", "What this target type reads, by name.", draft.settings, (settings) => this.#patch({ settings }), "ApiToken", "Add setting");
  }

  /** One value: typed in, or the name of the secret under CacheTags:Secrets that holds it, with a link to switch. */
  #renderValue(id: string, label: string, value: TargetValue, change: (value: TargetValue) => void, settingPlaceholder: string) {
    const setting = fromSetting(value);
    return html`
      <div class="value">
        ${setting
          ? html`<uui-input
              id=${id}
              label=${`${label} secret name`}
              placeholder=${settingPlaceholder}
              maxlength="200"
              .value=${value.setting ?? ""}
              @input=${(event: Event) => change({ setting: (event.target as HTMLInputElement).value })}>
              <span slot="prepend" class="prefix mono">${SECRETS}:</span>
            </uui-input>`
          : html`<uui-input
              id=${id}
              label=${label}
              maxlength="1000"
              .value=${value.value ?? ""}
              @input=${(event: Event) => change({ value: (event.target as HTMLInputElement).value })}></uui-input>`}
        <button type="button" class="switch" @click=${() => change(setting ? { value: "" } : { setting: "" })}>
          ${setting ? "Type the value instead" : `Read it from ${SECRETS} instead`}
        </button>
      </div>
    `;
  }

  #renderNamedValues(
    title: string,
    description: string,
    items: NamedValue[],
    change: (items: NamedValue[]) => void,
    namePlaceholder: string,
    addLabel: string
  ) {
    const update = (index: number, item: NamedValue) => change(items.map((existing, i) => (i === index ? item : existing)));
    return html`
      <section class="named">
        <h4>${title}</h4>
        <p class="muted small">${description}</p>
        ${items.map(
          (item, index) => html`
            <div class="named-row">
              <uui-input
                label="Name"
                placeholder=${namePlaceholder}
                maxlength="100"
                .value=${item.name}
                @input=${(event: Event) => update(index, { ...item, name: (event.target as HTMLInputElement).value })}></uui-input>
              ${this.#renderValue(`${title}-${index}`, item.name || "Value", item, (value) => update(index, { name: item.name, ...value }), "GatewayKey")}
              <uui-button compact look="secondary" label="Remove" @click=${() => change(items.filter((_, i) => i !== index))}>
                <uui-icon name="icon-trash"></uui-icon>
              </uui-button>
            </div>
          `
        )}
        <uui-button look="placeholder" label=${addLabel} @click=${() => change([...items, { name: "", value: "" }])}></uui-button>
      </section>
    `;
  }

  static styles = [
    UmbTextStyles,
    sharedStyles,
    css`
      fieldset {
        border: 0;
        margin: 0;
        padding: 0;
        min-width: 0;
      }
      uui-input,
      uui-select {
        width: 100%;
      }
      /* Match Umbraco's own property descriptions, which are darker than the form layout item's default. */
      uui-form-layout-item [slot="description"] {
        color: var(--uui-color-text-alt);
      }
      .tags,
      .actions {
        display: flex;
        flex-wrap: wrap;
        gap: var(--uui-size-space-2);
      }
      .wrap {
        overflow-wrap: anywhere;
      }
      .small {
        font-size: 0.9em;
      }
      .value {
        display: grid;
        gap: var(--uui-size-space-1);
        min-width: 0;
      }
      .prefix {
        padding: 0 var(--uui-size-space-3);
        color: var(--uui-color-text-alt);
        white-space: nowrap;
      }
      .switch {
        justify-self: start;
        border: 0;
        background: none;
        padding: 0;
        color: var(--uui-color-interactive);
        cursor: pointer;
        font: inherit;
        font-size: 0.85em;
      }
      .switch:hover {
        text-decoration: underline;
      }
      .named {
        margin: var(--uui-size-space-5) 0;
      }
      .named h4 {
        margin: 0;
      }
      .named-row {
        display: grid;
        grid-template-columns: minmax(10rem, 1fr) minmax(12rem, 2fr) auto;
        gap: var(--uui-size-space-3);
        align-items: start;
        margin-bottom: var(--uui-size-space-3);
      }
      .buttons {
        margin-top: var(--uui-size-space-5);
      }
      .buttons .delete {
        margin-left: auto;
      }
      @media (max-width: 40rem) {
        .named-row {
          grid-template-columns: 1fr;
        }
      }
    `,
  ];
}

export default CacheTagsTargetsViewElement;

declare global {
  interface HTMLElementTagNameMap {
    "thebuilder-cache-tags-targets-view": CacheTagsTargetsViewElement;
  }
}
