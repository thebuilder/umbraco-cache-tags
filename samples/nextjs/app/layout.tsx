import type { Metadata } from "next";
import Link from "next/link";
import { connection } from "next/server";
import { dictionary } from "@/lib/umbraco";
import "./globals.css";

// Pages wait on the server for their (cached) Delivery API responses rather than streaming a
// loading state first: the sample shows each page complete, as a visitor would see it.
export const instant = false;

export const metadata: Metadata = {
  title: "Cache Tags example",
  description: "A Next.js frontend for the TheBuilder.CacheTags Umbraco sample.",
};

export default function RootLayout({ children }: Readonly<{ children: React.ReactNode }>) {
  return (
    <html lang="en">
      <body>
        <header>
          <nav>
            <Link href="/">Home</Link>
            <Link href="/articles">Articles</Link>
            <Link href="/en-us/about">About</Link>
            <Link href="/da-dk/om-os">Om os</Link>
            <Link href="/guides">Guides</Link>
            <Link href="/blocks">Blocks</Link>
            <Link href="/photo-essay">Photo essay</Link>
            <Link href="/en-us/events">Events</Link>
            <Link href="/da-dk/events">Begivenheder</Link>
            <Link href="/search">Search</Link>
          </nav>
        </header>
        <main>{children}</main>
        <Footer />
      </body>
    </html>
  );
}

/** The tagline from Umbraco's dictionary. Left out while Umbraco is unreachable: the layout cannot show an error page. */
async function Footer() {
  // Rendered per request, from the cached response, so the build does not need Umbraco.
  await connection();
  try {
    const { data, fetchedAt } = await dictionary("en-US");
    return data?.["Footer.Tagline"] ? (
      <footer data-fetched-at={fetchedAt}>
        <p>{data["Footer.Tagline"]}</p>
      </footer>
    ) : null;
  } catch {
    return null;
  }
}
