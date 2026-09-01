/**
 * integration/news/index
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// integration/news/index.ts
// Full-parity port of CircleAI.Integration.News (C#). C# is the exact spec.
//
// Four INewsSource implementations:
//   BlueskySource   — Bluesky AT-proto app.bsky.feed.searchPosts reader.
//   MastodonSource  — Mastodon public / hashtag timeline reader.
//   NewsApiSource   — newsapi.org / gnews.io "articles"-shape adapter.
//   RssNewsSource   — generic RSS 2.0 + Atom 1.0 reader.
//
// All take the injected `IHttpClient` transport (the C# takes an `HttpClient`).

import {
  type NewsItem,
  type INewsSource,
  type IHttpClient,
  newsItem,
  ensureSuccess,
  isNullOrWhiteSpace,
  isNullOrEmpty,
  DateTimeOffsetMinValue,
} from "../index";
import { tryParseDate } from "../calendar/index";

// ── Shared helpers ────────────────────────────────────────────────────────────

/** C# `DateTimeOffset.TryParse(s, AssumeUniversal).ToUniversalTime()` or MinValue. */
function parseDateOrMin(s: string | null | undefined): Date {
  if (s == null) return DateTimeOffsetMinValue;
  const d = tryParseDate(s);
  return d !== null ? d : DateTimeOffsetMinValue;
}

/** C# `Regex.Replace(html, "<[^>]+>", " ").Trim()` — strips tags, collapses to a space. */
function stripTags(html: string): string {
  return html.replace(/<[^>]+>/g, " ").trim();
}

/**
 * C# string slice on a code-unit index: `text.Length > 80 ? text[..80] + "…" : text`.
 * JS string indexing is UTF-16 code units, matching C# `string` semantics.
 */
function truncate80(text: string): string {
  return text.length > 80 ? text.slice(0, 80) + "…" : text;
}

/** C# `Uri.TryCreate(s, Absolute)` ? that : new Uri("about:blank") — returns a URL string. */
function absoluteUrlOrBlank(s: string): string {
  if (isNullOrWhiteSpace(s)) return "about:blank";
  try {
    // Absolute requires a scheme; URL() with no base throws for relative inputs.
    const u = new URL(s);
    return u.toString();
  } catch {
    return "about:blank";
  }
}

// ── Bluesky ───────────────────────────────────────────────────────────────────

/** Options for {@link BlueskySource}. Mirrors C# `BlueskyOptions`. */
export interface BlueskyOptions {
  readonly query: string;
  /** AppView host. Default the public Bluesky API. */
  readonly host: string;
}

/** Constructs {@link BlueskyOptions} (default host "https://public.api.bsky.app"). */
export function blueskyOptions(query: string, host = "https://public.api.bsky.app"): BlueskyOptions {
  return { query, host };
}

/** Bluesky search-posts reader. Faithful port of C# `BlueskySource`. */
export class BlueskySource implements INewsSource {
  private readonly http: IHttpClient;
  private readonly opts: BlueskyOptions;

  constructor(opts: BlueskyOptions, http: IHttpClient) {
    if (opts == null) throw new Error("opts required");
    if (http == null) throw new Error("http required");
    this.opts = opts;
    this.http = http;
  }

  get sourceId(): string {
    return `bluesky:${this.opts.query}`;
  }
  get isConfigured(): boolean {
    return !isNullOrWhiteSpace(this.opts.query);
  }

