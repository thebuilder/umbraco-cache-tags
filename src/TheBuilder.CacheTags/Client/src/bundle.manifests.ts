import { UMB_CURRENT_USER_IS_ADMIN_CONDITION_ALIAS } from "@umbraco-cms/backoffice/current-user";
import { UMB_DOCUMENT_WORKSPACE_ALIAS } from "@umbraco-cms/backoffice/document";
import { UMB_MEDIA_WORKSPACE_ALIAS } from "@umbraco-cms/backoffice/media";
import { UMB_WORKSPACE_CONDITION_ALIAS, UMB_WORKSPACE_ENTITY_IS_NEW_CONDITION_ALIAS } from "@umbraco-cms/backoffice/workspace";

/** Settings > Advanced > Cache tags, like Log Viewer and Webhooks. */
const entityType = "thebuilder-cache-tags-root";
const workspaceAlias = "TheBuilder.CacheTags.Workspace";

// The Management API behind it is for administrators only.
const administrators = [{ alias: UMB_CURRENT_USER_IS_ADMIN_CONDITION_ALIAS }];

const view = (alias: string, label: string, pathname: string, icon: string, weight: number, element: () => Promise<unknown>) => ({
  type: "workspaceView",
  alias: `TheBuilder.CacheTags.WorkspaceView.${alias}`,
  name: `Cache Tags ${label} View`,
  element,
  weight,
  meta: { label, pathname, icon },
  conditions: [{ alias: UMB_WORKSPACE_CONDITION_ALIAS, match: workspaceAlias }],
});

export const manifests = [
  {
    type: "backofficeEntryPoint",
    alias: "TheBuilder.CacheTags.EntryPoint",
    name: "Cache Tags API Entry Point",
    js: () => import("./entrypoint.js"),
  },
  {
    type: "menuItem",
    alias: "TheBuilder.CacheTags.MenuItem",
    name: "Cache Tags Menu Item",
    weight: 50,
    meta: { label: "Cache tags", icon: "icon-tags", entityType, menus: ["Umb.Menu.AdvancedSettings"] },
    conditions: administrators,
  },
  {
    type: "workspace",
    kind: "default",
    alias: workspaceAlias,
    name: "Cache Tags Workspace",
    meta: { entityType, headline: "Cache tags" },
  },
  // A Cache box on a document's and a media item's Info tab.
  ...[
    { workspace: UMB_DOCUMENT_WORKSPACE_ALIAS, name: "Document" },
    { workspace: UMB_MEDIA_WORKSPACE_ALIAS, name: "Media" },
  ].map(({ workspace, name }) => ({
    type: "workspaceInfoApp",
    alias: `TheBuilder.CacheTags.InfoApp.${name}`,
    name: `Cache Tags ${name} Info App`,
    element: () => import("./cache-info-app.element.js"),
    // After Links (100), Referenced by (90) and Redirect management (85); before History (80).
    weight: 82,
    conditions: [
      { alias: UMB_WORKSPACE_CONDITION_ALIAS, match: workspace },
      { alias: UMB_WORKSPACE_ENTITY_IS_NEW_CONDITION_ALIAS, match: false },
      ...administrators,
    ],
  })),
  view("Changes", "Changes", "changes", "icon-history", 300, () => import("./changes-view.element.js")),
  view("Lookup", "Look up", "lookup", "icon-search", 200, () => import("./lookup-view.element.js")),
  view("Purge", "Purge", "purge", "icon-trash", 100, () => import("./purge-view.element.js")),
  view("Targets", "Targets", "targets", "icon-connection", 50, () => import("./targets-view.element.js")),
] as UmbExtensionManifest[];
