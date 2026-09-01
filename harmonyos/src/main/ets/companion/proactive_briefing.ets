/**
 * companion/proactive_briefing
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// companion/proactive_briefing.ts
//
// ProactiveBriefingService.cs — a scheduled service that assembles a
// "what's happening" briefing from registered calendar / email / news / weather
// connectors, summarises it through an LLM, and pushes the result through any
// registered notifier.
//
// The connectors, the AI service, and the notifiers are all INJECTED behind
// interfaces (as in the C#). The connector / message record shapes and the
// members the service actually reads are modelled here — the CircleAI.Integration
// layer is not part of this port's work unit. The schedule is the simplest
// possible cron: a list of UTC times-of-day. The default is 06:30 and 18:00 UTC.

// ── Connector record shapes (subset the service reads) ───────────────────────

/** Subset of CircleAI.Integration.CalendarEvent the briefing reads. */
export interface CalendarEvent {
  readonly title: string;
  readonly location: string | null;
  readonly startUtc: Date;
}

/** Subset of CircleAI.Integration.EmailMessage the briefing reads. */
export interface EmailMessage {
  readonly from: string;
  readonly subject: string;
}

/** Subset of CircleAI.Integration.NewsItem the briefing reads. */
export interface NewsItem {
  readonly title: string;
}

/** Subset of CircleAI.Integration.WeatherSample the briefing reads. */
export interface WeatherSample {
  readonly tempC: number;
  readonly feelsLikeC: number;
  readonly windKph: number;
  readonly condition: string;
}

// ── Connector interfaces (subset the service uses) ───────────────────────────

export interface ICalendarConnector {
  readonly providerId: string;
  readonly isConfigured: boolean;
  listEventsAsync(fromUtc: Date, toUtc: Date, signal?: AbortSignal): Promise<readonly CalendarEvent[]>;
}

export interface IEmailConnector {
  readonly providerId: string;
  readonly isConfigured: boolean;
  listUnreadAsync(max: number, signal?: AbortSignal): Promise<readonly EmailMessage[]>;
}

export interface INewsSource {
  readonly sourceId: string;
  readonly isConfigured: boolean;
  fetchLatestAsync(max: number, signal?: AbortSignal): Promise<readonly NewsItem[]>;
}

export interface IWeatherProvider {
  readonly providerId: string;
  currentAsync(lat: number, lon: number, signal?: AbortSignal): Promise<WeatherSample>;
}

// ── AI service (subset the service calls) ────────────────────────────────────

/** One chat message. Mirrors CircleAI.Inference.ChatMessage(role, content). */
export interface ChatMessage {
  readonly role: string;
  readonly content: string;
}

/** Subset of CircleAI.Hosting.IAIService the briefing calls. */
export interface IAIService {
  chatAsync(messages: readonly ChatMessage[], signal?: AbortSignal): Promise<string>;
}

// ── Notifier ─────────────────────────────────────────────────────────────────

/** Pluggable notifier — hosts wire WhatsApp, Telegram, SMS, push, etc. */
export interface IBriefingNotifier {
  deliverAsync(headline: string, body: string, address: string | null, signal?: AbortSignal): Promise<void>;
}

// ── Options ──────────────────────────────────────────────────────────────────

/** Configuration knobs for ProactiveBriefingService. */
export interface ProactiveBriefingOptions {
  /** UTC times-of-day (in minutes-of-day) at which to fire. Default: 06:30, 18:00. */
  readonly fireTimesUtcMinutes?: readonly number[];
  /** Latitude for weather lookup. undefined = skip weather. */
  readonly latitude?: number;
  /** Longitude for weather lookup. undefined = skip weather. */
  readonly longitude?: number;
  /** Headline used by the notifier. Default "Your briefing". */
  readonly headline?: string;
  /** Where to deliver the briefing (phone E.164 / channel id / …). */
  readonly deliveryAddress?: string | null;
}

/** The default fire times: 06:30 and 18:00 UTC, expressed as minutes-of-day. */
export const DEFAULT_FIRE_TIMES_UTC_MINUTES: readonly number[] = [6 * 60 + 30, 18 * 60];

