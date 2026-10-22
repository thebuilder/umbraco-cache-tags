---
title: Troubleshooting
description: What to check when responses carry no keys, a purge does not arrive, or a listing stays old.
seo:
  image: /og/troubleshooting.png
---

Start in **Settings > Advanced > Cache tags**. **Changes** shows every change, the keys it planned, which servers applied it, and each purge with its error. **Look up** shows what a key covers.

## A response has no keys

- **Preview and signed-in requests** get no keys, by design: `no-store` for preview, `private` for a member's or user's request.
- **A response that is not a 200**, such as a 404 or a redirect, gets `no-store` and no keys.
- **A proxy or CDN in between** may drop the header. Check the response straight from Umbraco.
- **`CacheTags:Enabled`** is `false`, or the header has another name in `CacheTags:HeaderName`.

## A purge does not arrive

Open the change in **Changes**.

- **Waiting for servers.** A server has not applied the change yet. A server that just started applies changes about a minute after start. The change is sent anyway after `AckTimeout`.
- **No purges.** No target is configured, or every target is disabled.
- **A purge with an error.** The target answered with an error, or could not be reached. It is retried with backoff, and listed under **Failed purges** after its last attempt. **Retry** sends it again.
- **401 from the webhook.** The secrets differ, the clocks are more than 5 minutes apart, or a proxy changes the path, which is part of the signature.

## A page stays old after a purge

- **The page was not cached with the keys.** Check that your fetch tags the entry with every key in the header, and that Next.js got all of them: it keeps 128 per entry.
- **The page is cached somewhere else too**, such as a CDN in front of the frontend. That cache needs purging as well: add it as a target.
- **The request varies by a header** that is not part of your cache key. The package does not send `Vary`; include `Accept-Language`, `Accept-Segment` and `Start-Item` in your own cache key if you forward them.

## A listing stays old

Listings are purged twice: with the change, and again once every server's index has it. **Waiting for index** in **Changes** means a server's index has not caught up. Right after a restart that can take over a minute; the purge goes anyway after `IndexTimeout`.

## SQLite says the database is locked

Under load, Umbraco on SQLite can report `database table is locked` and stop answering until it is restarted. The repository's sample site does this now and then during its end-to-end tests, which publish and fetch quickly from several processes at once. SQLite allows one writer at a time, and the package writes too: it records each response the first time it is rendered. Use SQL Server for anything but local development.
