# Releasing the NuGet package

Publishing a GitHub Release publishes the package. The release tag is the version: the publish
workflow applies it to the NuGet package and to the Umbraco package manifest, verifies the package,
and pushes that same file to nuget.org. The `0.1.0` in the project file is only the fallback for
local builds.

## One-time setup

1. Create a `nuget` environment in the repository's GitHub settings.
2. Add an environment secret `NUGET_USER` holding the nuget.org username, `the_builder`.
3. Add required reviewers to the environment if every publish should wait for an approval.
4. On nuget.org, add a trusted publishing policy:
   - Package owner: `the_builder`
   - Publisher: GitHub Actions
   - Repository owner: `thebuilder`
   - Repository: `umbraco-cache-tags`
   - Workflow: `publish-nuget.yml`
   - Environment: `nuget`

The policy has to be active before the first release. It exchanges GitHub's short-lived OIDC token
for a temporary NuGet API key, so no long-lived NuGet API key is stored in GitHub.

## Before a release

1. Check that CI passes for the commit you are releasing.
2. Run `dotnet list src/TheBuilder.CacheTags/TheBuilder.CacheTags.csproj package --vulnerable --include-transitive`
   and resolve any high or critical finding. `Directory.Build.props` suppresses five ImageSharp
   advisories that come with Umbraco 17 and 18 themselves; check whether Umbraco has shipped a fixed ImageSharp
   and the suppressions can go.
3. Review `pnpm audit --prod`. The backoffice bundle externalizes Umbraco's own modules, so it ships only the package's code.
4. Look at the package CI built before approving the `nuget` environment deployment.
5. Read `src/TheBuilder.CacheTags/PublicAPI.Unshipped.txt`. It holds every change to the public
   C# API since the last stable release. A `*REMOVED*` line is a breaking change for sites that
   build against the package, and belongs in the release notes. For a stable release, mark the API
   shipped in the commit you release:
   - for each `*REMOVED*` line, delete it and the line it names in `PublicAPI.Shipped.txt`;
   - move every other line into `PublicAPI.Shipped.txt`, sorted with `LC_ALL=C sort`;
   - leave `PublicAPI.Unshipped.txt` holding only `#nullable enable`.

GitHub Releases are the changelog. Write the release notes on the release; the NuGet package links
to it.

## Publish a prerelease

1. Create a GitHub Release from the commit to publish.
2. Give it a new prerelease tag, such as `v0.1.0-preview.1`.
3. Select **Set as a pre-release**.
4. Write the release notes, and publish the release.

NuGet versions are permanent. Use a new prerelease number for every publish, even when an earlier
one has been unlisted.

## Publish a stable release

1. Create a GitHub Release from the commit to publish.
2. Give it a new stable tag, such as `v0.1.0`.
3. Leave **Set as a pre-release** unselected.
4. Write the release notes, and publish the release.

The release type and the version have to agree: a prerelease needs a prerelease version, and a
stable release a stable one. The workflow refuses the release otherwise, because NuGet decides
prerelease from the version alone.

## When a release is faulty

A published NuGet version cannot be replaced or deleted.

1. Unlist the version on nuget.org.
2. Fix the problem on `main` and let CI pass.
3. Publish a new version. Never reuse the faulty version number.
4. Name the replacement version in the faulty release's notes.
