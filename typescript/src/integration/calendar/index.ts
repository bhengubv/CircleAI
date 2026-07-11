// integration/calendar/index.ts
// Full-parity port of CircleAI.Integration.Calendar (C#). C# is the exact spec.
//
// Three ICalendarConnector implementations:
//   CalDavCalendarConnector — generic CalDAV (iCloud/Fastmail/Nextcloud/…): HTTP
//     Basic auth + CalDAV REPORT time-range query + a minimal ICS parser/builder.
//   GoogleCalendarConnector — Google Calendar v3 REST over a host-supplied bearer.
//   MsGraphCalendarConnector — Microsoft Graph v1.0 calendarView over a bearer.
//
// The C# takes an injected `HttpClient`; here every connector takes the injected
// `IHttpClient` transport (from ../index.js) so parsing/URL-building is ported
// verbatim and runs deterministically with no real network.

import {
  type CalendarEvent,
  type ICalendarConnector,
  type IHttpClient,
  type HttpRequest,
  type HttpResponse,
  calendarEvent,
  ensureSuccess,
  isSuccessStatusCode,
  resolveUrl,
  isNullOrWhiteSpace,
  isNullOrEmpty,
  DateTimeOffsetMinValue,
} from "../index.js";

// ── Date formatting helpers (C# DateTimeOffset format-string parity) ──────────

function pad2(n: number): string {
  return n < 10 ? "0" + n : String(n);
}
function pad4(n: number): string {
  return n.toString().padStart(4, "0");
}

/** C# `DateTimeOffset.ToString("yyyyMMddTHHmmssZ")` on the UTC instant (ICS basic form). */
function fmtIcsUtc(d: Date): string {
  return (
    pad4(d.getUTCFullYear()) +
    pad2(d.getUTCMonth() + 1) +
    pad2(d.getUTCDate()) +
    "T" +
    pad2(d.getUTCHours()) +
    pad2(d.getUTCMinutes()) +
    pad2(d.getUTCSeconds()) +
    "Z"
  );
}

/** C# `DateTimeOffset.ToString("O")` — round-trip ISO 8601 with 7 fractional digits + offset. */
function fmtRoundTripUtc(d: Date): string {
  const frac = pad3(d.getUTCMilliseconds()) + "0000"; // ms → 7 digits
  return (
    pad4(d.getUTCFullYear()) +
    "-" +
    pad2(d.getUTCMonth() + 1) +
    "-" +
    pad2(d.getUTCDate()) +
    "T" +
    pad2(d.getUTCHours()) +
    ":" +
    pad2(d.getUTCMinutes()) +
    ":" +
    pad2(d.getUTCSeconds()) +
    "." +
    frac +
    "+00:00"
  );
}
function pad3(n: number): string {
  return n.toString().padStart(3, "0");
}

/** C# `ev.StartUtc.UtcDateTime.ToString("yyyy-MM-dd")` — the UTC calendar date. */
function fmtDateUtc(d: Date): string {
  return pad4(d.getUTCFullYear()) + "-" + pad2(d.getUTCMonth() + 1) + "-" + pad2(d.getUTCDate());
}

/** Time-of-day in ms from midnight UTC (mirrors C# `DateTimeOffset.TimeOfDay == TimeSpan.Zero`). */
function timeOfDayMsUtc(d: Date): number {
  return (
    d.getUTCHours() * 3_600_000 +
    d.getUTCMinutes() * 60_000 +
    d.getUTCSeconds() * 1000 +
    d.getUTCMilliseconds()
  );
}

function newGuidN(): string {
  // C# Guid.NewGuid().ToString("N") — 32 lowercase hex, no dashes.
  let s = "";
  for (let i = 0; i < 32; i++) s += Math.floor(Math.random() * 16).toString(16);
  return s;
}

// ── CalDAV ────────────────────────────────────────────────────────────────

/** Options for {@link CalDavCalendarConnector}. Mirrors C# `CalDavCalendarOptions`. */
export interface CalDavCalendarOptions {
  /** Full URL of the calendar collection (must end with a trailing slash for `.ics` combining). */
  readonly calendarUri: string;
  readonly username: string;
  readonly password: string;
}

