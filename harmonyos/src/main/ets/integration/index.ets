/**
 * integration/index
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// integration/index.ts
// Full-parity port of CircleAI.Integration (Contracts.cs). C# is the exact spec.
//
// Shared abstractions for the external-integration layer. Calendar, email, news,
// weather and home-automation providers all implement these so the Companion's
// ProactiveBriefingService can stitch a coherent "what's happening" picture
// without coupling to specific providers.
//
// Type mappings (C# → TS):
//   sealed record                → readonly interface (+ positional factory)
//   DateTimeOffset               → Date (UTC instant)
//   TimeSpan                     → number (milliseconds), to mirror Date arithmetic
//   IReadOnlyList<string>        → readonly string[]
//   IReadOnlyDictionary<K,V>     → ReadonlyMap<K,V>
//   (double Lat, double Lon)     → readonly { lat: number; lon: number }
//   ValueTask<T>                 → Promise<T>
//   CancellationToken            → AbortSignal (optional; cooperative)
//
// EXTERNAL-DEPENDENCY INJECTION (no real network — the coordinator's rule):
// The C# connectors take an injected `HttpClient`. We mirror that with the
// `IHttpClient` transport abstraction below: connectors build requests + parse
// responses exactly as the C# does, but run deterministically against any
// injected transport (a fake in tests, a real fetch-backed one in production).
// `HttpResponseError` mirrors `HttpResponseMessage.EnsureSuccessStatusCode()`.

// ── DateTimeOffset.MinValue parity ───────────────────────────────────────────

/**
 * The instant equal to C# `DateTimeOffset.MinValue` (0001-01-01T00:00:00Z).
 * Used as the "unparseable / absent timestamp" sentinel, exactly as the C#
 * connectors return it. Stable value: -62135596800000 ms.
 */
export const DateTimeOffsetMinValue: Date = new Date("0001-01-01T00:00:00Z");

/** True when `d` equals {@link DateTimeOffsetMinValue}. */
export function isMinValue(d: Date): boolean {
  return d.getTime() === DateTimeOffsetMinValue.getTime();
}

// ── HTTP transport abstraction (injected external dependency) ─────────────────

/** HTTP method verb (includes CalDAV's REPORT + Graph's PATCH). */
export type HttpMethod = "GET" | "POST" | "PUT" | "DELETE" | "PATCH" | "REPORT";

/** An outbound HTTP request assembled by a connector. Mirrors `HttpRequestMessage`. */
export interface HttpRequest {
  readonly method: HttpMethod;
  /** Absolute URL, or a path resolved against the transport's `baseAddress`. */
  readonly url: string;
  /** Request headers (case-insensitive by convention; keys as supplied). */
  readonly headers: ReadonlyMap<string, string>;
  /** Request body (already-serialized string), or undefined for no body. */
  readonly body?: string;
}

/** An HTTP response. Mirrors the parts of `HttpResponseMessage` the connectors read. */
export interface HttpResponse {
  readonly statusCode: number;
  readonly body: string;
}

/**
 * The injected HTTP transport. Mirrors the slice of `System.Net.Http.HttpClient`
 * the connectors use: an optional base address + a single `send`. Production
 * hosts wrap `fetch`; tests inject a deterministic fake.
 */
export interface IHttpClient {
  /** Base address prepended to relative request URLs (mirrors `HttpClient.BaseAddress`). */
  readonly baseAddress?: string;
  /** Default headers merged into every request (mirrors `DefaultRequestHeaders`). */
  readonly defaultHeaders?: ReadonlyMap<string, string>;
  send(request: HttpRequest, ct?: AbortSignal): Promise<HttpResponse>;
}

/** Thrown by `ensureSuccess` — mirrors `HttpResponseMessage.EnsureSuccessStatusCode()`. */
export class HttpResponseError extends Error {
  constructor(public readonly statusCode: number, message?: string) {
    super(message ?? `Response status code does not indicate success: ${statusCode}.`);
    this.name = "HttpResponseError";
  }
}

/** True for 2xx status codes (mirrors `HttpResponseMessage.IsSuccessStatusCode`). */
export function isSuccessStatusCode(statusCode: number): boolean {
  return statusCode >= 200 && statusCode <= 299;
}

/** Throws {@link HttpResponseError} unless `resp` is 2xx (mirrors `EnsureSuccessStatusCode`). */
export function ensureSuccess(resp: HttpResponse): HttpResponse {
  if (!isSuccessStatusCode(resp.statusCode)) throw new HttpResponseError(resp.statusCode);
  return resp;
}

