// realtime-cloud/ultravox_service.ts
//
// (3.3.0) IRealtimeService backed by Ultravox (UltravoxService.cs). Two-step:
// POST /api/calls to create a call → returns joinUrl → open WS to joinUrl.
//
// HTTP seam: the C# uses System.Net.Http.HttpClient. Following the port's
// "inject the platform seam" convention (mirroring the WebSocket transport
// seam), the HTTP surface is injected behind IRealtimeHttpClient with a
// throwing Null default, so the connector stays framework-free (no hard fetch
// dependency). The two-step create-call logic + joinUrl extraction is ported
// one-to-one.

import type { IRealtimeService, IRealtimeSession, RealtimeSessionConfig } from "../realtime/index.js";
import type { UltravoxOptions } from "./options.js";
import {
  NullRealtimeLogger,
  NullRealtimeTransportFactory,
  type IRealtimeLogger,
  type IRealtimeTransportFactory,
} from "./transport.js";
import { RealtimeWebSocketSession } from "./websocket_session.js";

/** A minimal HTTP POST-JSON response. Mirrors the slice of `HttpResponseMessage` the connector reads. */
export interface RealtimeHttpResponse {
  /** True for a 2xx status (mirrors `EnsureSuccessStatusCode` passing). */
  readonly ok: boolean;
  /** HTTP status code, for the error message when `ok` is false. */
  readonly status: number;
  /** Response body text (JSON is parsed by the caller). */
  readonly bodyText: string;
}

/**
 * (3.3.0) HTTP seam for the Ultravox create-call step — the injected analogue of
 * `HttpClient`. Implementations wrap `fetch`/`undici`/etc.; the default throws.
 */
export interface IRealtimeHttpClient {
  /** POST a JSON body to `url` with headers; resolve the response. */
  postJsonAsync(
    url: string,
    jsonBody: string,
    headers: ReadonlyMap<string, string>,
    signal?: AbortSignal,
  ): Promise<RealtimeHttpResponse>;
}

/** No-op {@link IRealtimeHttpClient} that throws on use. Mirrors the "host wires the real one" default. */
export class NullRealtimeHttpClient implements IRealtimeHttpClient {
  static readonly instance = new NullRealtimeHttpClient();
  postJsonAsync(
    _url: string,
    _jsonBody: string,
    _headers: ReadonlyMap<string, string>,
    _signal?: AbortSignal,
  ): Promise<RealtimeHttpResponse> {
    throw new Error(
      "No IRealtimeHttpClient is registered. Ultravox needs an HTTP client to create the call; inject a fetch-backed implementation.",
    );
  }
}

/** (3.3.0) {@link IRealtimeService} backed by Ultravox. Mirrors C# `UltravoxService`. */
export class UltravoxService implements IRealtimeService {
  private readonly http: IRealtimeHttpClient;
  private readonly options: UltravoxOptions;
  private readonly transports: IRealtimeTransportFactory;
  private readonly logger: IRealtimeLogger;

  constructor(
    http: IRealtimeHttpClient,
    options: UltravoxOptions,
    transports: IRealtimeTransportFactory = NullRealtimeTransportFactory.instance,
    logger: IRealtimeLogger = NullRealtimeLogger.instance,
  ) {
    if (http == null) throw new Error("http required");
    if (options == null) throw new Error("options required");
    this.http = http;
    this.options = options;
    this.transports = transports ?? NullRealtimeTransportFactory.instance;
    this.logger = logger ?? NullRealtimeLogger.instance;
  }

  get providerId(): string {
    return "ultravox";
  }

  get isConfigured(): boolean {
    return !isBlank(this.options.apiKey);
  }

  async startSessionAsync(config: RealtimeSessionConfig, signal?: AbortSignal): Promise<IRealtimeSession> {
    if (config == null) throw new Error("config required");
    this.ensureConfigured();

    const modelToUse = isBlank(config.model) ? this.options.defaultModel : config.model;
    const voiceToUse = isBlank(config.voiceId) ? this.options.defaultVoice : (config.voiceId as string);

    const body = JSON.stringify({
      model: modelToUse,
      voice: voiceToUse,
      systemPrompt: config.systemPrompt,
      medium: { serverWebSocket: { inputSampleRate: 16000, outputSampleRate: 24000 } },
    });

    const url = joinUrlPath(this.options.apiEndpoint, "/api/calls");
    const headers = new Map<string, string>([["X-API-Key", this.options.apiKey as string]]);

    const resp = await this.http.postJsonAsync(url, body, headers, signal);
    if (!resp.ok) {
      throw new Error(`Ultravox create-call failed with status ${resp.status}.`);
    }

    let joinUrl: string | null = null;
    try {
      const doc = JSON.parse(resp.bodyText) as Record<string, unknown>;
      joinUrl = typeof doc["joinUrl"] === "string" ? (doc["joinUrl"] as string) : null;
    } catch {
      joinUrl = null;
    }
    if (isBlank(joinUrl)) {
      throw new Error("Ultravox API did not return a joinUrl.");
    }

    const transport = await this.transports.connectAsync(joinUrl as string, null, signal);
    return new RealtimeWebSocketSession(transport, config, this.providerId, this.logger);
  }

  private ensureConfigured(): void {
    if (!this.isConfigured) {
      throw new Error("Ultravox is not configured. Set UltravoxOptions.apiKey before calling startSessionAsync.");
    }
  }
}

/** Join a base endpoint + path, avoiding a doubled slash. */
function joinUrlPath(base: string, path: string): string {
  const b = base.endsWith("/") ? base.slice(0, -1) : base;
  const p = path.startsWith("/") ? path : `/${path}`;
  return `${b}${p}`;
}

function isBlank(s: string | null | undefined): boolean {
  return s == null || s.trim().length === 0;
}
