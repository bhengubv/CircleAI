// integration_news.test.ts
// Verifies the CircleAI.Integration.News port: Bluesky search-posts (title
// truncation, at:// → bsky.app URL, tag facets), Mastodon (HTML strip, hashtag
// path), NewsAPI (articles shape, header auth), and the RSS/Atom reader, against
// a fake IHttpClient.

import { describe, it } from "node:test";
import assert from "node:assert/strict";
import type { HttpRequest, HttpResponse, IHttpClient } from "../src/integration/index";
import { DateTimeOffsetMinValue } from "../src/integration/index";
import {
  BlueskySource,
  MastodonSource,
  NewsApiSource,
  RssNewsSource,
  blueskyOptions,
  mastodonOptions,
  newsApiOptions,
  rssOptions,
} from "../src/integration/news/index";

class FakeHttp implements IHttpClient {
  readonly requests: HttpRequest[] = [];
  constructor(private handler: (r: HttpRequest) => HttpResponse) {}
  send(request: HttpRequest): Promise<HttpResponse> {
    this.requests.push(request);
    return Promise.resolve(this.handler(request));
  }
}
const ok = (body: string): HttpResponse => ({ statusCode: 200, body });

describe("BlueskySource", () => {
  it("sourceId/isConfigured", () => {
    const c = new BlueskySource(blueskyOptions("climate"), new FakeHttp(() => ok("{}")));
    assert.equal(c.sourceId, "bluesky:climate");
    assert.equal(c.isConfigured, true);
    assert.equal(new BlueskySource(blueskyOptions("  "), new FakeHttp(() => ok("{}"))).isConfigured, false);
  });

  it("parses posts: author handle as source, truncates title, builds bsky URL, reads tag facets", async () => {
    const longText = "x".repeat(100);
    const http = new FakeHttp((r) => {
      assert.ok(r.url.includes("app.bsky.feed.searchPosts"));
      assert.ok(r.url.includes("q=climate"));
      assert.ok(r.url.includes("sort=latest"));
      return ok(
        JSON.stringify({
          posts: [
            {
              uri: "at://did:plc:abc/app.bsky.feed.post/rkey123",
              author: { handle: "alice.bsky.social" },
              record: {
                text: longText,
                createdAt: "2026-07-10T08:00:00Z",
                facets: [{ features: [{ tag: "climate" }, { tag: "news" }] }],
              },
            },
          ],
        }),
      );
    });
    const c = new BlueskySource(blueskyOptions("climate"), http);
    const [item] = await c.fetchLatestAsync(20);
    assert.equal(item.itemId, "at://did:plc:abc/app.bsky.feed.post/rkey123");
    assert.equal(item.sourceId, "alice.bsky.social");
    assert.equal(item.title, "x".repeat(80) + "…"); // truncated at 80
    assert.equal(item.summary, longText);
    assert.equal(item.url, "https://bsky.app/profile/alice.bsky.social/post/rkey123");
    assert.deepEqual(item.tags, ["climate", "news"]);
    assert.equal(item.publishedUtc.toISOString(), "2026-07-10T08:00:00.000Z");
  });

  it("falls back to sourceId + about:blank + MinValue when fields are missing", async () => {
    const http = new FakeHttp(() => ok(JSON.stringify({ posts: [{ uri: "at://x", record: { text: "hi" } }] })));
    const c = new BlueskySource(blueskyOptions("q"), http);
    const [item] = await c.fetchLatestAsync(5);
    assert.equal(item.sourceId, "bluesky:q");
    assert.equal(item.url, "about:blank"); // no handle → blank
    assert.equal(item.publishedUtc.getTime(), DateTimeOffsetMinValue.getTime());
  });

  it("rejects max <= 0", async () => {
    const c = new BlueskySource(blueskyOptions("q"), new FakeHttp(() => ok("{}")));
    await assert.rejects(() => c.fetchLatestAsync(0), /max/);
  });
});

