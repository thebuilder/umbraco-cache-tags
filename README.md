<p align="center">
  <img src="https://raw.githubusercontent.com/thebuilder/umbraco-cache-tags/refs/heads/main/apps/docs/public/logo-mark.svg" width="112" height="112" alt="Cache Tags logo" />
</p>

<h1 align="center">Cache Tags for Umbraco</h1>

<p align="center">
  Cache Umbraco Delivery API responses at the frontend for as long as you like,<br />
  and purge exactly the right ones once every server has applied a change.
</p>

[![NuGet version](https://img.shields.io/nuget/vpre/TheBuilder.CacheTags)](https://www.nuget.org/packages/TheBuilder.CacheTags)
[![Umbraco Marketplace](https://img.shields.io/badge/Umbraco-Marketplace-3544b1)](https://marketplace.umbraco.com/package/thebuilder.cachetags)
[![License](https://img.shields.io/github/license/thebuilder/umbraco-cache-tags)](https://github.com/thebuilder/umbraco-cache-tags/blob/main/LICENSE)

Every cacheable Delivery API response carries its cache keys in a header. Your frontend tags its
cache entries with them. When an editor publishes, the package works out which responses showed the
change, waits until every Umbraco server serves it, and sends those keys to your frontend or CDN to
purge.

- **Exact purges.** Publishing an article purges its page, the listings it appears in, and the
  pages that pick it. Nothing else.
- **No stale refetch.** A purge goes out once every server has applied the change, and listings
  once every server's Delivery API index has it, so a frontend that refetches at once gets the new
  version.
- **Your cache, your choice.** Tag entries with Next.js `cacheTag`, purge Cloudflare by tag, or
  receive a signed webhook in anything else.
- **See what is cached.** Under **Settings > Advanced > Cache tags**, follow each change to its
  purges, look up what a key covers, and purge by hand.

The package is a preview. It supports Umbraco 17.4 and later, Umbraco 18 included, on .NET 10.

## Getting started

1. Install the package in your Umbraco site:

   ```sh
   dotnet add package TheBuilder.CacheTags --prerelease
   ```

2. Tag your frontend's cache entries with the keys in each response's `X-Umbraco-Cache-Keys`
   header.

3. Add a webhook route to your frontend that checks the signature and expires the keys it receives.

4. Point the package at it in `appsettings.json`:

   ```json
   {
     "CacheTags": {
       "Targets": {
         "nextjs": { "Url": "https://www.example.com/api/cache-tags", "Secret": "<at least 32 characters>" }
       }
     }
   }
   ```

5. Publish something, and watch the change and its purge under **Settings > Advanced > Cache tags**.

The [documentation](https://cache-tags.thebuilder.dk/) has a complete Next.js setup, every key and
setting, Cloudflare, tagging your own APIs, and troubleshooting.

## In this repository

- `src/TheBuilder.CacheTags`: the package, with its backoffice client in `Client/`.
- `samples`: an Umbraco site and a Next.js frontend that test the package end to end. See
  `samples/README.md`.
- `apps/docs`: the documentation site.
- `docs/design.md`: the design contract the package is held to, and `docs/findings.md`, the
  findings of the first probe.

## License

MIT. See [LICENSE](https://github.com/thebuilder/umbraco-cache-tags/blob/main/LICENSE).