/**
 * Resolves a request URL against a base address, mirroring `HttpClient`'s
 * BaseAddress semantics: absolute request URLs win; otherwise the (possibly
 * relative) URL is combined with the base. A leading "/" on the request URL
 * replaces the base path (Uri combining), otherwise it is appended after the base.
 */
export function resolveUrl(baseAddress: string | undefined, url: string): string {
  if (/^[a-z][a-z0-9+.-]*:\/\//i.test(url)) return url; // absolute
  if (baseAddress === undefined || baseAddress.length === 0) return url;
  if (url.startsWith("/")) {
    try {
      return new URL(url, baseAddress).toString();
    } catch {
      return baseAddress.replace(/\/+$/, "") + url;
    }
  }
  // Relative combine: base must end with "/" for its last segment to be a directory.
  const base = baseAddress.endsWith("/") ? baseAddress : baseAddress + "/";
  try {
    return new URL(url, base).toString();
  } catch {
    return base + url;
  }
}

// ── Calendar ──────────────────────────────────────────────────────────────

/** A calendar event. Mirrors C# `CalendarEvent` record. */
export interface CalendarEvent {
  readonly eventId: string;
  readonly calendarId: string;
  readonly title: string;
  readonly description: string | null;
  readonly location: string | null;
  /** UTC start instant (C# `DateTimeOffset StartUtc`). */
  readonly startUtc: Date;
  /** UTC end instant (C# `DateTimeOffset EndUtc`). */
  readonly endUtc: Date;
  readonly isAllDay: boolean;
  readonly attendees: readonly string[];
}

/** Constructs a {@link CalendarEvent}. */
export function calendarEvent(
  eventId: string,
  calendarId: string,
  title: string,
  description: string | null,
  location: string | null,
  startUtc: Date,
  endUtc: Date,
  isAllDay: boolean,
  attendees: readonly string[],
): CalendarEvent {
  return { eventId, calendarId, title, description, location, startUtc, endUtc, isAllDay, attendees };
}

/** A calendar provider. Mirrors C# `ICalendarConnector`. */
export interface ICalendarConnector {
  readonly providerId: string;
  readonly isConfigured: boolean;
  listEventsAsync(fromUtc: Date, toUtc: Date, ct?: AbortSignal): Promise<readonly CalendarEvent[]>;
  createEventAsync(ev: CalendarEvent, ct?: AbortSignal): Promise<CalendarEvent>;
  deleteEventAsync(calendarId: string, eventId: string, ct?: AbortSignal): Promise<void>;
}

// ── Email ───────────────────────────────────────────────────────────────────

/** An email message. Mirrors C# `EmailMessage` record. */
export interface EmailMessage {
  readonly messageId: string;
  readonly from: string;
  readonly to: readonly string[];
  readonly subject: string;
  readonly bodyText: string;
  /** UTC receipt instant (C# `DateTimeOffset ReceivedUtc`). */
  readonly receivedUtc: Date;
  readonly unread: boolean;
  readonly labels: readonly string[];
}

/** Constructs an {@link EmailMessage}. */
export function emailMessage(
  messageId: string,
  from: string,
  to: readonly string[],
  subject: string,
  bodyText: string,
  receivedUtc: Date,
  unread: boolean,
  labels: readonly string[],
): EmailMessage {
  return { messageId, from, to, subject, bodyText, receivedUtc, unread, labels };
}

/** An email provider. Mirrors C# `IEmailConnector`. */
export interface IEmailConnector {
  readonly providerId: string;
  readonly isConfigured: boolean;
  listUnreadAsync(max: number, ct?: AbortSignal): Promise<readonly EmailMessage[]>;
  searchAsync(query: string, max: number, ct?: AbortSignal): Promise<readonly EmailMessage[]>;
  markReadAsync(messageId: string, ct?: AbortSignal): Promise<void>;
}

// ── News + social feeds ───────────────────────────────────────────────────────

/** A news / social feed item. Mirrors C# `NewsItem` record. */
export interface NewsItem {
  readonly itemId: string;
  readonly sourceId: string;
  readonly title: string;
  readonly summary: string;
  /** The item URL (C# `Uri Url`; stored as a string here). */
  readonly url: string;
  /** UTC publication instant (C# `DateTimeOffset PublishedUtc`). */
  readonly publishedUtc: Date;
  readonly tags: readonly string[];
}

/** Constructs a {@link NewsItem}. */
export function newsItem(
  itemId: string,
  sourceId: string,
  title: string,
  summary: string,
  url: string,
  publishedUtc: Date,
  tags: readonly string[],
): NewsItem {
  return { itemId, sourceId, title, summary, url, publishedUtc, tags };
}

/** A news / social feed source. Mirrors C# `INewsSource`. */
export interface INewsSource {
  readonly sourceId: string;
  readonly isConfigured: boolean;
  fetchLatestAsync(max: number, ct?: AbortSignal): Promise<readonly NewsItem[]>;
}

// ── Weather ───────────────────────────────────────────────────────────────────

/** A weather observation / forecast sample. Mirrors C# `WeatherSample` record. */
export interface WeatherSample {
  /** UTC instant of the sample (C# `DateTimeOffset AtUtc`). */
  readonly atUtc: Date;
  readonly tempC: number;
  readonly feelsLikeC: number;
  readonly precipMm: number;
  readonly windKph: number;
  readonly cloudPct: number;
  readonly condition: string;
}

/** Constructs a {@link WeatherSample}. */
export function weatherSample(
  atUtc: Date,
  tempC: number,
  feelsLikeC: number,
  precipMm: number,
  windKph: number,
  cloudPct: number,
  condition: string,
): WeatherSample {
  return { atUtc, tempC, feelsLikeC, precipMm, windKph, cloudPct, condition };
}

/** A weather provider. Mirrors C# `IWeatherProvider`. */
export interface IWeatherProvider {
  readonly providerId: string;
  currentAsync(lat: number, lon: number, ct?: AbortSignal): Promise<WeatherSample>;
  hourlyAsync(lat: number, lon: number, hours: number, ct?: AbortSignal): Promise<readonly WeatherSample[]>;
}

// ── Routing / traffic ─────────────────────────────────────────────────────────

/** A lat/lon point (C# value tuple `(double Lat, double Lon)`). */
export interface GeoPoint {
  readonly lat: number;
  readonly lon: number;
}

/** A route estimate. Mirrors C# `RouteEstimate` record. */
export interface RouteEstimate {
  readonly distanceKm: number;
  /** Duration in milliseconds (C# `TimeSpan Duration`). */
  readonly duration: number;
  readonly polyline: readonly GeoPoint[];
}

/** Constructs a {@link RouteEstimate}. */
export function routeEstimate(distanceKm: number, duration: number, polyline: readonly GeoPoint[]): RouteEstimate {
  return { distanceKm, duration, polyline };
}

/** A routing / traffic provider. Mirrors C# `IRoutingProvider`. */
export interface IRoutingProvider {
  readonly providerId: string;
  routeAsync(
    fromLat: number,
    fromLon: number,
    toLat: number,
    toLon: number,
    mode?: string,
    ct?: AbortSignal,
  ): Promise<RouteEstimate>;
}

// ── Home automation ───────────────────────────────────────────────────────────

/** A home-automation entity. Mirrors C# `HaEntity` record. */
export interface HaEntity {
  readonly entityId: string;
  readonly friendlyName: string;
  readonly domain: string;
  readonly state: string;
  readonly attributes: ReadonlyMap<string, string>;
}

/** Constructs a {@link HaEntity}. */
export function haEntity(
  entityId: string,
  friendlyName: string,
  domain: string,
  state: string,
  attributes: ReadonlyMap<string, string>,
): HaEntity {
  return { entityId, friendlyName, domain, state, attributes };
}

/** A home-automation provider. Mirrors C# `IHomeAutomationConnector`. */
export interface IHomeAutomationConnector {
  readonly providerId: string;
  readonly isConfigured: boolean;
  listEntitiesAsync(ct?: AbortSignal): Promise<readonly HaEntity[]>;
  callServiceAsync(
    domain: string,
    service: string,
    data: ReadonlyMap<string, unknown> | null | undefined,
    ct?: AbortSignal,
  ): Promise<void>;
}

// ── Shared string / whitespace helpers (C# string.IsNullOrWhiteSpace parity) ──

/** Mirrors C# `string.IsNullOrWhiteSpace`. */
export function isNullOrWhiteSpace(s: string | null | undefined): boolean {
  return s === null || s === undefined || s.trim().length === 0;
}

/** Mirrors C# `string.IsNullOrEmpty`. */
export function isNullOrEmpty(s: string | null | undefined): boolean {
  return s === null || s === undefined || s.length === 0;
}

// ── In-memory reference connectors ────────────────────────────────────────────
//
// Deterministic, dependency-free reference implementations of the connector
// contracts above — the canonical offline/test doubles usable without any
// external provider. Mirrors C# `InMemoryIntegrationConnectors.cs`. The real
// provider bindings live in the ./calendar, ./email, ./news, ./geo and
// ./homeassistant sub-modules.

/**
 * C# `Math.Round(double, int)` uses banker's rounding (round-half-to-even).
 * The weather/routing providers round their outputs, so we reproduce
 * round-half-to-even at a given number of decimal places to match byte-for-byte.
 */
function roundHalfEven(value: number, digits: number): number {
  const factor = 10 ** digits;
  const scaled = value * factor;
  const floor = Math.floor(scaled);
  const diff = scaled - floor;
  let rounded: number;
  if (diff < 0.5) rounded = floor;
  else if (diff > 0.5) rounded = floor + 1;
  else rounded = floor % 2 === 0 ? floor : floor + 1; // exactly .5 → to even
  return rounded / factor;
}

/**
 * In-memory {@link ICalendarConnector}: events are held in a map; listing
 * returns those overlapping the window, ordered by start. Mirrors C#
 * `InMemoryCalendarConnector`.
 */
export class InMemoryCalendarConnector implements ICalendarConnector {
  private readonly events = new Map<string, CalendarEvent>();

  get providerId(): string {
    return "in-memory";
  }
  get isConfigured(): boolean {
    return true;
  }

  listEventsAsync(fromUtc: Date, toUtc: Date, _ct?: AbortSignal): Promise<readonly CalendarEvent[]> {
    const from = fromUtc.getTime();
    const to = toUtc.getTime();
    return Promise.resolve(
      [...this.events.values()]
        .filter((e) => e.startUtc.getTime() < to && e.endUtc.getTime() > from)
        .sort((a, b) => a.startUtc.getTime() - b.startUtc.getTime()),
    );
  }

  createEventAsync(ev: CalendarEvent, _ct?: AbortSignal): Promise<CalendarEvent> {
    if (ev == null) throw new Error("ev required");
    this.events.set(ev.eventId, ev);
    return Promise.resolve(ev);
  }

  deleteEventAsync(_calendarId: string, eventId: string, _ct?: AbortSignal): Promise<void> {
    this.events.delete(eventId);
    return Promise.resolve();
  }
}

/**
 * In-memory {@link IEmailConnector}: seeded with messages; unread + search read
 * newest-first, markRead flips the flag. Mirrors C# `InMemoryEmailConnector`.
 */
export class InMemoryEmailConnector implements IEmailConnector {
  private readonly messages = new Map<string, EmailMessage>();

  constructor(seed?: Iterable<EmailMessage>) {
    if (seed !== undefined) for (const m of seed) this.messages.set(m.messageId, m);
  }

  get providerId(): string {
    return "in-memory";
  }
  get isConfigured(): boolean {
    return true;
  }

  listUnreadAsync(max: number, _ct?: AbortSignal): Promise<readonly EmailMessage[]> {
    return Promise.resolve(
      [...this.messages.values()]
        .filter((m) => m.unread)
        .sort((a, b) => b.receivedUtc.getTime() - a.receivedUtc.getTime())
        .slice(0, Math.max(0, max)),
    );
  }

  searchAsync(query: string, max: number, _ct?: AbortSignal): Promise<readonly EmailMessage[]> {
    const needle = (query ?? "").toLowerCase();
    return Promise.resolve(
      [...this.messages.values()]
        .filter((m) => m.subject.toLowerCase().includes(needle) || m.bodyText.toLowerCase().includes(needle))
        .sort((a, b) => b.receivedUtc.getTime() - a.receivedUtc.getTime())
        .slice(0, Math.max(0, max)),
    );
  }

  markReadAsync(messageId: string, _ct?: AbortSignal): Promise<void> {
    const m = this.messages.get(messageId);
    if (m !== undefined) this.messages.set(messageId, { ...m, unread: false });
    return Promise.resolve();
  }
}

/** In-memory {@link INewsSource}: seeded items, newest-first. Mirrors C# `InMemoryNewsSource`. */
export class InMemoryNewsSource implements INewsSource {
  private readonly items = new Map<string, NewsItem>();

  constructor(seed?: Iterable<NewsItem>) {
    if (seed !== undefined) for (const i of seed) this.items.set(i.itemId, i);
  }

  get sourceId(): string {
    return "in-memory";
  }
  get isConfigured(): boolean {
    return true;
  }

  fetchLatestAsync(max: number, _ct?: AbortSignal): Promise<readonly NewsItem[]> {
    return Promise.resolve(
      [...this.items.values()]
        .sort((a, b) => b.publishedUtc.getTime() - a.publishedUtc.getTime())
        .slice(0, Math.max(0, max)),
    );
  }
}

/**
 * In-memory {@link IWeatherProvider}: deterministic pseudo-weather derived from
 * coordinates + hour (no randomness, reproducible across platforms). Mirrors C#
 * `InMemoryWeatherProvider`.
 */
export class InMemoryWeatherProvider implements IWeatherProvider {
  get providerId(): string {
    return "in-memory";
  }

  currentAsync(lat: number, lon: number, _ct?: AbortSignal): Promise<WeatherSample> {
    return Promise.resolve(InMemoryWeatherProvider.sample(lat, lon, 0));
  }

  hourlyAsync(lat: number, lon: number, hours: number, _ct?: AbortSignal): Promise<readonly WeatherSample[]> {
    const n = Math.max(0, hours);
    const out: WeatherSample[] = [];
    for (let h = 0; h < n; h++) out.push(InMemoryWeatherProvider.sample(lat, lon, h));
    return Promise.resolve(out);
  }

  private static sample(lat: number, _lon: number, hourOffset: number): WeatherSample {
    const tempC = roundHalfEven(15.0 + 10.0 * Math.cos(((lat + hourOffset) * Math.PI) / 12.0), 2);
    // C# `DateTimeOffset.UnixEpoch.AddHours(hourOffset)` → epoch + hours (ms).
    const atUtc = new Date(hourOffset * 3_600_000);
    return weatherSample(atUtc, tempC, roundHalfEven(tempC - 1.5, 2), 0.0, 12.0, 40, "Clear");
  }
}

/**
 * In-memory {@link IRoutingProvider}: great-circle distance and a mode-based
 * speed give a deterministic estimate with a 2-point polyline. Mirrors C#
 * `InMemoryRoutingProvider`.
 */
export class InMemoryRoutingProvider implements IRoutingProvider {
  get providerId(): string {
    return "in-memory";
  }

  routeAsync(
    fromLat: number,
    fromLon: number,
    toLat: number,
    toLon: number,
    mode = "car",
    _ct?: AbortSignal,
  ): Promise<RouteEstimate> {
    const km = InMemoryRoutingProvider.haversine(fromLat, fromLon, toLat, toLon);
    const kph = mode === "walk" ? 5.0 : mode === "bike" ? 18.0 : mode === "transit" ? 30.0 : 60.0;
    // C# `TimeSpan.FromHours(km / kph)` → duration in ms (0 when kph <= 0).
    const durationMs = (kph <= 0 ? 0 : km / kph) * 3_600_000;
    return Promise.resolve(
      routeEstimate(roundHalfEven(km, 3), durationMs, [
        { lat: fromLat, lon: fromLon },
        { lat: toLat, lon: toLon },
      ]),
    );
  }

  private static haversine(lat1: number, lon1: number, lat2: number, lon2: number): number {
    const r = 6371.0;
    const dLat = ((lat2 - lat1) * Math.PI) / 180.0;
    const dLon = ((lon2 - lon1) * Math.PI) / 180.0;
    const a =
      Math.sin(dLat / 2) * Math.sin(dLat / 2) +
      Math.cos((lat1 * Math.PI) / 180.0) *
        Math.cos((lat2 * Math.PI) / 180.0) *
        Math.sin(dLon / 2) *
        Math.sin(dLon / 2);
    return r * 2 * Math.atan2(Math.sqrt(a), Math.sqrt(1 - a));
  }
}

/**
 * In-memory {@link IHomeAutomationConnector}: seeded entities;
 * turn_on/turn_off/toggle deterministically mutate matching-domain entity state.
 * Mirrors C# `InMemoryHomeAutomationConnector`.
 */
export class InMemoryHomeAutomationConnector implements IHomeAutomationConnector {
  private readonly entities = new Map<string, HaEntity>();

  constructor(seed?: Iterable<HaEntity>) {
    if (seed !== undefined) for (const e of seed) this.entities.set(e.entityId, e);
  }

  get providerId(): string {
    return "in-memory";
  }
  get isConfigured(): boolean {
    return true;
  }

  listEntitiesAsync(_ct?: AbortSignal): Promise<readonly HaEntity[]> {
    return Promise.resolve(
      [...this.entities.values()].sort((a, b) => (a.entityId < b.entityId ? -1 : a.entityId > b.entityId ? 1 : 0)),
    );
  }

  callServiceAsync(
    domain: string,
    service: string,
    _data: ReadonlyMap<string, unknown> | null | undefined,
    _ct?: AbortSignal,
  ): Promise<void> {
    const target = domain.toLowerCase();
    for (const e of [...this.entities.values()].filter((x) => x.domain.toLowerCase() === target)) {
      const newState =
        service === "turn_on"
          ? "on"
          : service === "turn_off"
            ? "off"
            : service === "toggle"
              ? e.state === "on"
                ? "off"
                : "on"
              : e.state;
      this.entities.set(e.entityId, { ...e, state: newState });
    }
    return Promise.resolve();
  }
}
