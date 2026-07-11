// integration/geo/index.ts
// Full-parity port of CircleAI.Integration.Geo (C#). C# is the exact spec.
//
//   OpenMeteoWeatherProvider — Open-Meteo free, no-API-key weather (current +
//     hourly), WMO weather-code decode, m/s → km/h wind conversion.
//   OsrmRoutingProvider      — OSRM route/v1 client with mode→profile mapping and
//     GeoJSON polyline extraction.
//
// Both take the injected `IHttpClient` transport (the C# takes an `HttpClient`).
// All arithmetic is C# `double`, so no `Math.fround` applies.

import {
  type WeatherSample,
  type IWeatherProvider,
  type RouteEstimate,
  type IRoutingProvider,
  type GeoPoint,
  type IHttpClient,
  weatherSample,
  routeEstimate,
  ensureSuccess,
} from "../index.js";
import { tryParseDate } from "../calendar/index.js";

/** C# `double.ToString(CultureInfo.InvariantCulture)` — round-trip, '.' decimal, no group sep. */
function invNum(n: number): string {
  if (Number.isNaN(n)) return "NaN";
  if (n === Infinity) return "∞";
  if (n === -Infinity) return "-∞";
  return String(n);
}

// ── Open-Meteo weather ────────────────────────────────────────────────────────

/** Open-Meteo weather provider. Faithful port of C# `OpenMeteoWeatherProvider`. */
export class OpenMeteoWeatherProvider implements IWeatherProvider {
  private readonly http: IHttpClient;

  constructor(http: IHttpClient) {
    if (http == null) throw new Error("http required");
    this.http = http;
  }

  get providerId(): string {
    return "open-meteo";
  }

  async currentAsync(lat: number, lon: number, ct?: AbortSignal): Promise<WeatherSample> {
    const url =
      `https://api.open-meteo.com/v1/forecast?latitude=${invNum(lat)}&longitude=${invNum(lon)}` +
      "&current=temperature_2m,apparent_temperature,precipitation,wind_speed_10m,cloud_cover,weather_code";
    const resp = ensureSuccess(await this.http.send({ method: "GET", url, headers: new Map() }, ct));
    const root = JSON.parse(resp.body) as Record<string, unknown>;
    const cur = getProp(root, "current");
    const ts = getStr(cur, "time");
    return weatherSample(
      parseAssumeUtc(ts ?? new Date().toISOString()),
      getNum(cur, "temperature_2m"),
      getNum(cur, "apparent_temperature"),
      getNum(cur, "precipitation"),
      getNum(cur, "wind_speed_10m") * 3.6, // m/s → km/h
      getInt(cur, "cloud_cover"),
      wmoDecode(getInt(cur, "weather_code")),
    );
  }

  async hourlyAsync(lat: number, lon: number, hours: number, ct?: AbortSignal): Promise<readonly WeatherSample[]> {
    if (hours <= 0 || hours > 168) throw new Error("hours");
    const url =
      `https://api.open-meteo.com/v1/forecast?latitude=${invNum(lat)}&longitude=${invNum(lon)}` +
      "&hourly=temperature_2m,apparent_temperature,precipitation,wind_speed_10m,cloud_cover,weather_code" +
      `&forecast_hours=${hours}`;
    const resp = ensureSuccess(await this.http.send({ method: "GET", url, headers: new Map() }, ct));
    const root = JSON.parse(resp.body) as Record<string, unknown>;
    const h = getProp(root, "hourly");
    const time = getArr(h, "time");
    const temp = getArr(h, "temperature_2m");
    const feel = getArr(h, "apparent_temperature");
    const prec = getArr(h, "precipitation");
    const wind = getArr(h, "wind_speed_10m");
    const cld = getArr(h, "cloud_cover");
    const code = getArr(h, "weather_code");
    const n = Math.min(time.length, hours);
    const result: WeatherSample[] = [];
    for (let i = 0; i < n; i++) {
      result.push(
        weatherSample(
          parseAssumeUtc(typeof time[i] === "string" ? (time[i] as string) : ""),
          asNum(temp[i]),
          asNum(feel[i]),
          asNum(prec[i]),
          asNum(wind[i]) * 3.6,
          asInt(cld[i]),
          wmoDecode(asInt(code[i])),
        ),
      );
    }
    return result;
  }
}

