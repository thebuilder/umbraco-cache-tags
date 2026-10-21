// Sends a signed purge webhook to app/api/cache-tags, the way TheBuilder.CacheTags will.
//
//   pnpm purge content-e0000000-0000-0000-0000-000000000003 type-article
//
// Response and item keys (response-, content-, media-) are sent to expire, the rest to go stale.
//
// Reads CACHE_TAGS_WEBHOOK_SECRET and FRONTEND_URL from the environment, then .env.local, then .env.
import { createHmac, randomUUID } from "node:crypto";
import { existsSync } from "node:fs";

for (const file of [".env.local", ".env"]) {
  if (existsSync(file)) {
    process.loadEnvFile(file);
  }
}

const keys = process.argv.slice(2);
const secret = process.env.CACHE_TAGS_WEBHOOK_SECRET;
if (keys.length === 0 || !secret) {
  console.error("Usage: pnpm purge <key> [key ...], with CACHE_TAGS_WEBHOOK_SECRET set.");
  process.exit(1);
}

const url = new URL("/api/cache-tags", process.env.FRONTEND_URL ?? "http://localhost:3000");
const expire = keys.filter((key) => /^(response|content|media)-/.test(key));
const stale = keys.filter((key) => !expire.includes(key));
const body = JSON.stringify({ version: 1, id: randomUUID(), changeId: randomUUID(), expire, stale });
const timestamp = Math.floor(Date.now() / 1000).toString();
const signature = createHmac("sha256", secret).update(`POST\n${url.pathname}\n${timestamp}\n${body}`).digest("hex");

const response = await fetch(url, {
  method: "POST",
  headers: {
    "Content-Type": "application/json",
    "X-Cache-Tags-Timestamp": timestamp,
    "X-Cache-Tags-Signature": `sha256=${signature}`,
  },
  body,
});
console.log(response.status, await response.text());
process.exit(response.ok ? 0 : 1);
