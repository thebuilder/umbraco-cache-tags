---
title: Troubleshooting
description: What to check when responses carry no keys, a purge does not arrive, or a listing stays old.
seo:
  image: /og/troubleshooting.png
---

Start in **Settings > Advanced > Cache tags**. **Changes** shows every change, the keys it planned, which servers applied it, and each purge with its error. **Look up** shows what a key covers.

## A response has no keys

- **Preview and signed-in requests** get no keys on purpose. Preview gets `no-store`, and a member's or user's request gets `private`.
- **A response that is not a 200**, such as a 404 or a redirect, gets `no-store` and no keys.
- **A proxy or CDN in between** may drop the header. Check the response straight from Umbraco.
- **`CacheTags:Enabled`** is `false`, or the header has another name in `CacheTags:HeaderName`.

## A purge does not arrive

Open the change in **Changes**.

- **Waiting for servers.** A server has not applied the change yet. A server that just started applies changes about a minute after start, and a server whose clock is more than 2 seconds behind is never acknowledged in time. The package sends the purge anyway after `AckTimeout`, and such a server then evicts the change from its own output cache.
- **No purges.** No target is configured, or every target is disabled.
- **A purge with an error.** The target answered with an error, or the package could not reach it. The package retries with backoff, and lists the purge under **Failed purges** after its last attempt. **Retry** sends it again.
- **401 from the webhook.** The secrets differ, the clocks are more than 5 minutes apart, or a proxy changes the path, which is part of the signature.

## A page stays old after a purge

- **The page was not cached with the keys.** Check that your fetch tags the entry with every key in the header. Next.js keeps at most 128 tags per entry.
- **Something else caches the page too**, such as a CDN in front of the frontend. Add that cache as a target as well.
- **The entry outlived `MappingRetention`** (90 days). The package had forgotten which keys the response covers, so no purge named it. Keep `cacheLife`'s `expire`, or a CDN's edge TTL, within the retention.
- **The request varies by a header** that is not part of your cache key. The package sends no `Vary` header, so if you forward `Accept-Language`, `Accept-Segment` or `Start-Item`, include them in your own cache key.

## Look up shows fewer responses for an item after a publish

Once every purge of a change was sent, the daily cleanup forgets the responses it expired. It keeps those rendered or served in the hour before the change, or since. Fetch the page from the frontend again and its responses reappear.

## A listing stays old

The package purges listings twice, with the change and again once every server's index has it. **Waiting for index** in **Changes** means a server's index has not caught up. Right after a restart that can take over a minute. The package sends the listing purge anyway after `IndexTimeout`.

## SQLite says the database is locked

Under load, Umbraco on SQLite can report `database table is locked` and stop answering until you restart it. SQLite allows one writer at a time, and the package writes too, recording each response the first time Umbraco renders it. Use SQL Server for anything but local development.
