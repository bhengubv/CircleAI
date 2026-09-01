/**
 * inputs/index
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// inputs/index.ts
//
// Full-parity port of CircleAI.Inputs (C#). C# is the exact spec.
//
// Input-adapter contracts: IWebScraper / IStealthHttpClient / IVideoIngest /
// IMcpWebScrape / ITerminalCast, the ScrapedPage / VideoIngestResult /
// McpScrapeJob / TerminalCast(+Segment) records, real offline adapters, and the
// Null* defaults.
//
// The C# HttpHtmlScraper / StealthHttpClient take an injected HttpClient. Per
// the porting contract the network is injected behind IHttpFetcher so the
// adapters are deterministic and need no real network. The HTML→text
// extraction, link resolution, stealth-header rotation, MCP wrapper, and
// asciinema-v2 cast parser are ported faithfully.
//
// Type mappings (C# → TS):
//   record                                → readonly interface (+ positional factory)
//   Uri                                   → URL
//   IReadOnlyDictionary<string,string>?   → Readonly<Map<string, string>> | null
//   IReadOnlyList<T>                       → readonly T[]
//   TimeSpan Duration / Offset            → number (milliseconds / seconds×1000)
//   ValueTask<T>                          → Promise<T>

import { Files as fs } from "../platform";

// ─────────────────────────────────────────────────────────────────────────────
// Injected network seam (replaces C# HttpClient)
// ─────────────────────────────────────────────────────────────────────────────

/** Minimal HTTP GET seam. Replaces the C# injected HttpClient. */
export interface IHttpFetcher {
  /**
   * Issues a GET for `url` with optional extra request headers and returns the
   * response body as text. Implementations throw on non-success status.
   */
  getStringAsync(url: URL, headers?: Readonly<Map<string, string>>): Promise<string>;
}

// ─────────────────────────────────────────────────────────────────────────────
// Records
// ─────────────────────────────────────────────────────────────────────────────

/** One scraped page. Mirrors C# `ScrapedPage`. */
export interface ScrapedPage {
  readonly url: URL;
  readonly text: string;
  readonly title: string | null;
  readonly metadata: Readonly<Map<string, string>> | null;
  readonly resolvedLinks: readonly URL[] | null;
}

/** Constructs a {@link ScrapedPage}. */
export function scrapedPage(
  url: URL,
  text: string,
  title: string | null = null,
  metadata: Readonly<Map<string, string>> | null = null,
  resolvedLinks: readonly URL[] | null = null,
): ScrapedPage {
  return { url, text, title, metadata, resolvedLinks };
}

/** A video ingest result. Mirrors C# `VideoIngestResult`. */
export interface VideoIngestResult {
  readonly transcript: string;
  readonly shots: readonly string[];
  /** Duration in milliseconds (C# `TimeSpan Duration`). */
  readonly durationMs: number;
  readonly frameCount: number;
}

/** Constructs a {@link VideoIngestResult}. */
export function videoIngestResult(
  transcript: string,
  shots: readonly string[],
  durationMs: number,
  frameCount: number,
): VideoIngestResult {
  return { transcript, shots, durationMs, frameCount };
}

/** An MCP scrape job. Mirrors C# `McpScrapeJob`. */
export interface McpScrapeJob {
  readonly url: string;
  readonly headers: Readonly<Map<string, string>> | null;
}

/** Constructs a {@link McpScrapeJob}. */
export function mcpScrapeJob(url: string, headers: Readonly<Map<string, string>> | null = null): McpScrapeJob {
  return { url, headers };
}

/** One terminal-cast segment. Mirrors C# `TerminalCastSegment`. */
export interface TerminalCastSegment {
  /** Offset in milliseconds from cast start (C# `TimeSpan Offset`). */
  readonly offsetMs: number;
  readonly text: string;
}

/** Constructs a {@link TerminalCastSegment}. */
export function terminalCastSegment(offsetMs: number, text: string): TerminalCastSegment {
  return { offsetMs, text };
}

/** A parsed terminal cast. Mirrors C# `TerminalCast`. */
export interface TerminalCast {
  readonly segments: readonly TerminalCastSegment[];
  readonly width: number;
  readonly height: number;
}

/** Constructs a {@link TerminalCast}. */
export function terminalCast(segments: readonly TerminalCastSegment[], width: number, height: number): TerminalCast {
  return { segments, width, height };
}

// ─────────────────────────────────────────────────────────────────────────────
// Contracts
// ─────────────────────────────────────────────────────────────────────────────

/** Convert a URL into text (ConvertX pattern). Mirrors C# `IWebScraper`. */
export interface IWebScraper {
  readonly backendId: string;
  fetchAsync(url: URL): Promise<ScrapedPage>;
}

