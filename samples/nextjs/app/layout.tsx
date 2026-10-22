import type { Metadata } from "next";
import Link from "next/link";
import "./globals.css";

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
          </nav>
        </header>
        <main>{children}</main>
      </body>
    </html>
  );
}