export interface ProactiveBriefingDeps {
  readonly calendars?: readonly ICalendarConnector[];
  readonly emails?: readonly IEmailConnector[];
  readonly news?: readonly INewsSource[];
  readonly weather?: IWeatherProvider | null;
  readonly notifiers?: readonly IBriefingNotifier[];
  readonly ai?: IAIService | null;
}

const MS_PER_HOUR = 3_600_000;
const MS_PER_DAY = 86_400_000;

/**
 * Assembles + delivers a scheduled briefing. `start` arms a loop that sleeps
 * until the next configured fire time and calls `fireOnceAsync`; `stop` cancels
 * it. `fireOnceAsync` is public so hosts (and tests) can trigger a briefing
 * directly.
 */
export class ProactiveBriefingService {
  private readonly calendars: readonly ICalendarConnector[];
  private readonly emails: readonly IEmailConnector[];
  private readonly news: readonly INewsSource[];
  private readonly weather: IWeatherProvider | null;
  private readonly notifiers: readonly IBriefingNotifier[];
  private readonly ai: IAIService | null;
  private readonly fireTimes: readonly number[];
  private readonly headline: string;
  private readonly deliveryAddress: string | null;
  private readonly latitude?: number;
  private readonly longitude?: number;

  private controller: AbortController | null = null;
  private loop: Promise<void> | null = null;

  constructor(opts: ProactiveBriefingOptions, deps: ProactiveBriefingDeps = {}) {
    if (opts == null) throw new Error("opts required");
    this.fireTimes = opts.fireTimesUtcMinutes ?? DEFAULT_FIRE_TIMES_UTC_MINUTES;
    this.headline = opts.headline ?? "Your briefing";
    this.deliveryAddress = opts.deliveryAddress ?? null;
    this.latitude = opts.latitude;
    this.longitude = opts.longitude;
    this.calendars = deps.calendars ?? [];
    this.emails = deps.emails ?? [];
    this.news = deps.news ?? [];
    this.weather = deps.weather ?? null;
    this.notifiers = deps.notifiers ?? [];
    this.ai = deps.ai ?? null;
  }

  start(): void {
    if (this.controller !== null) return;
    this.controller = new AbortController();
    this.loop = this.loopAsync(this.controller.signal);
  }

  async stop(): Promise<void> {
    if (this.controller === null) return;
    this.controller.abort();
    const loop = this.loop;
    this.controller = null;
    this.loop = null;
    if (loop) {
      try {
        await loop;
      } catch {
        /* expected cancellation */
      }
    }
  }

  private async loopAsync(signal: AbortSignal): Promise<void> {
    while (!signal.aborted) {
      const sleepMs = this.timeUntilNextFireMs(new Date());
      try {
        await delay(sleepMs, signal);
      } catch {
        return;
      }
      try {
        await this.fireOnceAsync(signal);
      } catch {
        /* swallow — the C# logs a warning and continues */
      }
    }
  }

