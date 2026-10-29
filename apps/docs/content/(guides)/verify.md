---
title: Check it on your site
description: Check the keys, watch the purges and roll out in steps, before a long cache depends on them.
seo:
  image: /og/verify.png
---

The package's own tests run every case below against a sample site on every change, on one server and on two. Your site has its own templates, property editors and hosting, so check it there too before you cache for long. Check in this order: the keys, then the purges, then the frontend.

## 1. Check the keys

A response that is missing a key is never purged by a change to that item. It stays old until your safety revalidate, and nothing reports it. So check the keys first.

Request a page's Delivery API response straight from Umbraco and read the header:

```sh
curl -s -o /dev/null -D - https://cms.example.com/umbraco/delivery/api/v2/content/item/some/page/ | grep -i x-umbraco-cache-keys
```

It starts with `umbraco`, then the page's own `content-` key, then a `content-` or `media-` key for everything the page shows. For each place your site references other content, pick one page and find the key of the item it references:

- [ ] A content picker and a multinode tree picker
- [ ] A block list and a block grid, including a block inside another block's area
- [ ] Rich text: a link to a page, a link to a media item, an inline image, and a block in the rich text
- [ ] A media picker, and a multi URL picker with a page, a media item and an external link
- [ ] A picker that points at an unpublished page: its key is there even though the Delivery API leaves the page out, so publishing it purges the response
- [ ] Your own property editors and value converters, if any
- [ ] A listing: `children-<parent>` for `fetch=children:`, `type-<alias>` for a content type filter, `search-<index>` for anything else
- [ ] A preview request (`Preview: true` with your `Api-Key`): `Cache-Control: no-store` and no header
- [ ] The same request through every proxy or CDN in front of Umbraco: the header is still there and not cut short (nginx needs a `proxy_buffer_size` of 16k or more for a long header)

A page that shows more than 64 items lists their `type-` keys instead of each item's key. That is expected: it is still purged, only more often.

## 2. Watch changes before anything purges

Install the package without a target. It records every change and plans its keys, but sends nothing, and your frontend caches as it does today.

Edit and publish as usual for a few days, and read **Settings > Advanced > Cache tags > Changes**. For each change, check that the planned keys include the item, and that every server applied it. On a load-balanced site, the time from a change to its release is how long your slowest server took to apply it. A change released "after 60 seconds" means a server did not acknowledge it: look at that server's clock and its cache instruction sync.

## 3. Publish and watch the frontend

Add your target, tag your frontend's cache entries with the keys, and keep your current revalidate time for now. Then make each change below and check the result. Before each one, load the pages you will check twice, so they are cached. On a load-balanced site, make the change on the backoffice server and read the frontend that reads a delivery server.

| Change | What must refresh | What must not |
|---|---|---|
| Edit and publish a page | The page, the listings that show it, and every page that picks or links to it | A page that shows none of these |
| Publish one culture | That culture's page. Other cultures refresh too: keys carry no culture | |
| Change a value that does not vary by culture | The page in every culture | |
| Rename a page that has pages under it | Every page that picks or links to a page under it, since their routes changed | |
| Rename a page | Its old URL answers a redirect with `Cache-Control: no-store` and no keys | |
| Unpublish a page, or move it to the recycle bin | Its page is not found, and it is gone from listings and from the pages that pick it | |
| Publish a page that was never published | The pages that picked it now show it | |
| Edit a page that only rich text links to | The page with the link | |
| Rename or replace an image | Every page that shows it, inline images in rich text included | |
| Rename a media folder | Media listings that show it | |
| Sort a page's children | The listings that show them, once the index has the new order. The pages themselves keep their cache | |
| Your own APIs tagged with `[CacheTagged]` | The responses whose `x-` keys the change records | |

For each change, **Changes** shows it **Released** with every purge **Sent**. A purge with an error is retried, and listed under **Failed purges** after its last attempt.

## 4. Roll out in steps

1. **Keys only.** Install the package with no target. Check the keys (1) and the changes (2). Nothing your visitors see changes.
2. **Purges with a short cache.** Add the target and tag the frontend's cache, but keep your revalidate time as it is. Purges now make changes show sooner, and the revalidate time still bounds how old a page can get. Check the frontend (3).
3. **A long cache.** Once a week of changes shows no failed purges and no editor has seen an old page, raise the revalidate time. Keep a safety revalidate (an hour, a day) for a purge that never arrives.

To roll back at any step, remove the target. Your revalidate time takes over again.