  async fetchLatestAsync(max: number, ct?: AbortSignal): Promise<readonly NewsItem[]> {
    if (max <= 0) throw new Error("max");
    const url =
      `${this.opts.host}/xrpc/app.bsky.feed.searchPosts` +
      `?q=${encodeURIComponent(this.opts.query)}&limit=${Math.min(max, 100)}&sort=latest`;
    const resp = ensureSuccess(await this.http.send({ method: "GET", url, headers: new Map() }, ct));
    const root = JSON.parse(resp.body) as Map<string, unknown>;

    const list: NewsItem[] = [];
    const arr = root["posts"];
    if (!Array.isArray(arr)) return list;
    for (const postUnknown of arr) {
      const post = postUnknown as Map<string, unknown>;
      const uri = typeof post["uri"] === "string" ? (post["uri"] as string) : "";
      const record = post["record"];
      const recObj = record != null && typeof record === "object" && !Array.isArray(record) ? (record as Map<string, unknown>) : undefined;
      const text = recObj !== undefined && typeof recObj["text"] === "string" ? (recObj["text"] as string) : "";
      const ts = recObj !== undefined && typeof recObj["createdAt"] === "string" ? (recObj["createdAt"] as string) : null;
      let author: string | null = null;
      const a = post["author"];
      if (a != null && typeof a === "object" && typeof (a as Map<string, unknown>)["handle"] === "string") {
        author = (a as Map<string, unknown>)["handle"] as string;
      }
      const tags: string[] = [];
      if (recObj !== undefined) {
        const facets = recObj["facets"];
        if (Array.isArray(facets)) {
          for (const fUnknown of facets) {
            const f = fUnknown as Map<string, unknown>;
            const feats = f["features"];
            if (Array.isArray(feats)) {
              for (const featUnknown of feats) {
                const feat = featUnknown as Map<string, unknown>;
                if ("tag" in feat) tags.push(typeof feat["tag"] === "string" ? (feat["tag"] as string) : "");
              }
            }
          }
        }
      }
      list.push(
        newsItem(
          uri,
          author ?? this.sourceId,
          truncate80(text),
          text,
          buildPostUrl(author, uri),
          parseDateOrMin(ts),
          tags,
        ),
      );
    }
    return list;
  }
}

/** C# `BlueskySource.BuildPostUrl` — at:// URI → bsky.app profile post URL. */
function buildPostUrl(handle: string | null, atUri: string): string {
  if (isNullOrWhiteSpace(handle) || isNullOrWhiteSpace(atUri)) return "about:blank";
  const idx = atUri.lastIndexOf("/");
  if (idx < 0 || idx === atUri.length - 1) return "about:blank";
  const rkey = atUri.slice(idx + 1);
  return `https://bsky.app/profile/${handle}/post/${rkey}`;
}

// ── Mastodon ──────────────────────────────────────────────────────────────────

/** Options for {@link MastodonSource}. Mirrors C# `MastodonOptions`. */
export interface MastodonOptions {
  readonly instance: string;
  readonly hashtag: string | null;
  readonly accessToken: string | null;
}

/** Constructs {@link MastodonOptions}. */
export function mastodonOptions(instance: string, hashtag: string | null = null, accessToken: string | null = null): MastodonOptions {
  return { instance, hashtag, accessToken };
}

/** Mastodon timeline reader. Faithful port of C# `MastodonSource`. */
export class MastodonSource implements INewsSource {
  private readonly http: IHttpClient;
  private readonly opts: MastodonOptions;

  constructor(opts: MastodonOptions, http: IHttpClient) {
    if (opts == null) throw new Error("opts required");
    if (http == null) throw new Error("http required");
    this.opts = opts;
    this.http = http;
  }

  get sourceId(): string {
    return isNullOrEmpty(this.opts.hashtag)
      ? `mastodon:${this.opts.instance}:public`
      : `mastodon:${this.opts.instance}:#${this.opts.hashtag}`;
  }
  get isConfigured(): boolean {
    return !isNullOrWhiteSpace(this.opts.instance);
  }

