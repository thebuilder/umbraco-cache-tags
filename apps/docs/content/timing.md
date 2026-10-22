---
title: How a purge is timed
description: Why a purge waits for every Umbraco server, and why listings are purged a second time once every server's index has the change.
seo:
  image: /og/timing.png
---

A purge sent too early is worse than none: the frontend refetches at once, gets the old content from a server that has not applied the change yet, and caches it again for days. So the package waits until every server serves the new content.

## The steps

1. **Record.** The server where the editor published records the change, with the keys it plans to purge, in the package's ledger.
2. **Acknowledge.** Every server sends a heartbeat every 30 seconds. When a server applies the cache refresh for a change, it evicts the change's responses from Umbraco's own output cache and acknowledges the change. A draft save does not count; the refresh has to carry what the change did.
3. **Release.** Once every server that was running when the change was made has acknowledged it, any server resolves the plan to response keys and queues one purge per target. It waits at most `AckTimeout` (60 seconds), then sends anyway and logs which servers did not answer.
4. **Listings.** Listings come from each server's Delivery API index, which Umbraco updates in the background a second or two after the refresh, and over a minute right after a restart. Each server checks its index until it holds the changed item as it is in the database, evicts its own listing responses, and records the change as indexed. Once every server has, the listing purge goes out. It waits at most `IndexTimeout` (5 minutes).
5. **Send.** Any server sends due purges, one server at a time per purge. A failure is retried after 2, 4, 8 and more seconds, up to 10 minutes apart, and kept as failed after `MaxAttempts` (10). The backoffice lists failed purges, and can send them again.

Listings are in the first purge too, on purpose: the items a listing shows come from the published cache, so they are fresh at once. Only which items it lists, and their order, waits for the index.

## Several servers

Every server, delivery servers included, runs the same loop against the shared database. Nothing needs configuring: a server that starts sending heartbeats is waited for, and one that stops counts as gone after `ServerTimeout` (2 minutes).

- A server that has just started applies other servers' changes once Umbraco's instruction job first runs, about a minute after start. A change made in that minute waits for it, or for the ack timeout.
- A server that restarts after a change was released is a new server that nothing waits for, and serves its listings from the index as it rebuilds.

## What purges everything

Some changes reach every response, and purge `umbraco` as stale:

- a domain, language or public access change;
- a route change with more descendants than `MaxDescendants` (500).

A content type or data type change purges the `schema-` key of every type it alters, which reaches every response that renders such an item.
