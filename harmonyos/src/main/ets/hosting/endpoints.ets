/**
 * hosting/endpoints
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// hosting/endpoints.ts
//
// Port of the CircleAI.Hosting transport surface:
//   • IAIEndpoint.cs         — IAIEndpoint
//   • Endpoints/InProcessEndpoint.cs
//   • Endpoints/HttpLoopbackEndpoint.cs
//   • Endpoints/AIHttpClient.cs
//
// The C# HttpLoopbackEndpoint binds System.Net.HttpListener on 127.0.0.1 and
// AIHttpClient speaks HTTP over the wire. The TS port keeps ALL of the protocol
// behaviour — the route table, the X-Butler-Token constant-time auth, the SSE
// `data: <json>\n\n` framing with a trailing `event: done`, and the exact JSON
// payload shapes — but runs the transport in-memory through an injectable
// {@link IHttpTransport} seam (no sockets). An in-memory loopback transport
// dispatches client calls straight into the endpoint's handler, so the loopback
// endpoint + client round-trip is exercisable without a network.

import type {
  GenerationOptions,
  IChatGenerator,
} from "../inference/index";
import type { ChatMessage } from "../models/index";
import type { ToolInvocation, ToolResult } from "../tools/index";
import type { IAIService } from "./service";
import { AIOptions, generateRandomToken } from "./options";

// ─────────────────────────────────────────────────────────────────────────────
// IAIEndpoint
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Transport-agnostic endpoint that exposes a {@link IAIService}. Mirrors
 * CircleAI.Hosting.IAIEndpoint.
 */
export interface IAIEndpoint {
  /** Begins serving requests against `service`. Idempotent. */
  startAsync(service: IAIService): Promise<void>;
  /** Stops accepting new requests. */
  stopAsync(): Promise<void>;
  /** Async-dispose. */
  disposeAsync(): Promise<void>;
}

// ─────────────────────────────────────────────────────────────────────────────
// InProcessEndpoint
// ─────────────────────────────────────────────────────────────────────────────

/**
 * In-process endpoint — no transport, just exposes the underlying service.
 * Mirrors CircleAI.Hosting.Endpoints.InProcessEndpoint.
 */
export class InProcessEndpoint implements IAIEndpoint {
  private service: IAIService | null = null;
  private started = false;
  private disposed = false;

  /** The wrapped service. null until startAsync has run. */
  get serviceAccessor(): IAIService | null {
    return this.service;
  }

  async startAsync(service: IAIService): Promise<void> {
    if (this.disposed) throw new Error("InProcessEndpoint is disposed.");
    if (this.started) return;
    if (!service) throw new Error("service required");
    this.service = service;
    this.started = true;
  }

  async stopAsync(): Promise<void> {
    this.started = false;
    this.service = null;
  }

