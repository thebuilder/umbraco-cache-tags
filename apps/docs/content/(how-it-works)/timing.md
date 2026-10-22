---
title: How a purge is timed
description: Why a purge waits for every Umbraco server, and why listings are purged a second time once every server's index has the change.
seo:
  image: /og/timing.png
---

A purge sent too early is worse than none. The frontend refetches at once, gets the old content from a server that has not applied the change yet, and caches it again for days. So the package waits until every server serves the new content.

## The steps

1. **Record.** The server where the editor published records the change, with the keys it plans to purge, in the package's ledger.
2. **Acknowledge.** Every server sends a heartbeat every 30 seconds. When a server applies the cache refresh for a change, it evicts the change's responses from Umbraco's own output cache and acknowledges the change. A refresh counts only if it carries what the change did, so a draft save does not count.
3. **Release.** Once every server that was running when the editor made the change has acknowledged it, any server resolves the plan to response keys and queues purges for each target, split at its `MaxKeysPerRequest`. It waits at most `AckTimeout` (60 seconds), then sends anyway and logs which servers did not answer.
4. **Listings.** Listings come from each server's Delivery API index, which Umbraco updates in the background a second or two after the refresh, and over a minute right after a restart. Each server checks its index until it holds the changed item as it is in the database, evicts its own listing responses, and records the change as indexed. Once every server has, the listing purge goes out. It waits at most `IndexTimeout` (5 minutes).
5. **Send.** Any server sends due purges, one server at a time per purge. The package retries a failed purge after 2, 4, 8 and more seconds, at most 10 minutes apart, and keeps it as failed after `MaxAttempts` (10). The backoffice lists failed purges, and can send them again.

The first purge includes listings on purpose. The items a listing shows come from the published cache, so they are fresh at once. Only which items it lists, and their order, wait for the index.

## Several servers

Every server, delivery servers included, runs the same loop against the shared database, and nothing needs configuring. The package waits for every server that sends heartbeats, and counts a server as gone after `ServerTimeout` (2 minutes) without one.

- A server that has just started applies other servers' changes once Umbraco's instruction job first runs, about a minute after start. A change made in that minute waits for it, or for the ack timeout.
- A server that restarts after a change is released counts as a new server, so that change does not wait for it. It serves its listings from the index as the index rebuilds.
- A server that the ack timeout sent a change without still evicts the change from its own output cache once it has applied it.
- Keep the servers' clocks in sync, as NTP does. A refresh seen within 2 seconds of a change counts for that change alone, so a server whose clock is further behind is not acknowledged in time: its changes wait for the ack timeout, and it evicts them once they are sent.

## What purges everything

These changes reach every response, and purge `umbraco` as stale:

- A domain, language or public access change
- A route change with more descendants than `MaxDescendants` (500)

A content type or data type change purges the `schema-` key of every type it alters, which reaches every response that renders such an item. Renaming a type's alias purges the old alias's keys as well as the new one's. Umbraco indexes a renamed content type's items under the new alias up to a minute later, so its listings are purged again once every server's index has.

## What a sort purges

A sort changes no item, only the order of listings. It purges the parent's children listings, the listings of the sorted items' types, and every listing the package does not record (searches, filters, listings with no parent or type), and waits for the index like any listing purge.
