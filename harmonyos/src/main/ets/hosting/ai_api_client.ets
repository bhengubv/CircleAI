/**
 * hosting/ai_api_client
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// hosting/ai_api_client.ts
//
// Port of CircleAI.Hosting.AIApiClient — an IAIService that proxies every call
// to a remote ButlerAPI over HTTP/JSON, with SSE streaming. The C# version owns
// an HttpClient; the TS port routes through the same injectable
// {@link IHttpTransport} seam used by the loopback client, so it stays
// in-memory/testable while preserving the wire routes and payload shapes.
//
// Routes (relative to the ButlerAPI base):
//   GET  api/butler/health
//   POST api/butler/ask      { question }          → { text }
//   POST api/butler/chat     { messages, options }  → { text }
//   POST api/butler/stream   { messages, options }  → SSE  data: <token>
//   POST api/butler/agentic  { prompt, options }    → { text }
//   POST api/butler/tool     { name, arguments }    → ToolResult
//   POST api/butler/feedback { id, polarity, ... }

import type { GenerationOptions } from "../inference/index";
import type { ChatMessage } from "../models/index";
import type { UpgradeInfo } from "../models/index";
import { toolResultFailure } from "../tools/index";
import type { ToolInvocation, ToolResult } from "../tools/index";
import type { FeedbackSignal } from "../memory/index";
import type { IAIService } from "./service";
import type { HttpResponse, IHttpTransport } from "./endpoints";

interface GenerationOptionsWire {
  maxTokens?: number;
  temperature?: number;
  topP?: number;
  topK?: number;
  seed?: number;
  stopSequences?: string[];
}

function optionsWire(o: GenerationOptions | null | undefined): GenerationOptionsWire | null {
  if (o == null) return null;
  return {
    maxTokens: o.maxTokens,
    temperature: o.temperature,
    topP: o.topP,
    topK: o.topK,
    seed: o.seed,
    stopSequences: o.stopSequences,
  };
}

/**
 * {@link IAIService} that proxies requests to a remote ButlerAPI over
 * HTTP/JSON. Mirrors CircleAI.Hosting.AIApiClient.
 */
export class AIApiClient implements IAIService {
  private readonly transport: IHttpTransport;
  private readonly bearerToken: string | null;
  private ready = false;
  private disposed = false;

  /**
   * @param transport HTTP transport pointed at the ButlerAPI base.
   * @param bearerToken Optional bearer token sent in every request.
   */
  constructor(transport: IHttpTransport, bearerToken: string | null = null) {
    if (!transport) throw new Error("transport required");
    this.transport = transport;
    this.bearerToken = bearerToken;
  }

  get isReady(): boolean {
    return this.ready;
  }

  private get headers(): Record<string, string> {
    const h: Record<string, string> = { "Content-Type": "application/json" };
    if (this.bearerToken != null && this.bearerToken.trim().length > 0)
      h["Authorization"] = `Bearer ${this.bearerToken}`;
    return h;
  }

  async startAsync(): Promise<void> {
    const resp = await this.transport.sendAsync(
      "GET",
      "api/butler/health",
      this.headers,
      null,
    );
    ensureSuccess(resp);
    this.ready = true;
  }

  async stopAsync(): Promise<void> {
    this.ready = false;
  }

  async askAsync(question: string): Promise<string> {
    const resp = await this.transport.sendAsync(
      "POST",
      "api/butler/ask",
      this.headers,
      JSON.stringify({ question }),
    );
    ensureSuccess(resp);
    const payload = parse<{ text?: string }>(resp.body);
    return payload?.text ?? "";
  }

  async chatAsync(
    messages: readonly ChatMessage[],
    options?: GenerationOptions | null,
  ): Promise<string> {
    const resp = await this.transport.sendAsync(
      "POST",
      "api/butler/chat",
      this.headers,
      JSON.stringify({ messages, options: optionsWire(options) }),
    );
    ensureSuccess(resp);
    const payload = parse<{ text?: string }>(resp.body);
    return payload?.text ?? "";
  }

  async *streamAsync(
    messages: readonly ChatMessage[],
    options?: GenerationOptions | null,
  ): AsyncGenerator<string> {
    const resp = await this.transport.sendAsync(
      "POST",
      "api/butler/stream",
      { ...this.headers, Accept: "text/event-stream" },
      JSON.stringify({ messages, options: optionsWire(options) }),
    );
    ensureSuccess(resp);

    for await (const line of iterateSseLines(resp)) {
      if (!line.startsWith("data:")) continue;
      const token = line.slice("data:".length).trim();
      if (token === "[DONE]") return;
      if (token.length > 0) yield token;
    }
  }

  async agenticChatAsync(
    prompt: string,
    options?: GenerationOptions | null,
  ): Promise<string> {
    const resp = await this.transport.sendAsync(
      "POST",
      "api/butler/agentic",
      this.headers,
      JSON.stringify({ prompt, options: optionsWire(options) }),
    );
    ensureSuccess(resp);
    const payload = parse<{ text?: string }>(resp.body);
    return payload?.text ?? "";
  }

  async invokeToolAsync(invocation: ToolInvocation): Promise<ToolResult> {
    const resp = await this.transport.sendAsync(
      "POST",
      "api/butler/tool",
      this.headers,
      JSON.stringify({ name: invocation.toolName, arguments: invocation.arguments }),
    );
    ensureSuccess(resp);
    const result = parse<ToolResult>(resp.body);
    return result ?? toolResultFailure(invocation.toolName, "Empty response from cloud");
  }

  async submitFeedbackAsync(signal: FeedbackSignal): Promise<void> {
    const resp = await this.transport.sendAsync(
      "POST",
      "api/butler/feedback",
      this.headers,
      JSON.stringify({
        id: signal.id,
        polarity: signal.polarity,
        userText: signal.userText,
        assistantText: signal.assistantText,
        comment: signal.comment ?? null,
      }),
    );
    ensureSuccess(resp);
  }

  /** Cloud proxy exposes the default (empty) upgrade behaviour. */
  async checkForUpgradesAsync(): Promise<readonly UpgradeInfo[]> {
    return [];
  }

  /** Default prewarm delegates to startAsync (matches the interface default). */
  async prewarmAsync(): Promise<void> {
    await this.startAsync();
  }

  async disposeAsync(): Promise<void> {
    if (this.disposed) return;
    this.disposed = true;
  }
}

function ensureSuccess(resp: HttpResponse): void {
  if (resp.status < 200 || resp.status >= 300)
    throw new Error(`HTTP ${resp.status}: ${resp.body ?? ""}`);
}

function parse<T>(body: string | undefined): T | null {
  if (body == null || body.trim().length === 0) return null;
  try {
    return JSON.parse(body) as T;
  } catch {
    return null;
  }
}

async function* iterateSseLines(resp: HttpResponse): AsyncGenerator<string> {
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