  async disposeAsync(): Promise<void> {
    if (this.disposed) return;
    this.disposed = true;
    this.service = null;
    this.started = false;
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// HTTP transport seam
// ─────────────────────────────────────────────────────────────────────────────

/** A minimal HTTP response. `sse` is set for streaming (text/event-stream) bodies. */
export interface HttpResponse {
  readonly status: number;
  readonly contentType: string;
  /** For non-streaming responses: the full body text. */
  readonly body?: string;
  /** For streaming responses: an async iterable of raw body chunks. */
  readonly sse?: AsyncIterable<string>;
}

/**
 * Injectable HTTP transport used by {@link AIHttpClient}/{@link AIApiClient}.
 * The in-memory {@link LoopbackHttpTransport} dispatches straight into a
 * {@link HttpLoopbackEndpoint}; a real host would supply a fetch-backed one.
 */
export interface IHttpTransport {
  sendAsync(
    method: string,
    path: string,
    headers: Readonly<Map<string, string>>,
    body: string | null,
  ): Promise<HttpResponse>;
}

// ─────────────────────────────────────────────────────────────────────────────
// Wire payloads (mirror the C# private record/DTO shapes)
// ─────────────────────────────────────────────────────────────────────────────

interface GenerationOptionsPayload {
  maxTokens?: number;
  temperature?: number;
  topP?: number;
  topK?: number;
  seed?: number;
  stopSequences?: string[];
}

function optionsToPayload(o: GenerationOptions): GenerationOptionsPayload {
  return {
    maxTokens: o.maxTokens,
    temperature: o.temperature,
    topP: o.topP,
    topK: o.topK,
    seed: o.seed,
    stopSequences: o.stopSequences,
  };
}

function payloadToOptions(p: GenerationOptionsPayload | undefined): GenerationOptions | undefined {
  if (p == null) return undefined;
  return {
    maxTokens: p.maxTokens,
    temperature: p.temperature,
    topP: p.topP,
    topK: p.topK,
    seed: p.seed,
    stopSequences: p.stopSequences,
  };
}

// ─────────────────────────────────────────────────────────────────────────────
// HttpLoopbackEndpoint
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Loopback endpoint for {@link IAIService}. Mirrors
 * CircleAI.Hosting.Endpoints.HttpLoopbackEndpoint — the route table, token
 * auth, JSON payloads, and SSE framing are preserved. The transport is
 * in-memory: call {@link handleAsync} (or route a {@link LoopbackHttpTransport}
 * through it) to dispatch a request.
 *
 * Routes (all POST):
 *   /butler/ask    { question }               → text/plain
 *   /butler/chat   { messages, options }       → { content }
 *   /butler/stream { messages, options }       → text/event-stream
 *   /butler/tool   { toolName, arguments }      → ToolResult JSON
 */
export class HttpLoopbackEndpoint implements IAIEndpoint {
  private readonly options: AIOptions;
  private service: IAIService | null = null;
  private _token: string | null = null;
  private _boundPort = 0;
  private started = false;
  private disposed = false;

  constructor(options: AIOptions) {
    if (!options) throw new Error("options required");
    this.options = options;
  }

  /** Port the listener is bound to. 0 when not started (mirrors BoundPort). */
  get boundPort(): number {
    return this._boundPort;
  }

  /** Effective shared-secret token. null when not started (mirrors Token). */
  get token(): string | null {
    return this._token;
  }

  async startAsync(service: IAIService): Promise<void> {
    if (this.disposed) throw new Error("HttpLoopbackEndpoint is disposed.");
    if (!service) throw new Error("service required");
    if (this.started) return;

    this.service = service;
    this._token =
      this.options.loopbackToken == null || this.options.loopbackToken.length === 0
        ? generateRandomToken()
        : this.options.loopbackToken;

    const configured = this.options.loopbackPort ?? 0;
    // No real socket bind — mirror "OS picks a port" by synthesising one.
    this._boundPort = configured > 0 ? configured : pickPseudoPort();
    this.started = true;
  }

  async stopAsync(): Promise<void> {
    if (!this.started) return;
    this.started = false;
    this.service = null;
  }

  async disposeAsync(): Promise<void> {
    if (this.disposed) return;
    this.disposed = true;
    try {
      await this.stopAsync();
    } catch {
      /* DisposeAsync must not throw */
    }
  }

  /**
   * Dispatch one request through the endpoint's handler. Returns the framed
   * HTTP response. Mirrors HandleRequestAsync's routing + auth.
   */
  async handleAsync(
    method: string,
    path: string,
    headers: Readonly<Map<string, string>>,
    body: string | null,
  ): Promise<HttpResponse> {
    if (!this.authorise(headers)) return plain(401, "unauthorised");
    if (method.toUpperCase() !== "POST") return plain(405, "method not allowed");

    switch (path) {
      case "/butler/ask":
        return this.handleAsk(body);
      case "/butler/chat":
        return this.handleChat(body);
      case "/butler/stream":
        return this.handleStream(body);
      case "/butler/tool":
        return this.handleTool(body);
      default:
        return plain(404, "not found");
    }
  }

  private async handleAsk(body: string | null): Promise<HttpResponse> {
    const service = this.requireService();
    const payload = deserialise<{ question?: string }>(body);
    if (payload == null || payload.question == null || payload.question.trim().length === 0)
      return plain(400, "missing 'question'");
    const answer = await service.askAsync(payload.question);
    return plain(200, answer);
  }

  private async handleChat(body: string | null): Promise<HttpResponse> {
    const service = this.requireService();
    const payload = deserialise<ChatPayload>(body);
    if (payload?.messages == null || payload.messages.length === 0)
      return plain(400, "missing 'messages'");

    const messages: ChatMessage[] = payload.messages.map((m) => ({
      role: m.role ?? "user",
      content: m.content ?? "",
    }));
    const options = payloadToOptions(payload.options);
    const content = await service.chatAsync(messages, options);
    return json(200, { content });
  }

  private async handleStream(body: string | null): Promise<HttpResponse> {
    const service = this.requireService();
    const payload = deserialise<ChatPayload>(body);
    if (payload?.messages == null || payload.messages.length === 0)
      return plain(400, "missing 'messages'");

    const messages: ChatMessage[] = payload.messages.map((m) => ({
      role: m.role ?? "user",
      content: m.content ?? "",
    }));
    const options = payloadToOptions(payload.options);

    const sse = (async function* () {
      for await (const piece of service.streamAsync(messages, options)) {
        // Standard SSE framing; JSON-encode the chunk so newlines/quotes
        // inside it don't break the framing.
        yield `data: ${JSON.stringify(piece)}\n\n`;
      }
      // Closing event so clients know we're done cleanly.
      yield `event: done\ndata: {}\n\n`;
    })();

    return { status: 200, contentType: "text/event-stream", sse };
  }

  private async handleTool(body: string | null): Promise<HttpResponse> {
    const service = this.requireService();
    const payload = deserialise<ToolPayload>(body);
    if (payload == null || payload.toolName == null || payload.toolName.trim().length === 0)
      return plain(400, "missing 'toolName'");

    const invocation: ToolInvocation = {
      toolName: payload.toolName,
      arguments: payload.arguments ?? {},
    };
    const result = await service.invokeToolAsync(invocation);
    return json(result.success ? 200 : 502, result);
  }

  private authorise(headers: Readonly<Map<string, string>>): boolean {
    const token = this._token;
    if (token == null || token.length === 0) return false;
    const supplied = headerValue(headers, "X-Butler-Token");
    return (
      supplied != null &&
      supplied.length > 0 &&
      cryptographicEquals(supplied, token)
    );
  }

  private requireService(): IAIService {
    if (this.service === null)
      throw new Error("HttpLoopbackEndpoint has no service bound.");
    return this.service;
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// LoopbackHttpTransport — in-memory transport → HttpLoopbackEndpoint
// ─────────────────────────────────────────────────────────────────────────────

/** In-memory {@link IHttpTransport} that dispatches into a loopback endpoint. */
export class LoopbackHttpTransport implements IHttpTransport {
  constructor(private readonly endpoint: HttpLoopbackEndpoint) {
    if (!endpoint) throw new Error("endpoint required");
  }

  sendAsync(
    method: string,
    path: string,
    headers: Readonly<Map<string, string>>,
    body: string | null,
  ): Promise<HttpResponse> {
    // The endpoint routes on the absolute path; strip a leading base if present.
    const p = path.startsWith("/") ? path : `/${path}`;
    return this.endpoint.handleAsync(method, p, headers, body);
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// AIHttpClient — out-of-process client for HttpLoopbackEndpoint
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Client that talks to a {@link HttpLoopbackEndpoint} through an injected
 * {@link IHttpTransport}. Methods mirror {@link IAIService}. Mirrors
 * CircleAI.Hosting.Endpoints.AIHttpClient.
 */
export class AIHttpClient {
  private readonly transport: IHttpTransport;
  private readonly token: string;

  constructor(transport: IHttpTransport, token: string) {
    if (!transport) throw new Error("transport required");
    if (token == null || token.length === 0) throw new Error("token required");
    this.transport = transport;
    this.token = token;
  }

  private get headers(): Map<string, string> {
    return { "X-Butler-Token": this.token, Accept: "application/json" };
  }

  async askAsync(question: string): Promise<string> {
    if (question == null || question.length === 0) throw new Error("question required");
    const resp = await this.transport.sendAsync(
      "POST",
      "butler/ask",
      this.headers,
      JSON.stringify({ question }),
    );
    ensureSuccess(resp);
    return resp.body ?? "";
  }

  async chatAsync(
    messages: readonly ChatMessage[],
    options?: GenerationOptions | null,
  ): Promise<string> {
    if (!messages) throw new Error("messages required");
    const payload: ChatPayload = {
      messages: convertMessages(messages),
      options: options == null ? undefined : optionsToPayload(options),
    };
    const resp = await this.transport.sendAsync(
      "POST",
      "butler/chat",
      this.headers,
      JSON.stringify(payload),
    );
    ensureSuccess(resp);
    const parsed = deserialise<{ content?: string }>(resp.body ?? null);
    return parsed?.content ?? "";
  }

  async *streamAsync(
    messages: readonly ChatMessage[],
    options?: GenerationOptions | null,
  ): AsyncGenerator<string> {
    if (!messages) throw new Error("messages required");
    const payload: ChatPayload = {
      messages: convertMessages(messages),
      options: options == null ? undefined : optionsToPayload(options),
    };
    const resp = await this.transport.sendAsync(
      "POST",
      "butler/stream",
      this.headers,
      JSON.stringify(payload),
    );
    ensureSuccess(resp);

    for await (const line of iterateSseLines(resp)) {
      if (line.length === 0) continue; // SSE event separator
      if (line.startsWith("event:")) {
        if (line.slice(6).trim() === "done") return;
        continue;
      }
      if (!line.startsWith("data:")) continue;

      const dataPart = line.slice(5).replace(/^\s+/, "");
      if (dataPart.length === 0) continue;

      let piece: string | null;
      try {
        piece = JSON.parse(dataPart) as string;
      } catch {
        piece = dataPart;
      }
      if (piece != null && piece.length > 0) yield piece;
    }
  }

  async invokeToolAsync(invocation: ToolInvocation): Promise<ToolResult> {
    if (!invocation) throw new Error("invocation required");
    const payload: ToolPayload = {
      toolName: invocation.toolName,
      arguments: { ...invocation.arguments },
    };
    const resp = await this.transport.sendAsync(
      "POST",
      "butler/tool",
      this.headers,
      JSON.stringify(payload),
    );
    // Accept 200 (success) and 502 (tool failure); anything else is transport error.
    if (resp.status !== 200 && resp.status !== 502) ensureSuccess(resp);

    const result = deserialise<ToolResult>(resp.body ?? null);
    return (
      result ?? {
        toolName: invocation.toolName,
        success: false,
        error: "Empty response from Butler endpoint.",
      }
    );
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Shared payload/helper types
// ─────────────────────────────────────────────────────────────────────────────

interface ChatMessagePayload {
  role?: string;
  content?: string;
}

interface ChatPayload {
  messages?: ChatMessagePayload[];
  options?: GenerationOptionsPayload;
}

interface ToolPayload {
  toolName?: string;
  arguments?: Map<string, unknown>;
}

function convertMessages(messages: readonly ChatMessage[]): ChatMessagePayload[] {
  return messages.map((m) => ({ role: m.role, content: m.content }));
}

async function* iterateSseLines(resp: HttpResponse): AsyncGenerator<string> {
  // The endpoint emits complete SSE frames (chunks). Split into lines the way a
  // StreamReader.ReadLineAsync loop would, preserving blank separator lines.
  const source = resp.sse;
  if (source == null) {
    if (resp.body != null) for (const line of resp.body.split("\n")) yield line;
    return;
  }
  let buffer = "";
  for await (const chunk of source) {
    buffer += chunk;
    let nl: number;
    while ((nl = buffer.indexOf("\n")) >= 0) {
      yield buffer.slice(0, nl);
      buffer = buffer.slice(nl + 1);
    }
  }
  if (buffer.length > 0) yield buffer;
}

function plain(status: number, text: string): HttpResponse {
  return { status, contentType: "text/plain; charset=utf-8", body: text };
}

function json(status: number, payload: Object): HttpResponse {
  return {
    status,
    contentType: "application/json; charset=utf-8",
    body: JSON.stringify(payload),
  };
}

function deserialise<T>(body: string | null): T | null {
  if (body == null || body.trim().length === 0) return null;
  try {
    return JSON.parse(body) as T;
  } catch {
    return null;
  }
}

function headerValue(
  headers: Readonly<Map<string, string>>,
  name: string,
): string | undefined {
  const lower = name.toLowerCase();
  for (const [k, v] of Object.entries(headers)) if (k.toLowerCase() === lower) return v;
  return undefined;
}

/** Constant-time string comparison. Mirrors HttpLoopbackEndpoint.CryptographicEquals. */
function cryptographicEquals(a: string, b: string): boolean {
  if (a.length !== b.length) return false;
  let diff = 0;
  for (let i = 0; i < a.length; i++) diff |= a.charCodeAt(i) ^ b.charCodeAt(i);
  return diff === 0;
}

function ensureSuccess(resp: HttpResponse): void {
  if (resp.status < 200 || resp.status >= 300)
    throw new Error(`HTTP ${resp.status}: ${resp.body ?? ""}`);
}

let pseudoPortSeq = 49152;
function pickPseudoPort(): number {
  // Ephemeral-range pseudo-port for the in-memory endpoint (no real socket).
  pseudoPortSeq = pseudoPortSeq >= 65535 ? 49152 : pseudoPortSeq + 1;
  return pseudoPortSeq;
}

// Keep IChatGenerator import meaningful for downstream typing consumers.
export type { IChatGenerator };
