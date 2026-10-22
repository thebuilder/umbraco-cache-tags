---
title: Backoffice
description: See what is cached, follow each change to its purges, and purge by hand from Umbraco.
seo:
  image: /og/backoffice.png
---

Administrators find the package under **Settings > Advanced > Cache tags**, and on every document's and media item's **Info** tab.

## Changes

The latest changes, newest first, each with:

- what it planned: the item and listing keys;
- how many recorded responses the plan reaches now;
- which servers applied it, and which servers' indexes have it;
- its purges, per target, with their attempts and any error.

A change shows **Waiting for servers** until every server has applied it, **Waiting for index** until every server's index has it, and **Purged** once its purges are queued or sent. The view follows a change until it is purged.

Failed purges, those given up after their last attempt, are listed at the top. **Retry** sends one again once the target answers; **Set aside** stops it showing.

## Look up

Search by an item's name, or by part of a path, a query or a key, and open what you find:

- a document or media item shows the responses that include it;
- a `response-` key shows the request it stands for, its culture, when it was rendered and last served, and every key it covers;
- any other key shows the responses that cover it.

Catch-all listings such as searches carry their keys themselves and are not recorded, so they are not listed.

## Purge

Purge keys by hand, one per line, or purge everything. Purging everything makes every cached response fetch again from Umbraco, so expect more load for a while.

## The Info tab

A document's or media item's **Info** tab has a **Cache** box: how many served responses include it, the first few of them, a link to all of them in Look up, and **Purge this item**.

The list is what the package has recorded, not what a cache holds right now. After a purge the responses stay listed, because the next render of each includes the item again.
