import { createHmac, timingSafeEqual } from "node:crypto";
import { revalidateTag } from "next/cache";

/*
 * The purge webhook TheBuilder.CacheTags calls once a change has reached every delivery server.
 *
 *   POST /api/cache-tags
 *   X-Cache-Tags-Timestamp: <unix time in seconds>
 *   X-Cache-Tags-Signature: sha256=<lowercase hex HMAC-SHA256>
 *   {
 *     "version": 1,
 *     "id": "<purge id>",
 *     "changeId": "<change id>",
 *     "expire": ["response-<hash>", "content-<guid>", ...],
 *     "stale": ["type-article", "children-<guid>", "umbraco", ...]
 *   }
 *
 * Keys in "expire" name one response or one item: the next request waits for fresh content. Keys
 * in "stale" are listings or everything: they touch many pages, so they are served stale while
 * they refresh. A body over 1 MB, or of a version this receiver does not know, is refused.
 *
 * The signature covers `${method}\n${path}\n${timestamp}\n${body}`, with the raw body. Either the
 * current or the previous secret may have signed it, so secrets can be rotated without downtime.
 */

const toleranceSeconds = 5 * 60;
const maxBodyBytes = 1024 * 1024;

/** Event ids already handled, with when they may be forgotten. In memory, so per process. */
const handled = new Map<string, number>();

export async function POST(request: Request) {
  const secrets = [process.env.CACHE_TAGS_WEBHOOK_SECRET, process.env.CACHE_TAGS_WEBHOOK_SECRET_PREVIOUS].filter(
    (secret): secret is string => Boolean(secret),
  );
  if (secrets.length === 0) {
    return Response.json({ error: "CACHE_TAGS_WEBHOOK_SECRET is not set." }, { status: 500 });
  }

  if (Number(request.headers.get("content-length") ?? maxBodyBytes + 1) > maxBodyBytes) {
    return Response.json({ error: "The body is missing a Content-Length or is over 1 MB." }, { status: 413 });
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

  let purge: { version?: unknown; id?: unknown; expire?: unknown; stale?: unknown };
  try {
    purge = JSON.parse(body);
  } catch {
    return Response.json({ error: "The body is not JSON." }, { status: 400 });
  }

  const { version, id, expire, stale } = purge;
  if (version !== 1) {
    return Response.json({ error: `Purge version ${String(version)} is not supported.` }, { status: 400 });
  }
  if (typeof id !== "string" || id === "" || !isStrings(expire) || !isStrings(stale)) {
    return Response.json({ error: 'The body needs an "id" string and "expire" and "stale" arrays of strings.' }, { status: 400 });
  }

  for (const [seen, forgetAt] of handled) {
    if (forgetAt <= now) {
      handled.delete(seen);
    }
  }
  if (handled.has(id)) {
    return Response.json({ id, duplicate: true });
  }

  for (const key of expire) {
    revalidateTag(key, { expire: 0 });
  }
  for (const key of stale) {
    revalidateTag(key, "max");
  }

  // A replay is rejected by the timestamp once the window has passed, so the id only needs keeping
  // until then. The timestamp may be up to the tolerance ahead of this clock.
  handled.set(id, seconds + toleranceSeconds + 1);
  return Response.json({ id, duplicate: false, expired: expire.length, stale: stale.length });
}

function isStrings(value: unknown): value is string[] {
  return Array.isArray(value) && value.every((item) => typeof item === "string");
}

function sign(secret: string, payload: string) {
  return `sha256=${createHmac("sha256", secret).update(payload).digest("hex")}`;
}

function matches(received: string, expected: string) {
  const a = Buffer.from(received);
  const b = Buffer.from(expected);
  return a.length === b.length && timingSafeEqual(a, b);
}