  async fetchLatestAsync(max: number, ct?: AbortSignal): Promise<readonly NewsItem[]> {
    if (max <= 0) throw new Error("max");
    const path = isNullOrEmpty(this.opts.hashtag)
      ? `/api/v1/timelines/public?limit=${Math.min(max, 40)}`
      : `/api/v1/timelines/tag/${encodeURIComponent(this.opts.hashtag as string)}?limit=${Math.min(max, 40)}`;
    const headers = new Map<string, string>([["User-Agent", "CircleAI/1.0 (MastodonSource)"]]);
    if (!isNullOrWhiteSpace(this.opts.accessToken)) headers.set("Authorization", `Bearer ${this.opts.accessToken}`);
    const url = this.opts.instance.replace(/\/+$/, "") + path;
    const resp = ensureSuccess(await this.http.send({ method: "GET", url, headers }, ct));
    const root = JSON.parse(resp.body) as Object;

    if (!Array.isArray(root)) return [];
    const list: NewsItem[] = [];
    for (const sUnknown of root) {
      const s = sUnknown as Map<string, unknown>;
      const url2 = typeof s["url"] === "string" ? (s["url"] as string) : "";
      const contentHtml = typeof s["content"] === "string" ? (s["content"] as string) : "";
      const pub = typeof s["created_at"] === "string" ? (s["created_at"] as string) : null;
      const tags: string[] = [];
      const tagsArr = s["tags"];
      if (Array.isArray(tagsArr)) {
        for (const tgUnknown of tagsArr) {
          const tg = tgUnknown as Map<string, unknown>;
          if ("name" in tg) tags.push(typeof tg["name"] === "string" ? (tg["name"] as string) : "");
        }
      }
      let acct: string | null = null;
      const a = s["account"];
      if (a != null && typeof a === "object" && typeof (a as Map<string, unknown>)["acct"] === "string") {
        acct = (a as Map<string, unknown>)["acct"] as string;
      }
      const text = stripTags(contentHtml);
      list.push(
        newsItem(url2, acct ?? this.sourceId, truncate80(text), text, absoluteUrlOrBlank(url2), parseDateOrMin(pub), tags),
      );
    }
    return list;
  }
}

// ── NewsAPI / GNews ───────────────────────────────────────────────────────────

/** Options for {@link NewsApiSource}. Mirrors C# `NewsApiOptions`. */
export interface NewsApiOptions {
  readonly apiKey: string;
  readonly query: string;
  /** REST endpoint. Default newsapi.org /v2/everything. */
  readonly endpoint: string;
}

/** Constructs {@link NewsApiOptions} (default endpoint "https://newsapi.org/v2/everything"). */
export function newsApiOptions(apiKey: string, query: string, endpoint = "https://newsapi.org/v2/everything"): NewsApiOptions {
  return { apiKey, query, endpoint };
}

/** NewsAPI / GNews adapter. Faithful port of C# `NewsApiSource`. */
export class NewsApiSource implements INewsSource {
  private readonly http: IHttpClient;
  private readonly opts: NewsApiOptions;

  constructor(opts: NewsApiOptions, http: IHttpClient) {
    if (opts == null) throw new Error("opts required");
    if (http == null) throw new Error("http required");
    this.opts = opts;
    this.http = http;
  }

  get sourceId(): string {
    return `newsapi:${this.opts.query}`;
  }
  get isConfigured(): boolean {
    return !isNullOrWhiteSpace(this.opts.apiKey);
  }

  async fetchLatestAsync(max: number, ct?: AbortSignal): Promise<readonly NewsItem[]> {
    if (max <= 0) throw new Error("max");
    if (!this.isConfigured) throw new Error("NewsAPI key not configured.");
    const url = `${this.opts.endpoint}?q=${encodeURIComponent(this.opts.query)}&pageSize=${Math.min(max, 100)}&sortBy=publishedAt&language=en`;
    const headers = new Map<string, string>([
      ["X-Api-Key", this.opts.apiKey],
      ["User-Agent", "CircleAI/1.0 (NewsApiSource)"],
    ]);
    const resp = ensureSuccess(await this.http.send({ method: "GET", url, headers }, ct));
    const root = JSON.parse(resp.body) as Map<string, unknown>;

    const list: NewsItem[] = [];
    const arr = root["articles"];
    if (Array.isArray(arr)) {
      for (const aUnknown of arr) {
        const a = aUnknown as Map<string, unknown>;
        const title = typeof a["title"] === "string" ? (a["title"] as string) : "";
        const desc = typeof a["description"] === "string" ? (a["description"] as string) : "";
        const url2 = typeof a["url"] === "string" ? (a["url"] as string) : "";
        const pub = typeof a["publishedAt"] === "string" ? (a["publishedAt"] as string) : null;
        let src: string | null = null;
        const s = a["source"];
        if (s != null && typeof s === "object" && typeof (s as Map<string, unknown>)["name"] === "string") {
          src = (s as Map<string, unknown>)["name"] as string;
        }
        list.push(newsItem(url2, src ?? this.sourceId, title, desc, absoluteUrlOrBlank(url2), parseDateOrMin(pub), []));
      }
    }
    return list;
  }
}