/** Constructs {@link CalDavCalendarOptions}. */
export function calDavCalendarOptions(calendarUri: string, username: string, password: string): CalDavCalendarOptions {
  return { calendarUri, username, password };
}

/** Base64-encodes UTF-8 `s` (mirrors `Convert.ToBase64String(Encoding.UTF8.GetBytes(s))`). */
function base64Utf8(s: string): string {
  return Buffer.from(s, "utf8").toString("base64");
}

/**
 * Generic CalDAV connector — HTTP Basic auth + the standard CalDAV REPORT verb.
 * Faithful port of C# `CalDavCalendarConnector`.
 */
export class CalDavCalendarConnector implements ICalendarConnector {
  private readonly http: IHttpClient;
  private readonly opts: CalDavCalendarOptions;
  private readonly authHeader: string;

  constructor(opts: CalDavCalendarOptions, http: IHttpClient) {
    if (opts == null) throw new Error("opts required");
    if (http == null) throw new Error("http required");
    this.opts = opts;
    this.http = http;
    this.authHeader = "Basic " + base64Utf8(`${opts.username}:${opts.password}`);
  }

  get providerId(): string {
    return "caldav";
  }
  get isConfigured(): boolean {
    return !isNullOrWhiteSpace(this.opts.username) && !isNullOrWhiteSpace(this.opts.password);
  }

  async listEventsAsync(fromUtc: Date, toUtc: Date, ct?: AbortSignal): Promise<readonly CalendarEvent[]> {
    const xml =
      '<?xml version="1.0" encoding="utf-8" ?>\n' +
      '<C:calendar-query xmlns:D="DAV:" xmlns:C="urn:ietf:params:xml:ns:caldav">\n' +
      "  <D:prop>\n" +
      "    <D:getetag/>\n" +
      "    <C:calendar-data/>\n" +
      "  </D:prop>\n" +
      "  <C:filter>\n" +
      '    <C:comp-filter name="VCALENDAR">\n' +
      '      <C:comp-filter name="VEVENT">\n' +
      `        <C:time-range start="${fmtIcsUtc(fromUtc)}" end="${fmtIcsUtc(toUtc)}"/>\n` +
      "      </C:comp-filter>\n" +
      "    </C:comp-filter>\n" +
      "  </C:filter>\n" +
      "</C:calendar-query>";
    const headers = new Map<string, string>([
      ["Authorization", this.authHeader],
      ["Content-Type", "application/xml; charset=utf-8"],
      ["Depth", "1"],
    ]);
    const req: HttpRequest = { method: "REPORT", url: this.opts.calendarUri, headers, body: xml };
    const resp = ensureSuccess(await this.http.send(req, ct));

    const result: CalendarEvent[] = [];
    for (const calData of extractCalendarData(resp.body)) {
      for (const ev of parseIcs(calData, this.opts.calendarUri)) result.push(ev);
    }
    return result;
  }

  async createEventAsync(ev: CalendarEvent, ct?: AbortSignal): Promise<CalendarEvent> {
    if (ev == null) throw new Error("ev required");
    const uid = isNullOrWhiteSpace(ev.eventId) ? newGuidN() : ev.eventId;
    const withUid: CalendarEvent = { ...ev, eventId: uid };
    const ics = buildIcs(withUid);
    const targetUri = resolveUrl(this.opts.calendarUri, uid + ".ics");
    const headers = new Map<string, string>([
      ["Authorization", this.authHeader],
      ["Content-Type", "text/calendar; charset=utf-8"],
      ["If-None-Match", "*"],
    ]);
    const req: HttpRequest = { method: "PUT", url: targetUri, headers, body: ics };
    ensureSuccess(await this.http.send(req, ct));
    return withUid;
  }

