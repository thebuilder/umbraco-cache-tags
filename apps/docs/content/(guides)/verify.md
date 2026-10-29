---
title: Check it on your site
description: Look at one header, then roll out in steps.
seo:
  image: /og/verify.png
---

Every change the package handles is tested end to end on every commit: pickers, blocks, rich text, media, cultures, listings, renames, unpublishing and the recycle bin, on one server, on two, and with Umbraco's output cache on. What your site adds is its own setup, so take a quick look there too.

## Look at a header

```sh
curl -sD - -o /dev/null https://cms.example.com/umbraco/delivery/api/v2/content/item/some/page/ | grep -i x-umbraco-cache-keys
```

It lists `umbraco`, the page's own key and the key of everything the page shows. References are read through Umbraco's own reference tracking, so anything Umbraco lists under a page's **References** is covered, your own property editors included.

If a CDN or proxy sits in front of Umbraco, request the page through it too and check the header arrives whole. nginx needs a `proxy_buffer_size` of 16k or more for long headers.

## Roll out in steps

1. **Without a target.** Install the package and add no target. It records each change and the keys it would purge under **Settings > Advanced > Cache tags > Changes**, and sends nothing.
2. **With a target.** Add your target and tag your frontend's cache, but keep your revalidate time. Publish a page and watch it, and the pages that show it, refresh.
3. **A long cache.** Once that looks right, raise the revalidate time. Keep a safety revalidate, such as a day.

To roll back at any step, remove the target.