  /**
   * Time (ms) until the next configured fire moment. Always > 30s past `now` so
   * a fire never immediately re-fires. No fire times → 1 hour. Faithful to the
   * C# TimeUntilNextFire.
   */
  timeUntilNextFireMs(now: Date): number {
    if (this.fireTimes.length === 0) return MS_PER_HOUR;
    const nowMs = now.getTime();
    const todayBaseMs = Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), now.getUTCDate());
    let best: number | null = null;
    for (const tod of this.fireTimes) {
      let candidate = todayBaseMs + tod * 60_000;
      if (candidate <= nowMs + 30_000) candidate += MS_PER_DAY;
      const gap = candidate - nowMs;
      if (best === null || gap < best) best = gap;
    }
    return best ?? MS_PER_HOUR;
  }

  /** Assemble the briefing context, summarise via the LLM, deliver. */
  async fireOnceAsync(signal?: AbortSignal): Promise<void> {
    const ctxParts: string[] = [];

    // Calendar — next 24 hours.
    for (const cal of this.calendars) {
      if (!cal.isConfigured) continue;
      try {
        const from = new Date();
        const to = new Date(from.getTime() + 24 * MS_PER_HOUR);
        const events = await cal.listEventsAsync(from, to, signal);
        if (events.length > 0) {
          ctxParts.push(`### Calendar (${cal.providerId})`);
          const ordered = stableSortByAsc([...events], (e) => e.startUtc.getTime()).slice(0, 8);
          for (const e of ordered) {
            const loc = e.location == null || e.location.length === 0 ? "" : " @ " + e.location;
            ctxParts.push(`- ${formatLocalHhmm(e.startUtc)} ${e.title}${loc}`);
          }
        }
      } catch {
        /* skip this provider */
      }
    }

    // Email — unread.
    for (const em of this.emails) {
      if (!em.isConfigured) continue;
      try {
        const unread = await em.listUnreadAsync(5, signal);
        if (unread.length > 0) {
          ctxParts.push(`### Unread email (${em.providerId})`);
          for (const m of unread) ctxParts.push(`- ${m.from}: ${m.subject}`);
        }
      } catch {
        /* skip */
      }
    }

    // News — latest from each source.
    for (const src of this.news) {
      if (!src.isConfigured) continue;
      try {
        const items = await src.fetchLatestAsync(5, signal);
        if (items.length > 0) {
          ctxParts.push(`### News (${src.sourceId})`);
          for (const i of items) ctxParts.push(`- ${i.title}`);
        }
      } catch {
        /* skip */
      }
    }

    // Weather — if location configured.
    if (this.weather !== null && this.latitude !== undefined && this.longitude !== undefined) {
      try {
        const w = await this.weather.currentAsync(this.latitude, this.longitude, signal);
        ctxParts.push(`### Weather (${this.weather.providerId})`);
        ctxParts.push(
          `- ${f0(w.tempC)}°C ${w.condition}, feels ${f0(w.feelsLikeC)}°C, wind ${f0(w.windKph)} km/h`,
        );
      } catch {
        /* skip */
      }
    }

    if (ctxParts.length === 0) return;

    const context = ctxParts.join("\n");
    const prompt =
      "Summarise the user's morning briefing in 80 words or less. Warm but factual. " +
      "End with the one thing they should do first today.\n\n" +
      context;

    let summary: string;
    if (this.ai !== null) {
      try {
        summary = await this.ai.chatAsync([{ role: "user", content: prompt }], signal);
      } catch {
        summary = context;
      }
    } else {
      summary = context;
    }

    for (const notifier of this.notifiers) {
      try {
        await notifier.deliverAsync(this.headline, summary, this.deliveryAddress, signal);
      } catch {
        /* swallow — the C# logs a warning per notifier */
      }
    }
  }
}

/** C# `{value:F0}` — round half-away-from-zero to an integer string. */
function f0(value: number): string {
  // .NET "F0" rounds half away from zero; Math.round rounds half up (toward +∞).
  // For negative .5 values these differ, so implement away-from-zero explicitly.
  const rounded = value < 0 ? -Math.round(-value) : Math.round(value);
  return String(rounded);
}

/** C# `{dt.ToLocalTime():HH:mm}` — local (machine-TZ) 24-hour clock, zero-padded. */
function formatLocalHhmm(d: Date): string {
  const hh = String(d.getHours()).padStart(2, "0");
  const mm = String(d.getMinutes()).padStart(2, "0");
  return `${hh}:${mm}`;
}

function stableSortByAsc<T>(items: T[], key: (t: T) => number): T[] {
  return items
    .map((item, index) => ({ item, index }))
    .sort((a, b) => {
      const ka = key(a.item);
      const kb = key(b.item);
      if (ka > kb) return 1;
      if (ka < kb) return -1;
      return a.index - b.index;
    })
    .map((x) => x.item);
}

function delay(ms: number, signal?: AbortSignal): Promise<void> {
  return new Promise<void>((resolve, reject) => {
    if (signal?.aborted) {
      reject(new Error("aborted"));
      return;
    }
    const t = setTimeout(() => {
      cleanup();
      resolve();
    }, ms);
    if (typeof t.unref === "function") t.unref();
    const onAbort = () => {
      clearTimeout(t);
      cleanup();
      reject(new Error("aborted"));
    };
    const cleanup = () => signal?.removeEventListener("abort", onAbort);
    signal?.addEventListener("abort", onAbort, { once: true });
  });
}