  async deleteEventAsync(_calendarId: string, eventId: string, ct?: AbortSignal): Promise<void> {
    if (isNullOrWhiteSpace(eventId)) throw new Error("eventId required");
    const targetUri = resolveUrl(this.opts.calendarUri, eventId + ".ics");
    const headers = new Map<string, string>([["Authorization", this.authHeader]]);
    const resp = await this.http.send({ method: "DELETE", url: targetUri, headers }, ct);
    // 204 No Content / 200 OK / 404 Not Found are all acceptable; else ensure success.
    if (resp.statusCode !== 204 && resp.statusCode !== 200 && resp.statusCode !== 404) ensureSuccess(resp);
  }
}

/** Extracts every `<C:calendar-data>` text body from a CalDAV multistatus XML. */
function extractCalendarData(xml: string): string[] {
  const out: string[] = [];
  // Match <...:calendar-data ...>BODY</...:calendar-data> (namespace-prefix agnostic).
  const rx = /<(?:[A-Za-z0-9_.-]+:)?calendar-data\b[^>]*>([\s\S]*?)<\/(?:[A-Za-z0-9_.-]+:)?calendar-data>/g;
  let m: RegExpExecArray | null;
  while ((m = rx.exec(xml)) !== null) out.push(decodeXmlText(m[1]));
  return out;
}

/** Decodes the five predefined XML entities (matches XDocument text-node decoding). */
function decodeXmlText(s: string): string {
  return s
    .replace(/&lt;/g, "<")
    .replace(/&gt;/g, ">")
    .replace(/&quot;/g, '"')
    .replace(/&apos;/g, "'")
    .replace(/&amp;/g, "&");
}

// ── Minimal ICS parser / builder (verbatim port) ──────────────────────────────

function* parseIcs(ics: string, calendarId: string): Generator<CalendarEvent> {
  if (isNullOrWhiteSpace(ics)) return;
  const rxEvent = /BEGIN:VEVENT([\s\S]*?)END:VEVENT/g;
  let em: RegExpExecArray | null;
  while ((em = rxEvent.exec(ics)) !== null) {
    const body = em[1];
    const get = (key: string): string => {
      // C#: (?m)^{key}(?:;[^:]*)?:(.*)$  — multiline, key at line start.
      const rx = new RegExp("^" + escapeRegex(key) + "(?:;[^:]*)?:(.*)$", "m");
      const line = rx.exec(body);
      return line !== null ? line[1].trim() : "";
    };
    const time = (key: string): Date => {
      const v = get(key);
      if (isNullOrEmpty(v)) return DateTimeOffsetMinValue;
      const utc = tryParseExactIcsUtc(v);
      if (utc !== null) return utc;
      const dOnly = tryParseExactDateOnly(v);
      if (dOnly !== null) return dOnly;
      return DateTimeOffsetMinValue;
    };
    const uid = get("UID");
    const title = get("SUMMARY");
    const desc = get("DESCRIPTION");
    const loc = get("LOCATION");
    const startUtc = time("DTSTART");
    const endUtc = time("DTEND");
    yield calendarEvent(
      uid,
      calendarId,
      title,
      isNullOrEmpty(desc) ? null : desc,
      isNullOrEmpty(loc) ? null : loc,
      startUtc,
      endUtc,
      startUtc.getTime() !== DateTimeOffsetMinValue.getTime() &&
        timeOfDayMsUtc(startUtc) === 0 &&
        timeOfDayMsUtc(endUtc) === 0,
      [],
    );
  }
}

/** C# `DateTimeOffset.TryParseExact(v, "yyyyMMddTHHmmssZ", …)` → UTC, or null. */
function tryParseExactIcsUtc(v: string): Date | null {
  const m = /^(\d{4})(\d{2})(\d{2})T(\d{2})(\d{2})(\d{2})Z$/.exec(v);
  if (m === null) return null;
  const d = new Date(
    Date.UTC(+m[1], +m[2] - 1, +m[3], +m[4], +m[5], +m[6]),
  );
  return d;
}

