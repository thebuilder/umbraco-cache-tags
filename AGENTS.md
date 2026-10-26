# Agents

`TheBuilder.CacheTags` is one NuGet package for Umbraco 17 and 18 (.NET 10): it tags Delivery API
responses with cache keys and purges the right keys at the frontend once every delivery server has
applied a change.

- Stay generic: no search vendor and no frontend framework built in. Frontend integration is
  documented as examples.
- No Claude co-author trailers in commits, and no "Generated with Claude Code" lines in pull requests.

## Layout

- `src/TheBuilder.CacheTags`: the package. `Collection/` tags Delivery API responses with the keys
  of what they show, `Ledger/` plans the purge for each change on the server that makes it,
  `Delivery/` waits for every server and sends the purges, `Storage/` holds the package's tables,
  `ManagementApi/` is the admin API behind the backoffice.
- `tests/TheBuilder.CacheTags.Tests`: unit tests, including the ledger's SQL against SQLite.
- `docs/design.md`: the key scheme, the header and webhook contract, and how a purge is planned and
  delivered. Keep it true.
- `apps/docs`: the documentation site.
- `samples/TheBuilder.CacheTags.Example`: an Umbraco site with seeded content, on http://localhost:5320.
- `samples/nextjs`: a Next.js site that renders it and receives purge webhooks. `samples/README.md`
  defines the webhook format the package must send.

## Commands

```bash
dotnet build TheBuilder.CacheTags.slnx
dotnet test tests/TheBuilder.CacheTags.Tests
dotnet run --project samples/TheBuilder.CacheTags.Example
cd samples/nextjs && pnpm install && pnpm dev
```

See `samples/README.md` for checking a purge end to end.

## Public C# API

The package ships `TheBuilder.CacheTags`, so its public types and members are its API. Each shipped
project declares that API in `PublicAPI.Shipped.txt` (what the last stable release
shipped) and `PublicAPI.Unshipped.txt` (changes since then). Microsoft.CodeAnalysis.PublicApiAnalyzers
fails the build when the code and the files disagree. `Directory.Build.props` turns it on for every
project that has the files.

- Make new types `internal` unless a site is meant to implement, replace, configure or consume
  them. The main assembly's internals are visible to `TheBuilder.CacheTags.Tests` only.
- Controllers are public and ASP.NET only calls public constructors, so every type a controller's
  constructor takes is public. When that type is not meant for sites, put a public interface with
  only `internal` members in front of an internal class. Nothing outside the assembly can implement or call it,
  so its members can change without breaking anyone.
  The implementing members must be `public` on the internal class.
- Adding public API: the RS0016 error quotes the exact line to add to `PublicAPI.Unshipped.txt`,
  and IDEs offer it as a code fix.
- Removing public API: when the line is in `PublicAPI.Unshipped.txt`, delete it. It never shipped,
  so nothing breaks. When the line is in `PublicAPI.Shipped.txt`, leave it there and add it to
  `PublicAPI.Unshipped.txt` with a `*REMOVED*` prefix. RS0017 names the line. This is a breaking
  change, so say so in the pull request.
- Changing a member's signature removes the old line and adds a new one. Follow both rules above.
- Keep both files in ordinal order (`LC_ALL=C sort`), with `#nullable enable` on the first line.
- Many lines at once: `dotnet format analyzers` does not apply this analyzer's fix, but each RS0016
  message is the line itself, so the build output can be collected:

  ```bash
  dotnet build src/TheBuilder.CacheTags --no-incremental 2>&1 | grep 'error RS0016' | sed -E "s/^.*Symbol '//; s/' is not part of the declared public API.*//" | LC_ALL=C sort -u
  ```

  Add the output to `PublicAPI.Unshipped.txt`, then sort the lines below `#nullable enable`.