// ── RSS 2.0 / Atom 1.0 ────────────────────────────────────────────────────────

/** Options for {@link RssNewsSource}. Mirrors C# `RssOptions`. */
export interface RssOptions {
  readonly feedUrl: string;
  readonly sourceId: string | null;
}

/** Constructs {@link RssOptions}. */
export function rssOptions(feedUrl: string, sourceId: string | null = null): RssOptions {
  return { feedUrl, sourceId };
}

/** Generic RSS/Atom reader. Faithful port of C# `RssNewsSource`. */
export class RssNewsSource implements INewsSource {
  private readonly http: IHttpClient;
  private readonly opts: RssOptions;

  constructor(opts: RssOptions, http: IHttpClient) {
    if (opts == null) throw new Error("opts required");
    if (http == null) throw new Error("http required");
    this.opts = opts;
    this.http = http;
  }

  get sourceId(): string {
    if (this.opts.sourceId !== null) return this.opts.sourceId;
    // C#: _opts.FeedUrl.Host
    try {
      return new URL(this.opts.feedUrl).host;
    } catch {
      return this.opts.feedUrl;
    }
  }
  get isConfigured(): boolean {
    return true;
  }

  async fetchLatestAsync(max: number, ct?: AbortSignal): Promise<readonly NewsItem[]> {
    if (max <= 0) throw new Error("max");
    const resp = ensureSuccess(await this.http.send({ method: "GET", url: this.opts.feedUrl, headers: new Map() }, ct));
    const xml = resp.body;
    const sid = this.sourceId;
    // C#: ParseRss(doc).Concat(ParseAtom(doc)).Take(max)
    const items = [...parseRss(xml, sid), ...parseAtom(xml, sid)].slice(0, max);
    return items;
  }
}

// The C# uses XDocument. We parse the two well-defined element shapes with a
// small tag reader — sufficient for RSS <item> and Atom <entry> like the C#'s
// Descendants()/Element()/Attribute() calls, and dependency-free.

interface XmlNode {
  readonly name: string; // local name (namespace prefix stripped)
  readonly attrs: ReadonlyMap<string, string>;
  readonly children: XmlNode[];
  text: string; // concatenated direct text content, entity-decoded
}

function* parseRss(xml: string, sourceId: string): Generator<NewsItem> {
  const doc = parseXml(xml);
  for (const item of descendants(doc, "item")) {
    const title = childText(item, "title") ?? "";
    const link = childText(item, "link") ?? "";
    const pub = childText(item, "pubDate");
    const desc = childText(item, "description") ?? "";
    const guid = childText(item, "guid") ?? link;
    const tags = childElements(item, "category").map((c) => c.text);
    yield newsItem(guid, sourceId, title, stripTags(desc), absoluteUrlOrBlank(link), parseDateOrMin(pub), tags);
  }
}

function* parseAtom(xml: string, sourceId: string): Generator<NewsItem> {
  const doc = parseXml(xml);
  for (const entry of descendants(doc, "entry")) {
    const title = childText(entry, "title") ?? "";
    const linkEl = childElements(entry, "link")[0];
    const link = linkEl !== undefined ? linkEl.attrs.get("href") ?? "" : "";
    const pub = childText(entry, "updated") ?? childText(entry, "published") ?? null;
    const desc = childText(entry, "summary") ?? childText(entry, "content") ?? "";
    const guid = childText(entry, "id") ?? link;
    const tags = childElements(entry, "category")
      .map((c) => c.attrs.get("term") ?? "")
      .filter((t) => !isNullOrEmpty(t));
    yield newsItem(guid, sourceId, title, stripTags(desc), absoluteUrlOrBlank(link), parseDateOrMin(pub), tags);
  }
}

// ── Minimal XML reader ────────────────────────────────────────────────────────