/** C# `DateOnly.TryParseExact(v, "yyyyMMdd")` → midnight-UTC DateTimeOffset, or null. */
function tryParseExactDateOnly(v: string): Date | null {
  const m = /^(\d{4})(\d{2})(\d{2})$/.exec(v);
  if (m === null) return null;
  return new Date(Date.UTC(+m[1], +m[2] - 1, +m[3], 0, 0, 0));
}

function buildIcs(ev: CalendarEvent): string {
  const dtStamp = fmtIcsUtc(new Date());
  const dtStart = fmtIcsUtc(ev.startUtc);
  const dtEnd = fmtIcsUtc(ev.endUtc);
  const lines: string[] = [];
  lines.push("BEGIN:VCALENDAR");
  lines.push("VERSION:2.0");
  lines.push("PRODID:-//CircleAI//Calendar//EN");
  lines.push("BEGIN:VEVENT");
  lines.push(`UID:${ev.eventId}`);
  lines.push(`DTSTAMP:${dtStamp}`);
  lines.push(`DTSTART:${dtStart}`);
  lines.push(`DTEND:${dtEnd}`);
  lines.push(`SUMMARY:${escapeIcs(ev.title)}`);
  if (!isNullOrEmpty(ev.description)) lines.push(`DESCRIPTION:${escapeIcs(ev.description as string)}`);
  if (!isNullOrEmpty(ev.location)) lines.push(`LOCATION:${escapeIcs(ev.location as string)}`);
  lines.push("END:VEVENT");
  lines.push("END:VCALENDAR");
  // C# StringBuilder.AppendLine uses Environment.NewLine; we emit "\r\n" per RFC 5545.
  return lines.join("\r\n") + "\r\n";
}

/** C#: s.Replace("\\","\\\\").Replace("\n","\\n").Replace(",","\\,").Replace(";","\\;"). */
function escapeIcs(s: string): string {
  return s.replace(/\\/g, "\\\\").replace(/\n/g, "\\n").replace(/,/g, "\\,").replace(/;/g, "\\;");
}

