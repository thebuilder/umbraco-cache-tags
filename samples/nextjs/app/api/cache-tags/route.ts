import { createHmac, timingSafeEqual } from "node:crypto";
import { revalidateTag } from "next/cache";

/*
 * The purge webhook TheBuilder.CacheTags calls once a change has reached every delivery server.
 *
 *   POST /api/cache-tags
 *   X-Cache-Tags-Timestamp: <unix time in seconds>
 *   X-Cache-Tags-Signature: sha256=<lowercase hex HMAC-SHA256>
 *   { "id": "<event id>", "keys": ["c-<guid>", "t-article", ...] }
 *
 * The signature covers `${method}\n${path}\n${timestamp}\n${body}`, with the raw body. Either the
 * current or the previous secret may have signed it, so secrets can be rotated without downtime.
 */

const toleranceSeconds = 5 * 60;

/** Event ids already handled, with when they may be forgotten. In memory, so per process. */
const handled = new Map<string, number>();

export async function POST(request: Request) {
  const secrets = [process.env.CACHE_TAGS_WEBHOOK_SECRET, process.env.CACHE_TAGS_WEBHOOK_SECRET_PREVIOUS].filter(
    (secret): secret is string => Boolean(secret),
  );
  if (secrets.length === 0) {
    return Response.json({ error: "CACHE_TAGS_WEBHOOK_SECRET is not set." }, { status: 500 });
  }

  const body = await request.text();
  const timestamp = request.headers.get("x-cache-tags-timestamp") ?? "";
  const signature = request.headers.get("x-cache-tags-signature") ?? "";
  const seconds = /^\d+$/.test(timestamp) ? Number(timestamp) : Number.NaN;
  const now = Math.floor(Date.now() / 1000);
  if (!(Math.abs(now - seconds) <= toleranceSeconds)) {
    return Response.json({ error: "The timestamp is missing or outside the 5 minute window." }, { status: 401 });
  }

  const signed = `${request.method}\n${new URL(request.url).pathname}\n${timestamp}\n${body}`;
  if (!secrets.some((secret) => matches(signature, sign(secret, signed)))) {
    return Response.json({ error: "The signature does not match." }, { status: 401 });
  }

  let event: { id?: unknown; keys?: unknown };
  try {
    event = JSON.parse(body);
  } catch {
    return Response.json({ error: "The body is not JSON." }, { status: 400 });
  }

  const { id, keys } = event;
  if (typeof id !== "string" || id === "" || !Array.isArray(keys) || !keys.every((key) => typeof key === "string")) {
    return Response.json({ error: 'The body needs an "id" string and a "keys" array of strings.' }, { status: 400 });
  }

  for (const [seen, forgetAt] of handled) {
    if (forgetAt <= now) {
      handled.delete(seen);
    }
  }
  if (handled.has(id)) {
    return Response.json({ id, duplicate: true });
  }

  const expired: string[] = [];
  const stale: string[] = [];
  const ignored: string[] = [];
  for (const key of new Set(keys as string[])) {
    if (/^[qcm]-/.test(key)) {
      // One response or one item: the next request waits for fresh content.
      revalidateTag(key, { expire: 0 });
      expired.push(key);
    } else if (/^(t|ch|s)-/.test(key) || key === "umbraco") {
      // Listings (by content type, by parent, by other query) and everything: these touch many
      // pages, so serve stale while they refresh.
      revalidateTag(key, "max");
      stale.push(key);
    } else {
      // A kind this example does not know is reported back, not purged.
      ignored.push(key);
    }
  }

  // A replay is rejected by the timestamp once the window has passed, so the id only needs keeping
  // until then. The timestamp may be up to the tolerance ahead of this clock.
  handled.set(id, seconds + toleranceSeconds + 1);
  return Response.json({ id, duplicate: false, expired, stale, ignored });
}

function sign(secret: string, payload: string) {
  return `sha256=${createHmac("sha256", secret).update(payload).digest("hex")}`;
}

function matches(received: string, expected: string) {
  const a = Buffer.from(received);
  const b = Buffer.from(expected);
  return a.length === b.length && timingSafeEqual(a, b);
}