describe("MastodonSource", () => {
  it("sourceId reflects public vs hashtag", () => {
    assert.equal(new MastodonSource(mastodonOptions("https://mas.to"), new FakeHttp(() => ok("[]"))).sourceId, "mastodon:https://mas.to:public");
    assert.equal(
      new MastodonSource(mastodonOptions("https://mas.to", "tech"), new FakeHttp(() => ok("[]"))).sourceId,
      "mastodon:https://mas.to:#tech",
    );
  });

  it("public timeline: strips HTML, uses acct as source, sets bearer + UA", async () => {
    const http = new FakeHttp((r) => {
      assert.ok(r.url.endsWith("/api/v1/timelines/public?limit=40"));
      assert.equal(r.headers.get("Authorization"), "Bearer tok");
      assert.equal(r.headers.get("User-Agent"), "CircleAI/1.0 (MastodonSource)");
      return ok(
        JSON.stringify([
          {
            url: "https://mas.to/@bob/1",
            content: "<p>Hello <b>world</b></p>",
            created_at: "2026-07-10T09:00:00Z",
            account: { acct: "bob" },
            tags: [{ name: "intro" }],
          },
        ]),
      );
    });
    const c = new MastodonSource(mastodonOptions("https://mas.to/", null, "tok"), http);
    const [item] = await c.fetchLatestAsync(40);
    assert.equal(item.itemId, "https://mas.to/@bob/1");
    assert.equal(item.sourceId, "bob");
    assert.equal(item.summary, "Hello  world"); // tags stripped
    assert.equal(item.url, "https://mas.to/@bob/1");
    assert.deepEqual(item.tags, ["intro"]);
  });

  it("hashtag timeline path is used when a hashtag is set", async () => {
    let url = "";
    const http = new FakeHttp((r) => {
      url = r.url;
      return ok("[]");
    });
    const c = new MastodonSource(mastodonOptions("https://mas.to", "tech"), http);
    await c.fetchLatestAsync(10);
    assert.ok(url.endsWith("/api/v1/timelines/tag/tech?limit=10"));
  });
});

describe("NewsApiSource", () => {
  it("sourceId/isConfigured require an api key", () => {
    assert.equal(new NewsApiSource(newsApiOptions("k", "ai"), new FakeHttp(() => ok("{}"))).sourceId, "newsapi:ai");
    assert.equal(new NewsApiSource(newsApiOptions("k", "ai"), new FakeHttp(() => ok("{}"))).isConfigured, true);
    assert.equal(new NewsApiSource(newsApiOptions("", "ai"), new FakeHttp(() => ok("{}"))).isConfigured, false);
  });

  it("maps the articles array with source name + published date", async () => {
    const http = new FakeHttp((r) => {
      assert.equal(r.headers.get("X-Api-Key"), "secret");
      assert.ok(r.url.includes("sortBy=publishedAt"));
      return ok(
        JSON.stringify({
          articles: [
            {
              title: "Headline",
              description: "desc",
              url: "https://news.example/a",
              publishedAt: "2026-07-10T07:30:00Z",
              source: { name: "Example News" },
            },
          ],
        }),
      );
    });
    const c = new NewsApiSource(newsApiOptions("secret", "ai"), http);
    const [item] = await c.fetchLatestAsync(20);
    assert.equal(item.itemId, "https://news.example/a");
    assert.equal(item.sourceId, "Example News");
    assert.equal(item.title, "Headline");
    assert.equal(item.summary, "desc");
    assert.equal(item.url, "https://news.example/a");
    assert.deepEqual(item.tags, []);
    assert.equal(item.publishedUtc.toISOString(), "2026-07-10T07:30:00.000Z");
  });

  it("throws when not configured", async () => {
    const c = new NewsApiSource(newsApiOptions("", "ai"), new FakeHttp(() => ok("{}")));
    await assert.rejects(() => c.fetchLatestAsync(5), /not configured/);
  });
});