function escapeRegex(s: string): string {
  return s.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

// ── Google Calendar v3 ────────────────────────────────────────────────────────

/** Options for {@link GoogleCalendarConnector}. Mirrors C# `GoogleCalendarOptions`. */
export interface GoogleCalendarOptions {
  /** Async callback returning a fresh Bearer token (or null/empty when unavailable). */
  readonly accessTokenProvider: (ct?: AbortSignal) => Promise<string | null>;
  /** Calendar to read/write. Default "primary". */
  readonly calendarId: string;
}

/** Constructs {@link GoogleCalendarOptions} (default calendarId "primary"). */
export function googleCalendarOptions(
  accessTokenProvider: (ct?: AbortSignal) => Promise<string | null>,
  calendarId = "primary",
): GoogleCalendarOptions {
  return { accessTokenProvider, calendarId };
}

const GOOGLE_BASE = "https://www.googleapis.com/calendar/v3/";

/** Google Calendar v3 client. Faithful port of C# `GoogleCalendarConnector`. */
export class GoogleCalendarConnector implements ICalendarConnector {
  private readonly http: IHttpClient;
  private readonly opts: GoogleCalendarOptions;

  constructor(opts: GoogleCalendarOptions, http: IHttpClient) {
    if (opts == null) throw new Error("opts required");
    if (http == null) throw new Error("http required");
    this.opts = opts;
    this.http = http;
  }

  get providerId(): string {
    return "google-calendar";
  }
  get isConfigured(): boolean {
    return this.opts.accessTokenProvider != null;
  }

  async listEventsAsync(fromUtc: Date, toUtc: Date, ct?: AbortSignal): Promise<readonly CalendarEvent[]> {
    const token = await this.ensureAuth(ct);
    const path =
      `calendars/${encodeURIComponent(this.opts.calendarId)}/events` +
      `?timeMin=${encodeURIComponent(fmtRoundTripUtc(fromUtc))}` +
      `&timeMax=${encodeURIComponent(fmtRoundTripUtc(toUtc))}` +
      `&singleEvents=true&orderBy=startTime&maxResults=250`;
    const resp = ensureSuccess(await this.get(GOOGLE_BASE + path, token, ct));
    const root = JSON.parse(resp.body) as Record<string, unknown>;

    const list: CalendarEvent[] = [];
    const items = root["items"];
    if (Array.isArray(items)) {
      for (const evUnknown of items) {
        const ev = evUnknown as Record<string, unknown>;
        if (typeof ev["status"] === "string" && ev["status"] === "cancelled") continue;

        const [startUtc, isAllDay] = parseGoogleTime(ev, "start");
        const [endUtc] = parseGoogleTime(ev, "end");

        const attendees: string[] = [];
        const atts = ev["attendees"];
        if (Array.isArray(atts)) {
          for (const aUnknown of atts) {
            const a = aUnknown as Record<string, unknown>;
            if ("email" in a) attendees.push(typeof a["email"] === "string" ? (a["email"] as string) : "");
          }
        }
        list.push(
          calendarEvent(
            typeof ev["id"] === "string" ? (ev["id"] as string) : "",
            this.opts.calendarId,
            typeof ev["summary"] === "string" ? (ev["summary"] as string) : "",
            typeof ev["description"] === "string" ? (ev["description"] as string) : null,
            typeof ev["location"] === "string" ? (ev["location"] as string) : null,
            startUtc,
            endUtc,
            isAllDay,
            attendees,
          ),
        );
      }
    }
    return list;
  }

  async createEventAsync(ev: CalendarEvent, ct?: AbortSignal): Promise<CalendarEvent> {
    if (ev == null) throw new Error("ev required");
    const token = await this.ensureAuth(ct);
    const body = {
      summary: ev.title,
      description: ev.description,
      location: ev.location,
      start: ev.isAllDay
        ? { date: fmtDateUtc(ev.startUtc) }
        : { dateTime: fmtRoundTripUtc(ev.startUtc), timeZone: "UTC" },
      end: ev.isAllDay ? { date: fmtDateUtc(ev.endUtc) } : { dateTime: fmtRoundTripUtc(ev.endUtc), timeZone: "UTC" },
      attendees: ev.attendees.map((a) => ({ email: a })),
    };
    const resp = ensureSuccess(
      await this.postJson(GOOGLE_BASE + `calendars/${encodeURIComponent(ev.calendarId)}/events`, body, token, ct),
    );
    const root = JSON.parse(resp.body) as Record<string, unknown>;
    return { ...ev, eventId: typeof root["id"] === "string" ? (root["id"] as string) : "" };
  }

  async deleteEventAsync(calendarId: string, eventId: string, ct?: AbortSignal): Promise<void> {
    if (isNullOrWhiteSpace(calendarId)) throw new Error("calendarId required");
    if (isNullOrWhiteSpace(eventId)) throw new Error("eventId required");
    const token = await this.ensureAuth(ct);
    const url = GOOGLE_BASE + `calendars/${encodeURIComponent(calendarId)}/events/${encodeURIComponent(eventId)}`;
    const resp = await this.http.send(
      { method: "DELETE", url, headers: new Map([["Authorization", `Bearer ${token}`]]) },
      ct,
    );
    if (resp.statusCode !== 204 && resp.statusCode !== 410) ensureSuccess(resp);
  }

  private async ensureAuth(ct?: AbortSignal): Promise<string> {
    const token = await this.opts.accessTokenProvider(ct);
    if (isNullOrWhiteSpace(token)) throw new Error("Google Calendar access token unavailable; refresh OAuth.");
    return token as string;
  }

  private get(url: string, token: string, ct?: AbortSignal): Promise<HttpResponse> {
    return this.http.send({ method: "GET", url, headers: new Map([["Authorization", `Bearer ${token}`]]) }, ct);
  }
  private postJson(url: string, body: unknown, token: string, ct?: AbortSignal): Promise<HttpResponse> {
    return this.http.send(
      {
        method: "POST",
        url,
        headers: new Map([
          ["Authorization", `Bearer ${token}`],
          ["Content-Type", "application/json; charset=utf-8"],
        ]),
        body: JSON.stringify(body),
      },
      ct,
    );
  }
}

/** C# `GoogleCalendarConnector.ParseTime` → (utc, allDay). */
function parseGoogleTime(parent: Record<string, unknown>, property: string): [Date, boolean] {
  const node = parent[property];
  if (node == null || typeof node !== "object") return [DateTimeOffsetMinValue, false];
  const obj = node as Record<string, unknown>;
  if (typeof obj["dateTime"] === "string") {
    const dto = tryParseDate(obj["dateTime"] as string);
    if (dto !== null) return [dto, false];
  }
  if (typeof obj["date"] === "string") {
    const dm = /^(\d{4})-(\d{2})-(\d{2})$/.exec(obj["date"] as string);
    if (dm !== null) return [new Date(Date.UTC(+dm[1], +dm[2] - 1, +dm[3], 0, 0, 0)), true];
  }
  return [DateTimeOffsetMinValue, false];
}

// ── Microsoft Graph v1.0 ──────────────────────────────────────────────────────

/** Options for {@link MsGraphCalendarConnector}. Mirrors C# `MsGraphCalendarOptions`. */
export interface MsGraphCalendarOptions {
  readonly accessTokenProvider: (ct?: AbortSignal) => Promise<string | null>;
  readonly calendarId: string;
}

/** Constructs {@link MsGraphCalendarOptions} (default calendarId "primary"). */
export function msGraphCalendarOptions(
  accessTokenProvider: (ct?: AbortSignal) => Promise<string | null>,
  calendarId = "primary",
): MsGraphCalendarOptions {
  return { accessTokenProvider, calendarId };
}

const MSGRAPH_BASE = "https://graph.microsoft.com/v1.0/";

/** Microsoft Graph 1.0 calendar client. Faithful port of C# `MsGraphCalendarConnector`. */
export class MsGraphCalendarConnector implements ICalendarConnector {
  private readonly http: IHttpClient;
  private readonly opts: MsGraphCalendarOptions;

  constructor(opts: MsGraphCalendarOptions, http: IHttpClient) {
    if (opts == null) throw new Error("opts required");
    if (http == null) throw new Error("http required");
    this.opts = opts;
    this.http = http;
  }

  get providerId(): string {
    return "ms-graph-calendar";
  }
  get isConfigured(): boolean {
    return this.opts.accessTokenProvider != null;
  }

  async listEventsAsync(fromUtc: Date, toUtc: Date, ct?: AbortSignal): Promise<readonly CalendarEvent[]> {
    const token = await this.ensureAuth(ct);
    const path =
      `me/calendar/calendarView` +
      `?startDateTime=${encodeURIComponent(fmtRoundTripUtc(fromUtc))}` +
      `&endDateTime=${encodeURIComponent(fmtRoundTripUtc(toUtc))}` +
      `&$top=250&$orderby=start/dateTime`;
    const resp = ensureSuccess(
      await this.http.send(
        { method: "GET", url: MSGRAPH_BASE + path, headers: new Map([["Authorization", `Bearer ${token}`]]) },
        ct,
      ),
    );
    const root = JSON.parse(resp.body) as Record<string, unknown>;

    const list: CalendarEvent[] = [];
    const arr = root["value"];
    if (Array.isArray(arr)) {
      for (const evUnknown of arr) {
        const ev = evUnknown as Record<string, unknown>;
        const attendees: string[] = [];
        const atts = ev["attendees"];
        if (Array.isArray(atts)) {
          for (const aUnknown of atts) {
            const a = aUnknown as Record<string, unknown>;
            const em = a["emailAddress"];
            if (em != null && typeof em === "object" && "address" in (em as object)) {
              const addr = (em as Record<string, unknown>)["address"];
              attendees.push(typeof addr === "string" ? addr : "");
            }
          }
        }
        const startUtc = parseGraphTime(ev, "start");
        const endUtc = parseGraphTime(ev, "end");
        const allDay = ev["isAllDay"] === true;

        let location: string | null = null;
        const loc = ev["location"];
        if (loc != null && typeof loc === "object" && "displayName" in (loc as object)) {
          const dn = (loc as Record<string, unknown>)["displayName"];
          location = typeof dn === "string" ? dn : null;
        }

        list.push(
          calendarEvent(
            typeof ev["id"] === "string" ? (ev["id"] as string) : "",
            this.opts.calendarId,
            typeof ev["subject"] === "string" ? (ev["subject"] as string) : "",
            typeof ev["bodyPreview"] === "string" ? (ev["bodyPreview"] as string) : null,
            location,
            startUtc,
            endUtc,
            allDay,
            attendees,
          ),
        );
      }
    }
    return list;
  }

  async createEventAsync(ev: CalendarEvent, ct?: AbortSignal): Promise<CalendarEvent> {
    if (ev == null) throw new Error("ev required");
    const token = await this.ensureAuth(ct);
    const body = {
      subject: ev.title,
      body: { contentType: "text", content: ev.description ?? "" },
      start: { dateTime: fmtRoundTripUtc(ev.startUtc), timeZone: "UTC" },
      end: { dateTime: fmtRoundTripUtc(ev.endUtc), timeZone: "UTC" },
      isAllDay: ev.isAllDay,
      location: { displayName: ev.location ?? "" },
      attendees: ev.attendees.map((a) => ({ emailAddress: { address: a }, type: "required" })),
    };
    const resp = ensureSuccess(
      await this.http.send(
        {
          method: "POST",
          url: MSGRAPH_BASE + "me/events",
          headers: new Map([
            ["Authorization", `Bearer ${token}`],
            ["Content-Type", "application/json; charset=utf-8"],
          ]),
          body: JSON.stringify(body),
        },
        ct,
      ),
    );
    const root = JSON.parse(resp.body) as Record<string, unknown>;
    return { ...ev, eventId: typeof root["id"] === "string" ? (root["id"] as string) : "" };
  }

  async deleteEventAsync(_calendarId: string, eventId: string, ct?: AbortSignal): Promise<void> {
    if (isNullOrWhiteSpace(eventId)) throw new Error("eventId required");
    const token = await this.ensureAuth(ct);
    const resp = await this.http.send(
      {
        method: "DELETE",
        url: MSGRAPH_BASE + `me/events/${encodeURIComponent(eventId)}`,
        headers: new Map([["Authorization", `Bearer ${token}`]]),
      },
      ct,
    );
    if (resp.statusCode !== 204) ensureSuccess(resp);
  }

  private async ensureAuth(ct?: AbortSignal): Promise<string> {
    const token = await this.opts.accessTokenProvider(ct);
    if (isNullOrWhiteSpace(token)) throw new Error("Microsoft Graph access token unavailable; refresh OAuth.");
    return token as string;
  }
}

/** C# `MsGraphCalendarConnector.ParseGraphTime` → UTC or MinValue. */
function parseGraphTime(parent: Record<string, unknown>, property: string): Date {
  const node = parent[property];
  if (node == null || typeof node !== "object") return DateTimeOffsetMinValue;
  const dt = (node as Record<string, unknown>)["dateTime"];
  if (typeof dt !== "string" || dt.length === 0) return DateTimeOffsetMinValue;
  const dto = tryParseDate(dt);
  return dto !== null ? dto : DateTimeOffsetMinValue;
}

/**
 * C# `DateTimeOffset.TryParse(..., AssumeUniversal).ToUniversalTime()`. A string
 * with no offset is treated as UTC (AssumeUniversal); with an offset it is
 * converted to UTC. Returns null when unparseable.
 */
export function tryParseDate(s: string | null | undefined): Date | null {
  if (s == null || s.length === 0) return null;
  const hasZone = /(?:[zZ]|[+-]\d{2}:?\d{2})$/.test(s.trim());
  const iso = hasZone ? s : s + "Z"; // AssumeUniversal for zoneless input
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) {
    const d2 = new Date(s);
    return Number.isNaN(d2.getTime()) ? null : d2;
  }
  return d;
}

export { isSuccessStatusCode };