/** Fingerprint-avoiding HTTP client (Scrapling pattern). Mirrors C# `IStealthHttpClient`. */
export interface IStealthHttpClient {
  readonly backendId: string;
  getAsync(url: URL, headers?: Readonly<Map<string, string>> | null): Promise<ScrapedPage>;
}

/** Video → model-ready text stream (openvid). Mirrors C# `IVideoIngest`. */
export interface IVideoIngest {
  readonly backendId: string;
  ingestAsync(filePath: string): Promise<VideoIngestResult>;
}

/** MCP-side delegated scraping. Mirrors C# `IMcpWebScrape`. */
export interface IMcpWebScrape {
  readonly backendId: string;
  scrapeAsync(job: McpScrapeJob): Promise<ScrapedPage>;
}

/** Parse/replay asciinema casts (ASCILINE). Mirrors C# `ITerminalCast`. */
export interface ITerminalCast {
  readonly backendId: string;
  loadAsync(filePath: string): Promise<TerminalCast>;
  renderTranscriptAsync(cast: TerminalCast): Promise<string>;
}

// ─────────────────────────────────────────────────────────────────────────────
// Real (offline) implementations
// ─────────────────────────────────────────────────────────────────────────────

const TITLE_RX = /<title>(.*?)<\/title>/is;
const SCRIPT_RX = /<(script|style)[^>]*>.*?<\/\1>/gis;
const TAG_RX = /<[^>]+>/g;
const HREF_RX = /href\s*=\s*["']([^"'#]+)["']/gi;
const WS_RX = /\s+/g;

/** HTML scraper with simple text extraction. Mirrors C# `HttpHtmlScraper`. */
export class HttpHtmlScraper implements IWebScraper {
  private readonly http: IHttpFetcher;

  constructor(http: IHttpFetcher) {
    if (http == null) throw new Error("http required");
    this.http = http;
  }

  get backendId(): string {
    return "http-html";
  }

  async fetchAsync(url: URL): Promise<ScrapedPage> {
    if (url == null) throw new Error("url required");
    const html = await this.http.getStringAsync(url);
    let title = firstMatch(TITLE_RX, html);
    if (title.length > 0) title = htmlDecode(title.trim());

    const stripped = html.replace(SCRIPT_RX, " ");
    let text = stripped.replace(TAG_RX, " ").replace(WS_RX, " ").trim();
    text = htmlDecode(text);

    const links: URL[] = [];
    for (const m of html.matchAll(HREF_RX)) {
      try {
        links.push(new URL(m[1], url));
      } catch {
        /* skip unparseable href */
      }
    }

    return scrapedPage(url, text, title.length === 0 ? null : title, null, links);
  }
}

const USER_AGENTS: readonly string[] = [
  "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36",
  "Mozilla/5.0 (Macintosh; Intel Mac OS X 14_2) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.2 Safari/605.1.15",
  "Mozilla/5.0 (X11; Linux x86_64; rv:121.0) Gecko/20100101 Firefox/121.0",
];
const ACCEPT_LANGUAGES: readonly string[] = ["en-US,en;q=0.9", "en-GB,en;q=0.9", "en-ZA,en;q=0.9"];

/** Stealth HTTP client — rotates headers per call. Mirrors C# `StealthHttpClient`. */
export class StealthHttpClient implements IStealthHttpClient {
  private readonly http: IHttpFetcher;
  private seq = 0;

  constructor(http: IHttpFetcher) {
    if (http == null) throw new Error("http required");
    this.http = http;
  }

  get backendId(): string {
    return "stealth-http";
  }

  async getAsync(url: URL, headers: Readonly<Map<string, string>> | null = null): Promise<ScrapedPage> {
    if (url == null) throw new Error("url required");
    const seq = ++this.seq;
    const reqHeaders: Map<string, string> = {
      "User-Agent": USER_AGENTS[seq % USER_AGENTS.length],
      "Accept-Language": ACCEPT_LANGUAGES[seq % ACCEPT_LANGUAGES.length],
      Accept: "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8",
      "Accept-Encoding": "gzip, deflate, br",
      "Cache-Control": "no-cache",
      Connection: "keep-alive",
    };
    if (headers !== null) {
      for (const [k, v] of Object.entries(headers)) reqHeaders[k] = v;
    }
    const body = await this.http.getStringAsync(url, reqHeaders);
    return scrapedPage(url, body);
  }
}

/** MCP-side scrape implementation wrapping a real scraper. Mirrors C# `DefaultMcpWebScrape`. */
export class DefaultMcpWebScrape implements IMcpWebScrape {
  private readonly inner: IWebScraper;

  constructor(inner: IWebScraper) {
    if (inner == null) throw new Error("inner required");
    this.inner = inner;
  }

  get backendId(): string {
    return `mcp:${this.inner.backendId}`;
  }

  async scrapeAsync(job: McpScrapeJob): Promise<ScrapedPage> {
    if (job == null) throw new Error("job required");
    return this.inner.fetchAsync(new URL(job.url));
  }
}

