import { readFileSync } from "node:fs";
import { defineConfig, type Plugin } from "vite";

const projectVersionPattern = /<Version>([^<]+)<\/Version>/;

/**
 * Writes umbraco-package.json with the package's version, so the manifest Umbraco reads and the
 * NuGet package always agree. `dotnet pack` passes the version as PACKAGE_VERSION; a plain build
 * reads the project file's, which is the one place the local version is written down.
 */
const packageManifest = (): Plugin => {
  const read = (file: string) => readFileSync(new URL(file, import.meta.url), "utf8");
  const projectVersion = projectVersionPattern.exec(read("../TheBuilder.CacheTags.csproj"))?.[1];
  // || rather than ??: an empty PACKAGE_VERSION means none was given, not an empty version.
  const version = process.env.PACKAGE_VERSION || projectVersion;
  if (!version) {
    throw new Error("No PACKAGE_VERSION, and no <Version> in TheBuilder.CacheTags.csproj.");
  }
  return {
    name: "package-manifest",
    generateBundle() {
      this.emitFile({
        type: "asset",
        fileName: "umbraco-package.json",
        source: `${JSON.stringify({ ...JSON.parse(read("./umbraco-package.json")), version }, null, 2)}\n`,
      });
    },
  };
};

export default defineConfig({
  plugins: [packageManifest()],
  build: {
    lib: { entry: "src/bundle.manifests.ts", formats: ["es"], fileName: "cache-tags" },
    outDir: "../wwwroot/App_Plugins/TheBuilder.CacheTags",
    emptyOutDir: true,
    sourcemap: true,
    rollupOptions: { external: [/^@umbraco/] },
  },
});