describe("RssNewsSource", () => {
  it("sourceId defaults to the feed host", () => {
    const c = new RssNewsSource(rssOptions("https://blog.example.com/feed.xml"), new FakeHttp(() => ok("")));
    assert.equal(c.sourceId, "blog.example.com");
    assert.equal(c.isConfigured, true);
    assert.equal(new RssNewsSource(rssOptions("https://x/f", "custom"), new FakeHttp(() => ok(""))).sourceId, "custom");
  });

  it("parses RSS 2.0 items with categories, guid, and pubDate", async () => {
    const xml = `<?xml version="1.0"?>
<rss version="2.0"><channel>
  <title>Feed</title>
  <item>
    <title>First Post</title>
    <link>https://blog.example.com/1</link>
    <guid>guid-1</guid>
    <pubDate>Thu, 10 Jul 2026 06:00:00 GMT</pubDate>
    <description><![CDATA[<p>Body &amp; more</p>]]></description>
    <category>tech</category>
    <category>news</category>
  </item>
</channel></rss>`;
    const c = new RssNewsSource(rssOptions("https://blog.example.com/feed.xml"), new FakeHttp(() => ok(xml)));
    const items = await c.fetchLatestAsync(20);
    assert.equal(items.length, 1);
    const it = items[0];
    assert.equal(it.itemId, "guid-1");
    assert.equal(it.sourceId, "blog.example.com");
    assert.equal(it.title, "First Post");
    // CDATA content is verbatim in XDocument (& amp; stays literal), then tags stripped.
    assert.equal(it.summary, "Body &amp; more");
    assert.equal(it.url, "https://blog.example.com/1");
    assert.deepEqual(it.tags, ["tech", "news"]);
    assert.equal(it.publishedUtc.toISOString(), "2026-07-10T06:00:00.000Z");
  });

  it("decodes ordinary (non-CDATA) entities in element text", async () => {
    // Outside CDATA, XDocument decodes &amp; → & ; the port must match.
    const xml = `<rss version="2.0"><channel><item>
      <title>Tom &amp; Jerry</title>
      <link>https://x/tj</link>
      <description>A &lt;b&gt;bold&lt;/b&gt; &amp; italic tale</description>
    </item></channel></rss>`;
    const c = new RssNewsSource(rssOptions("https://x/feed"), new FakeHttp(() => ok(xml)));
    const [it] = await c.fetchLatestAsync(5);
    assert.equal(it.title, "Tom & Jerry");
    // &lt;b&gt; decodes to <b>, which Strip then removes; &amp; → &.
    // "A <b>bold</b> & italic tale" → tags→spaces → "A  bold  & italic tale".
    assert.equal(it.summary, "A  bold  & italic tale");
  });

  it("parses Atom 1.0 entries with link href, term categories, updated date", async () => {
    const xml = `<?xml version="1.0" encoding="utf-8"?>
<feed xmlns="http://www.w3.org/2005/Atom">
  <title>Atom Feed</title>
  <entry>
    <title>Atom Post</title>
    <link href="https://atom.example.com/e1"/>
    <id>tag:atom,2026:e1</id>
    <updated>2026-07-10T05:00:00Z</updated>
    <summary>Summary text</summary>
    <category term="alpha"/>
    <category term=""/>
  </entry>
</feed>`;
    const c = new RssNewsSource(rssOptions("https://atom.example.com/feed"), new FakeHttp(() => ok(xml)));
    const [it] = await c.fetchLatestAsync(20);
    assert.equal(it.itemId, "tag:atom,2026:e1");
    assert.equal(it.title, "Atom Post");
    assert.equal(it.url, "https://atom.example.com/e1");
    assert.equal(it.summary, "Summary text");
    assert.deepEqual(it.tags, ["alpha"]); // empty term filtered out
    assert.equal(it.publishedUtc.toISOString(), "2026-07-10T05:00:00.000Z");
  });

  it("respects max across concatenated RSS+Atom results", async () => {
    const xml = `<rss version="2.0"><channel>
      <item><title>A</title><link>https://x/a</link></item>
      <item><title>B</title><link>https://x/b</link></item>
      <item><title>C</title><link>https://x/c</link></item>
    </channel></rss>`;
    const c = new RssNewsSource(rssOptions("https://x/feed"), new FakeHttp(() => ok(xml)));
    const items = await c.fetchLatestAsync(2);
    assert.deepEqual(items.map((i) => i.title), ["A", "B"]);
  });
});