function decodeXmlEntities(s: string): string {
  return s
    .replace(/<!\[CDATA\[([\s\S]*?)\]\]>/g, "$1")
    .replace(/&lt;/g, "<")
    .replace(/&gt;/g, ">")
    .replace(/&quot;/g, '"')
    .replace(/&apos;/g, "'")
    .replace(/&#x([0-9a-fA-F]+);/g, (_m, h: string) => String.fromCodePoint(parseInt(h, 16)))
    .replace(/&#(\d+);/g, (_m, d: string) => String.fromCodePoint(parseInt(d, 10)))
    .replace(/&amp;/g, "&");
}

function localName(qname: string): string {
  const i = qname.indexOf(":");
  return i >= 0 ? qname.slice(i + 1) : qname;
}

/** Parses XML into a synthetic root node holding the document's top-level elements. */
function parseXml(xml: string): XmlNode {
  const root: XmlNode = { name: "#root", attrs: new Map(), children: [], text: "" };
  const stack: XmlNode[] = [root];
  // Strip comments, processing instructions, and the XML/DOCTYPE declarations.
  const cleaned = xml
    .replace(/<!--[\s\S]*?-->/g, "")
    .replace(/<\?[\s\S]*?\?>/g, "")
    .replace(/<!DOCTYPE[^>]*>/gi, "");
  const tagRx = /<\/?([A-Za-z_][\w.\-:]*)((?:\s+[^<>]*?)?)\/?>|<!\[CDATA\[([\s\S]*?)\]\]>|([^<]+)/g;
  let m: RegExpExecArray | null;
  while ((m = tagRx.exec(cleaned)) !== null) {
    const full = m[0];
    if (m[3] !== undefined) {
      // CDATA text
      const top = stack[stack.length - 1];
      top.text += m[3];
      continue;
    }
    if (m[4] !== undefined) {
      // Character data
      const top = stack[stack.length - 1];
      top.text += decodeXmlEntities(m[4]);
      continue;
    }
    const name = localName(m[1]);
    const isClose = full.startsWith("</");
    const isSelfClose = /\/>\s*$/.test(full);
    if (isClose) {
      // Pop to the matching element (by local name).
      for (let i = stack.length - 1; i > 0; i--) {
        if (stack[i].name === name) {
          stack.length = i;
          break;
        }
      }
      continue;
    }
    const attrs = parseAttrs(m[2] ?? "");
    const node: XmlNode = { name, attrs, children: [], text: "" };
    stack[stack.length - 1].children.push(node);
    if (!isSelfClose) stack.push(node);
  }
  return root;
}

function parseAttrs(s: string): Map<string, string> {
  const attrs = new Map<string, string>();
  const rx = /([A-Za-z_][\w.\-:]*)\s*=\s*"([^"]*)"|([A-Za-z_][\w.\-:]*)\s*=\s*'([^']*)'/g;
  let m: RegExpExecArray | null;
  while ((m = rx.exec(s)) !== null) {
    const key = localName(m[1] ?? m[3]);
    const val = decodeXmlEntities(m[2] ?? m[4] ?? "");
    if (!attrs.has(key)) attrs.set(key, val);
  }
  return attrs;
}

/** All descendant elements with the given local name (mirrors XDocument.Descendants). */
function descendants(node: XmlNode, name: string): XmlNode[] {
  const out: XmlNode[] = [];
  const walk = (n: XmlNode): void => {
    for (const c of n.children) {
      if (c.name === name) out.push(c);
      walk(c);
    }
  };
  walk(node);
  return out;
}

/** First direct child element with the given local name (mirrors XElement.Element). */
function child(node: XmlNode, name: string): XmlNode | undefined {
  return node.children.find((c) => c.name === name);
}

/** Text of the first direct child element, or undefined if absent (mirrors `.Element(x)?.Value`). */
function childText(node: XmlNode, name: string): string | undefined {
  const c = child(node, name);
  return c !== undefined ? c.text : undefined;
}

/** Direct child elements with the given local name (mirrors XElement.Elements). */
function childElements(node: XmlNode, name: string): XmlNode[] {
  return node.children.filter((c) => c.name === name);
}
