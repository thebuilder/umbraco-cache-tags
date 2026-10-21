import type { NextConfig } from "next";

const nextConfig: NextConfig = {
  // Needed for 'use cache', cacheTag and cacheLife, which lib/umbraco.ts uses.
  cacheComponents: true,
  partialPrefetching: true,
  // Keep `next dev` from writing an AGENTS.md into the sample.
  agentRules: false,
};

export default nextConfig;
