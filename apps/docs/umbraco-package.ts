import { defineUmbracoPackage } from "@thebuilder/umbraco-docs";

export const cacheTagsPackage = defineUmbracoPackage({
  id: "thebuilder.cachetags",
  name: "Cache Tags",
  summary:
    "Tag Umbraco Delivery API responses with cache keys, and purge exactly the right ones once every server has applied a change.",
  links: {
    docs: "https://cache-tags.thebuilder.dk",
    nuget: "https://www.nuget.org/packages/TheBuilder.CacheTags",
    marketplace: "https://marketplace.umbraco.com/package/thebuilder.cachetags",
    github: "https://github.com/thebuilder/umbraco-cache-tags",
  },
  logo: "/logo-mark.svg",
  compatibility: { umbraco: ">=17.4 <19", dotnet: ">=10" },
  status: "preview",
  categories: ["Headless", "Developer Tools"],
});
