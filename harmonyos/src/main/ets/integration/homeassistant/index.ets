/**
 * integration/homeassistant/index
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// integration/homeassistant/index.ts
// Full-parity port of CircleAI.Integration.HomeAssistant (C#). C# is the exact spec.
//
// HomeAssistantConnector — HA REST API client (long-lived bearer token). Lists
// entities (deriving domain from the entity_id prefix and flattening attributes
// to strings), calls services, and offers turn_on / turn_off convenience wrappers.
//
// Takes the injected `IHttpClient` transport (the C# takes an `HttpClient`).

import {
  type HaEntity,
  type IHomeAutomationConnector,
  type IHttpClient,
  haEntity,
  ensureSuccess,
  isNullOrWhiteSpace,
  isNullOrEmpty,
} from "../index";

/** Options for {@link HomeAssistantConnector}. Mirrors C# `HomeAssistantOptions`. */
export interface HomeAssistantOptions {
  /** HA base URL, e.g. "http://homeassistant.local:8123/" — must include trailing slash. */
  readonly baseUrl: string;
  /** Long-lived access token. */
  readonly accessToken: string;
}

/** Constructs {@link HomeAssistantOptions}. */
export function homeAssistantOptions(baseUrl: string, accessToken: string): HomeAssistantOptions {
  return { baseUrl, accessToken };
}

/**
 * Combines the HA base URL with a relative API path, mirroring `HttpClient`'s
 * BaseAddress + relative-URI behaviour (base must end with "/").
 */
function combineBase(baseUrl: string, path: string): string {
  const base = baseUrl.endsWith("/") ? baseUrl : baseUrl + "/";
  try {
    return new URL(path, base).toString();
  } catch {
    return base + path;
  }
}

/** HomeAssistant REST connector. Faithful port of C# `HomeAssistantConnector`. */
export class HomeAssistantConnector implements IHomeAutomationConnector {
  private readonly http: IHttpClient;
  private readonly opts: HomeAssistantOptions;
  private readonly authHeader: string | null;

  constructor(opts: HomeAssistantOptions, http: IHttpClient) {
    if (opts == null) throw new Error("opts required");
    if (http == null) throw new Error("http required");
    this.opts = opts;
    this.http = http;
    this.authHeader = isNullOrWhiteSpace(opts.accessToken) ? null : `Bearer ${opts.accessToken}`;
  }

  get providerId(): string {
    return "home-assistant";
  }
  get isConfigured(): boolean {
    return this.opts.baseUrl != null && this.opts.baseUrl.length > 0 && !isNullOrWhiteSpace(this.opts.accessToken);
  }

  async listEntitiesAsync(ct?: AbortSignal): Promise<readonly HaEntity[]> {
    const resp = ensureSuccess(
      await this.http.send({ method: "GET", url: combineBase(this.opts.baseUrl, "api/states"), headers: this.headers() }, ct),
    );
    const root = JSON.parse(resp.body) as unknown;

    const list: HaEntity[] = [];
    if (!Array.isArray(root)) return list;
    for (const stUnknown of root) {
      const st = stUnknown as Record<string, unknown>;
      const entityId = typeof st["entity_id"] === "string" ? (st["entity_id"] as string) : "";
      if (isNullOrEmpty(entityId)) continue;
      const state = typeof st["state"] === "string" ? (st["state"] as string) : "";
      const domain = entityId.split(".", 2)[0];
      const attrs = new Map<string, string>();
      let friendly = entityId;
      const attEl = st["attributes"];
      if (attEl != null && typeof attEl === "object" && !Array.isArray(attEl)) {
        for (const [name, value] of Object.entries(attEl as Record<string, unknown>)) {
          attrs.set(name, jsonElementToString(value));
          // C#: if name == "friendly_name" && value is a JSON string → friendly = value ?? entityId.
          if (name === "friendly_name" && typeof value === "string") friendly = value;
        }
      }
      list.push(haEntity(entityId, friendly, domain, state, attrs));
    }
    return list;
  }

  async callServiceAsync(
    domain: string,
    service: string,
    data: ReadonlyMap<string, unknown> | null | undefined,
    ct?: AbortSignal,
  ): Promise<void> {
    if (isNullOrWhiteSpace(domain)) throw new Error("domain required");
    if (isNullOrWhiteSpace(service)) throw new Error("service required");
    const payloadObj: Record<string, unknown> = {};
    if (data != null) for (const [k, v] of data) payloadObj[k] = v;
    const resp = await this.http.send(
      {
        method: "POST",
        url: combineBase(this.opts.baseUrl, `api/services/${encodeURIComponent(domain)}/${encodeURIComponent(service)}`),
        headers: this.headers("application/json; charset=utf-8"),
        body: JSON.stringify(payloadObj),
      },
      ct,
    );
    ensureSuccess(resp);
  }

  /** Convenience: turn an entity on via homeassistant.turn_on. */
  turnOnAsync(entityId: string, ct?: AbortSignal): Promise<void> {
    return this.callServiceAsync("homeassistant", "turn_on", new Map([["entity_id", entityId]]), ct);
  }

  /** Convenience: turn an entity off via homeassistant.turn_off. */
  turnOffAsync(entityId: string, ct?: AbortSignal): Promise<void> {
    return this.callServiceAsync("homeassistant", "turn_off", new Map([["entity_id", entityId]]), ct);
  }

  private headers(contentType?: string): Map<string, string> {
    const h = new Map<string, string>();
    if (this.authHeader !== null) h.set("Authorization", this.authHeader);
    if (contentType !== undefined) h.set("Content-Type", contentType);
    return h;
  }
}

/**
 * Mirrors C# `HomeAssistantConnector`'s attribute flattening:
 *   String → the string; Number → JsonElement.ToString() (raw JSON number text);
 *   True → "true"; False → "false"; anything else → JsonElement.ToString() (its
 *   raw JSON serialization).
 */
function jsonElementToString(value: unknown): string {
  if (typeof value === "string") return value;
  if (typeof value === "number") return numberToJsonText(value);
  if (value === true) return "true";
  if (value === false) return "false";
  if (value === null) return "null";
  // Object / array → their JSON text (JsonElement.ToString()).
  return JSON.stringify(value);
}

/** Renders a number the way System.Text.Json emits it (integers plain; no trailing ".0"). */
function numberToJsonText(n: number): string {
  if (Number.isNaN(n)) return "NaN";
  if (n === Infinity) return "Infinity";
  if (n === -Infinity) return "-Infinity";
  return String(n);
}
