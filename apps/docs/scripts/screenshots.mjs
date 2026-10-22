// Takes the documentation's backoffice screenshots from a running sample site, so they can be
// retaken whenever the UI changes. Start the sample first:
//
//   dotnet run --project samples/TheBuilder.CacheTags.Example
//
// and, so the purges have somewhere to go, the Next.js sample (see samples/README.md). Then run
// `pnpm docs:screenshots` (or set UMBRACO_URL for a sample on another address). The script requests
// a few Delivery API responses, publishes Harbour mornings so the newest change is a publish, signs
// in with the sample's own unattended login, and writes the images to content/screenshots/.
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { chromium } from "playwright";
import sharp from "sharp";

const umbracoUrl = (process.env.UMBRACO_URL ?? "http://localhost:5320").replace(/\/$/, "");
const docsRoot = fileURLToPath(new URL("../", import.meta.url));
const sampleSettings = JSON.parse(
  readFileSync(
    fileURLToPath(new URL("../../../samples/TheBuilder.CacheTags.Example/appsettings.json", import.meta.url)),
    "utf8"
  )
);
const login = sampleSettings.Umbraco.CMS.Unattended;

const api = `${umbracoUrl}/umbraco/management/api/v1`;
const workspace = `${umbracoUrl}/umbraco/section/settings/workspace/thebuilder-cache-tags-root/view`;
// Harbour mornings, from the sample's seed (samples/README.md).
const harbourMornings = "e0000000-0000-0000-0000-000000000003";

// The sample's API user for scripts (samples/README.md), so the setup does not depend on the
// backoffice's own token.
let tokenResponse;
try {
  tokenResponse = await fetch(`${api}/security/back-office/token`, {
    method: "POST",
    body: new URLSearchParams({
      grant_type: "client_credentials",
      client_id: "umbraco-back-office-cache-tags-e2e",
      client_secret: "example-local-e2e-client-secret",
    }),
  });
} catch (error) {
  console.error(`Could not reach ${umbracoUrl}. Start the sample site first, or set UMBRACO_URL.`);
  throw error;
}
if (!tokenResponse.ok) throw new Error(`The token endpoint answered ${tokenResponse.status}.`);
const { access_token: token } = await tokenResponse.json();

const call = async (method, path, body) => {
  const response = await fetch(`${api}${path}`, {
    method,
    headers: { Authorization: `Bearer ${token}`, "Content-Type": "application/json" },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (!response.ok) throw new Error(`${method} ${path} answered ${response.status}: ${await response.text()}`);
  const text = await response.text();
  return text ? JSON.parse(text) : undefined;
};

// The same content every run: responses that include Harbour mornings, so the Cache box and Look up
// have something to list, and a publish of it at the top of Changes.
for (const path of ["content/item/", "content/item/articles/", "content/item/articles/harbour-mornings/", "content/item/articles/forest-walks/", "content?fetch=children:/articles/"]) {
  const response = await fetch(`${umbracoUrl}/umbraco/delivery/api/v2/${path}`);
  if (!response.ok) throw new Error(`GET ${path} answered ${response.status}.`);
}
const before = (await call("GET", "/cache-tags/changes?take=1"))[0]?.id;
await call("PUT", `/document/${harbourMornings}/publish`, { publishSchedules: [{ culture: null }] });

// Wait until the publish is purged, so the Changes view is not mid-refresh when it is captured.
let published;
for (let attempt = 0; attempt < 60; attempt++) {
  const [latest] = await call("GET", "/cache-tags/changes?take=1");
  if (latest && latest.id !== before && latest.status === "Released" && latest.purges.every((purge) => purge.state === "Sent")) {
    published = latest;
    break;
  }
  await new Promise((resolve) => setTimeout(resolve, 1000));
}
if (!published) throw new Error("The publish was not purged within a minute. Is the Next.js sample running?");

const browser = await chromium.launch();
const context = await browser.newContext({
  viewport: { width: 1440, height: 760 },
  deviceScaleFactor: 2,
  colorScheme: "light",
});
const page = await context.newPage();

// Server names are the machine's name, which has no place in the docs, so the Changes view is shown
// them as web-1, web-2 and so on.
const serverNames = new Map();
await page.route("**/management/api/v1/cache-tags/changes?*", async (route) => {
  const response = await route.fetch();
  const rename = (name) => {
    const [machine, ...rest] = name.split("/");
    if (!serverNames.has(machine)) serverNames.set(machine, `web-${serverNames.size + 1}`);
    return [serverNames.get(machine), ...rest].join("/");
  };
  const changes = (await response.json()).map((change) => ({
    ...change,
    server: rename(change.server),
    acknowledgedBy: change.acknowledgedBy.map(rename),
    notAcknowledgedBy: change.notAcknowledgedBy.map(rename),
    indexedBy: change.indexedBy.map(rename),
  }));
  await route.fulfill({ response, json: changes });
});

await page.goto(`${umbracoUrl}/umbraco`);
await page.locator("#username-input").fill(login.UnattendedUserEmail);
await page.locator("#password-input").fill(login.UnattendedUserPassword);
await page.keyboard.press("Enter");
await page.waitForURL(/\/section\//, { timeout: 60_000 });

const settle = async () => {
  await page.waitForTimeout(2500);
  // Away from anything with a hover state.
  await page.mouse.move(5, page.viewportSize().height - 5);
};

const save = async (name, raw) => {
  const output = `${docsRoot}content/screenshots/${name}.png`;
  // A palette PNG keeps the UI's flat colours exact at a fraction of the size.
  await sharp(raw).png({ compressionLevel: 9, palette: true, quality: 90 }).toFile(output);
  console.log(`Wrote content/screenshots/${name}.png`);
};

// The Settings tree and the header take a third of the width in the settings shots, which leaves
// the workspace's text small on a docs page, so those are cropped to the workspace and its tabs,
// down to the workspace's footer, which is empty here.
const settingsWorkspace = { x: 300, y: 60 };

const capture = async (name, height) => {
  await page.setViewportSize({ width: 1440, height });
  await settle();
  const { x, y } = settingsWorkspace;
  const footer = await page.locator("umb-workspace-footer").boundingBox();
  const bottom = footer?.y ?? height;
  await save(name, await page.screenshot({ clip: { x, y, width: 1440 - x, height: bottom - y } }));
};

// Changes, with the publish of Harbour mornings open.
await page.goto(`${workspace}/changes`);
await page.locator("details.change summary").first().click();
await capture("changes", 900);

// Look up, searching for "harbour". The view reads its search from the URL.
await page.goto(`${workspace}/lookup?q=harbour`);
await page.getByText("Harbour mornings").first().waitFor();
await capture("lookup", 900);

// The Cache box on Harbour mornings' Info tab.
await page.setViewportSize({ width: 1440, height: 1400 });
await page.goto(`${umbracoUrl}/umbraco/section/content/workspace/document/edit/${harbourMornings}/invariant/view/info`);
const box = page.locator("thebuilder-cache-tags-info-app uui-box");
await box.locator("li").first().waitFor();
await box.scrollIntoViewIfNeeded();
await settle();
await save("info", await box.screenshot());

await browser.close();
