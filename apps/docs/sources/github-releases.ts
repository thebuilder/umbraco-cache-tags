import { resolve } from "node:path";

import type { ContentSource, SourceEntry } from "blume/core/sources/types.ts";
import { githubReleasesSource } from "blume/sources/github-releases.ts";

type ReleaseSourceOptions = {
  owner: string;
  repo: string;
};

const excludeSearchFromRaw = (raw: string): string =>
  raw.replace(/^---\n/u, "---\nsearch:\n  exclude: true\n");

const excludeFromSearch = (entry: SourceEntry): SourceEntry => ({
  ...entry,
  data: {
    ...entry.data,
    search: { exclude: true },
  },
  raw: entry.raw ? excludeSearchFromRaw(entry.raw) : undefined,
});

// Blume builds /changelog only once there is an entry, and the Changelog tab would point nowhere
// until the first release. This stands in for the releases until then, and goes the moment GitHub
// has one, with nothing to remember to remove.
const noReleasesYet = ({ owner, repo }: ReleaseSourceOptions): SourceEntry => {
  const data = {
    changelog: { category: "Upcoming" },
    date: "2026-10-13",
    search: { exclude: true },
    title: "No releases yet",
    type: "changelog",
  };
  const body = `Cache Tags has not been released yet. Each release will appear here with its notes as soon as it is published on [GitHub Releases](https://github.com/${owner}/${repo}/releases).`;
  const frontmatter = [
    "---",
    "changelog:",
    "  category: Upcoming",
    "date: 2026-10-13",
    "search:",
    "  exclude: true",
    "title: No releases yet",
    "type: changelog",
    "---",
  ].join("\n");
  return {
    body: { format: "md", text: body },
    data,
    raw: `${frontmatter}\n${body}\n`,
    ref: "no-releases-yet.md",
  };
};

// Blume rebuilds a custom() source on each run's context through withContext, so the wrapper
// re-wraps the rebuilt engine. Spreading the engine's own withContext would hand Blume the bare
// releases source and drop the search exclusion and the placeholder.
const wrapReleaseSource = (
  source: ContentSource,
  options: ReleaseSourceOptions,
): ContentSource => {
  const read = source.read;

  return {
    ...source,
    load: async () => {
      const result = await source.load();

      return {
        ...result,
        entries:
          result.entries.length > 0
            ? result.entries.map(excludeFromSearch)
            : [noReleasesYet(options)],
      };
    },
    read: read
      ? async (ref: string) => excludeSearchFromRaw(await read(ref))
      : undefined,
    withContext: (context) =>
      wrapReleaseSource(
        source.withContext?.(context) ?? source,
        options,
      ),
  };
};

export const githubReleaseChangelogSource = ({
  owner,
  repo,
}: ReleaseSourceOptions): ContentSource => {
  const isDev = process.argv.includes("dev");
  const source = githubReleasesSource(
    {
      name: "changelog",
      owner,
      prefix: "changelog",
      repo,
    },
    {
      cacheDir: resolve(".blume/cache/changelog"),
      mode: isDev ? "dev" : "build",
      projectRoot: process.cwd(),
      refresh: !isDev,
    },
  );

  return wrapReleaseSource(source, { owner, repo });
};