/** C# WMO weather-code decode (Open-Meteo standard). */
function wmoDecode(code: number): string {
  switch (code) {
    case 0:
      return "clear sky";
    case 1:
    case 2:
    case 3:
      return "partly cloudy";
    case 45:
    case 48:
      return "fog";
    case 51:
    case 53:
    case 55:
      return "drizzle";
    case 56:
    case 57:
      return "freezing drizzle";
    case 61:
    case 63:
    case 65:
      return "rain";
    case 66:
    case 67:
      return "freezing rain";
    case 71:
    case 73:
    case 75:
      return "snow";
    case 77:
      return "snow grains";
    case 80:
    case 81:
    case 82:
      return "rain showers";
    case 85:
    case 86:
      return "snow showers";
    case 95:
      return "thunderstorm";
    case 96:
    case 99:
      return "thunderstorm with hail";
    default:
      return "unknown";
  }
}

// ── OSRM routing ──────────────────────────────────────────────────────────────

/** Options for {@link OsrmRoutingProvider}. Mirrors C# `OsrmOptions`. */
export interface OsrmOptions {
  /** OSRM host. Default the public demo server. */
  readonly host: string;
}

/** Constructs {@link OsrmOptions} (default host "https://router.project-osrm.org"). */
export function osrmOptions(host = "https://router.project-osrm.org"): OsrmOptions {
  return { host };
}

/** OSRM HTTP routing client. Faithful port of C# `OsrmRoutingProvider`. */
export class OsrmRoutingProvider implements IRoutingProvider {
  private readonly http: IHttpClient;
  private readonly opts: OsrmOptions;

  constructor(http: IHttpClient, opts: OsrmOptions = osrmOptions()) {
    if (opts == null) throw new Error("opts required");
    if (http == null) throw new Error("http required");
    this.opts = opts;
    this.http = http;
  }

  get providerId(): string {
    return "osrm";
  }

  async routeAsync(
    fromLat: number,
    fromLon: number,
    toLat: number,
    toLon: number,
    mode = "car",
    ct?: AbortSignal,
  ): Promise<RouteEstimate> {
    const profile = mode === "bike" || mode === "bicycle" ? "bike" : mode === "foot" || mode === "walk" ? "foot" : "driving";
    const url =
      `${this.opts.host.replace(/\/+$/, "")}/route/v1/${profile}/` +
      `${invNum(fromLon)},${invNum(fromLat)};` +
      `${invNum(toLon)},${invNum(toLat)}` +
      "?overview=full&geometries=geojson";
    const resp = ensureSuccess(await this.http.send({ method: "GET", url, headers: new Map() }, ct));
    const root = JSON.parse(resp.body) as Record<string, unknown>;

    const code = getStr(root, "code");
    if (code !== "Ok") throw new Error(`OSRM returned code=${code}`);

    const routes = root["routes"];
    if (!Array.isArray(routes) || routes.length === 0) throw new Error("OSRM returned no routes");
    const route = routes[0] as Record<string, unknown>;
    const dist = getNum(route, "distance"); // metres
    const dur = getNum(route, "duration"); // seconds
    const poly: GeoPoint[] = [];
    const geom = route["geometry"];
    if (geom != null && typeof geom === "object") {
      const coords = (geom as Record<string, unknown>)["coordinates"];
      if (Array.isArray(coords)) {
        for (const pt of coords) {
          if (!Array.isArray(pt) || pt.length < 2) continue;
          poly.push({ lat: asNum(pt[1]), lon: asNum(pt[0]) });
        }
      }
    }
    return routeEstimate(dist / 1000.0, dur * 1000, poly); // TimeSpan.FromSeconds(dur) → ms
  }
}

// ── JSON access helpers (mirror JsonElement.GetProperty / Get*) ───────────────

function getProp(obj: Record<string, unknown>, key: string): Record<string, unknown> {
  const v = obj[key];
  if (v == null || typeof v !== "object" || Array.isArray(v)) throw new Error(`missing object property '${key}'`);
  return v as Record<string, unknown>;
}
function getArr(obj: Record<string, unknown>, key: string): unknown[] {
  const v = obj[key];
  if (!Array.isArray(v)) throw new Error(`missing array property '${key}'`);
  return v;
}
function getStr(obj: Record<string, unknown>, key: string): string | null {
  const v = obj[key];
  return typeof v === "string" ? v : null;
}
function getNum(obj: Record<string, unknown>, key: string): number {
  const v = obj[key];
  if (typeof v !== "number") throw new Error(`property '${key}' is not a number`);
  return v;
}
function getInt(obj: Record<string, unknown>, key: string): number {
  return Math.trunc(getNum(obj, key));
}
function asNum(v: unknown): number {
  if (typeof v !== "number") throw new Error("expected number");
  return v;
}
function asInt(v: unknown): number {
  return Math.trunc(asNum(v));
}

/** C# `DateTimeOffset.Parse(ts, InvariantCulture, AssumeUniversal).ToUniversalTime()`. */
function parseAssumeUtc(ts: string): Date {
  const d = tryParseDate(ts);
  if (d === null) throw new Error(`unparseable timestamp '${ts}'`);
  return d;
}