/** Parser for asciinema v2 cast files. Mirrors C# `AsciinemaTerminalCast`. */
export class AsciinemaTerminalCast implements ITerminalCast {
  get backendId(): string {
    return "asciinema";
  }

  async loadAsync(filePath: string): Promise<TerminalCast> {
    if (filePath == null || filePath.trim().length === 0) throw new Error("filePath required");
    let content: string;
    try {
      content = await fs.readFile(filePath, "utf8");
    } catch {
      throw new Error(`cast file not found: ${filePath}`);
    }

    let width = 80;
    let height = 24;
    const segments: TerminalCastSegment[] = [];

    const lines = content.split("\n");
    if (lines.length === 0 || (lines.length === 1 && lines[0].length === 0)) {
      throw new Error("empty cast file");
    }
    const first = lines[0];
    if (first.length === 0) throw new Error("empty cast file");
    try {
      const hdr = JSON.parse(first) as Object;
      if (isObject(hdr)) {
        if (typeof hdr.width === "number") width = hdr.width;
        if (typeof hdr.height === "number") height = hdr.height;
      }
    } catch {
      /* header optional / non-standard cast */
    }

    for (let i = 1; i < lines.length; i++) {
      const line = lines[i];
      if (line == null || line.trim().length === 0) continue;
      try {
        const arr = JSON.parse(line) as Object;
        if (!Array.isArray(arr) || arr.length < 3) continue;
        const t = arr[0] as number;
        const typ = arr[1] as string;
        const txt = (arr[2] as string) ?? "";
        if (typ === "o") segments.push(terminalCastSegment(secondsToMs(t), txt));
      } catch {
        /* skip malformed event */
      }
    }

    return terminalCast(segments, width, height);
  }

  async renderTranscriptAsync(cast: TerminalCast): Promise<string> {
    if (cast == null) throw new Error("cast required");
    let out = "";
    for (const s of cast.segments) out += s.text;
    return out;
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Null* defaults
// ─────────────────────────────────────────────────────────────────────────────

/** Fail-safe {@link IWebScraper}. */
export class NullWebScraper implements IWebScraper {
  static readonly instance = new NullWebScraper();
  get backendId(): string {
    return "null";
  }
  async fetchAsync(url: URL): Promise<ScrapedPage> {
    return scrapedPage(url, "");
  }
}

/** Fail-safe {@link IStealthHttpClient}. */
export class NullStealthHttpClient implements IStealthHttpClient {
  static readonly instance = new NullStealthHttpClient();
  get backendId(): string {
    return "null";
  }
  async getAsync(url: URL): Promise<ScrapedPage> {
    return scrapedPage(url, "");
  }
}

/** Fail-safe {@link IVideoIngest}. */
export class NullVideoIngest implements IVideoIngest {
  static readonly instance = new NullVideoIngest();
  get backendId(): string {
    return "null";
  }
  async ingestAsync(): Promise<VideoIngestResult> {
    return videoIngestResult("", [], 0, 0);
  }
}

/** Fail-safe {@link IMcpWebScrape}. */
export class NullMcpWebScrape implements IMcpWebScrape {
  static readonly instance = new NullMcpWebScrape();
  get backendId(): string {
    return "null";
  }
  async scrapeAsync(job: McpScrapeJob): Promise<ScrapedPage> {
    return scrapedPage(new URL(job.url), "");
  }
}

/** Fail-safe {@link ITerminalCast}. */
export class NullTerminalCast implements ITerminalCast {
  static readonly instance = new NullTerminalCast();
  get backendId(): string {
    return "null";
  }
  async loadAsync(): Promise<TerminalCast> {
    return terminalCast([], 80, 24);
  }
  async renderTranscriptAsync(): Promise<string> {
    return "";
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Helpers
// ─────────────────────────────────────────────────────────────────────────────

function firstMatch(rx: RegExp, text: string): string {
  const m = rx.exec(text);
  return m ? m[1] : "";
}

function isObject(v: Object): v is Map<string, unknown> {
  return typeof v === "object" && v !== null && !Array.isArray(v);
}

function secondsToMs(seconds: number): number {
  return Math.round(seconds * 1000);
}

/** Minimal HTML entity decode covering the entities the extractor emits. */
function htmlDecode(s: string): string {
  return s
    .replace(/&lt;/g, "<")
    .replace(/&gt;/g, ">")
    .replace(/&quot;/g, '"')
    .replace(/&#39;/g, "'")
    .replace(/&apos;/g, "'")
    .replace(/&nbsp;/g, " ")
    .replace(/&#(\d+);/g, (_m, d: string) => String.fromCodePoint(Number(d)))
    .replace(/&#x([0-9a-fA-F]+);/g, (_m, h: string) => String.fromCodePoint(parseInt(h, 16)))
    .replace(/&amp;/g, "&");
}
