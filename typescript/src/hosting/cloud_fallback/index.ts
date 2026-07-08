// hosting/cloud_fallback/index.ts
//
// Port of CircleAI.Hosting.CloudFallback:
//   • CloudFallbackChain.cs — IConfigurableChatGenerator + start-of-call
//     ordering chain (first ready generator wins; fail-soft frames are skipped)
//   • BackupBrainOrchestrator.cs — mid-turn failover with degrade/cool-down
//   • ServerSentEventsReader.cs — shared SSE frame reader
//   • Options.cs — per-provider option shapes
//   • OpenAiChatGenerator / AnthropicChatGenerator / GeminiChatGenerator and the
//     OpenAI-compatible base (Groq/Cerebras/Together/DeepSeek)
//
// Cloud generators speak the real wire formats but send through the injectable
// {@link IHttpTransport} seam (no sockets), so tests use the deterministic
// {@link FakeConfigurableChatGenerator} or a fake transport. The chain and
// orchestrator — the deterministic failover logic — are ported verbatim.

import type {
  GenerationOptions,
  IChatGenerator,
} from "../../inference/index.js";
import type { ChatMessage } from "../../models/index.js";
import type { HttpResponse, IHttpTransport } from "../endpoints.js";

// ─────────────────────────────────────────────────────────────────────────────
// IConfigurableChatGenerator
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Reports whether a generator can currently serve calls. Mirrors
 * CircleAI.Hosting.CloudFallback.IConfigurableChatGenerator.
 */
export interface IConfigurableChatGenerator extends IChatGenerator {
  /** True when the generator can serve calls (e.g. API key present). */
  readonly isConfigured: boolean;
  /** Display name (e.g. "OpenAI · gpt-4o-mini"). */
  readonly engineLabel: string;
  /** Human-readable explanation of the current state. */
  readonly statusMessage: string;
}

function isConfigurable(g: IChatGenerator): g is IConfigurableChatGenerator {
  return typeof (g as Partial<IConfigurableChatGenerator>).isConfigured === "boolean";
}

// ─────────────────────────────────────────────────────────────────────────────
// CloudFallbackChain
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Tries an ordered list of {@link IChatGenerator}s and uses the first one ready.
 * Mirrors CircleAI.Hosting.CloudFallback.CloudFallbackChain.
 */
export class CloudFallbackChain implements IChatGenerator {
  private readonly generators: readonly IChatGenerator[];

  constructor(generators: Iterable<IChatGenerator>) {
    if (!generators) throw new Error("generators required");
    this.generators = [...generators];
  }

  get generatorList(): readonly IChatGenerator[] {
    return this.generators;
  }

  async generateAsync(
    messages: readonly ChatMessage[],
    options?: GenerationOptions,
  ): Promise<string> {
    for (const g of this.generators) {
      if (!isReady(g)) continue;
      try {
        return await g.generateAsync(messages, options);
      } catch {
        // Fall through to the next generator.
      }
    }
    return "[CloudFallbackChain: no configured generator could serve the request]";
  }

  async *streamAsync(
    messages: readonly ChatMessage[],
    options?: GenerationOptions,
  ): AsyncGenerator<string> {
    for (const g of this.generators) {
      if (!isReady(g)) continue;

      const iterator = g.streamAsync(messages, options)[Symbol.asyncIterator]();
      let yielded = false;
      try {
        for (;;) {
          let res: IteratorResult<string>;
          try {
            res = await iterator.next();
          } catch {
            // Faulted mid-stream; move on iff nothing yielded yet.
            if (yielded) return;
            break;
          }
          if (res.done) return;

          const chunk = res.value;
          if (!yielded && isFailSoftFrame(chunk)) {
            // Generator declined the call (e.g. no API key).
            break;
          }
          yielded = true;
          yield chunk;
        }
      } finally {
        await iterator.return?.(undefined);
      }

      if (yielded) return;
    }

    yield "[CloudFallbackChain: no configured generator could serve the request]";
  }

  dispose(): void {
    for (const g of this.generators) {
      try {
        g.dispose();
      } catch {
        /* best effort */
      }
    }
  }
}

function isReady(g: IChatGenerator): boolean {
  return !isConfigurable(g) || g.isConfigured;
}

function isFailSoftFrame(chunk: string): boolean {
  return (
    chunk.startsWith("[") &&
    (chunk.toLowerCase().includes("not configured") ||
      chunk.toLowerCase().includes("cloudfallbackchain"))
  );
}

// ─────────────────────────────────────────────────────────────────────────────
// BackupBrainOrchestrator
// ─────────────────────────────────────────────────────────────────────────────

/** Health state of one brain in the chain. Mirrors BrainHealth. */
export enum BrainHealth {
  Healthy = "Healthy",
  Degraded = "Degraded",
  CoolingDown = "CoolingDown",
}

/** Snapshot of brain health for monitoring. Mirrors BrainStatus. */
export interface BrainStatus {
  readonly label: string;
  readonly health: BrainHealth;
  readonly consecutiveFailures: number;
}

/** Policy knobs. Mirrors BackupBrainPolicy. Durations are milliseconds. */
export interface BackupBrainPolicy {
  /** Consecutive failures that push a brain to degraded. Default 2. */
  readonly degradedAfterFailures?: number;
  /** How long a degraded brain stays out before retry, ms. Default 30 000. */
  readonly coolDownDurationMs?: number;
  /** How many brains to try before giving up on one turn. Default 3. */
  readonly maxRetriesPerTurn?: number;
}

const DEFAULT_POLICY: Required<BackupBrainPolicy> = {
  degradedAfterFailures: 2,
  coolDownDurationMs: 30_000,
  maxRetriesPerTurn: 3,
};

class BrainEntry {
  consecutive = 0;
  degradedSinceMs = 0;
  isDegraded = false;
  constructor(readonly brain: IChatGenerator) {}

  healthAt(nowMs: number, coolDownMs: number): BrainHealth {
    if (!this.isDegraded) return BrainHealth.Healthy;
    if (nowMs - this.degradedSinceMs >= coolDownMs) return BrainHealth.CoolingDown;
    return BrainHealth.Degraded;
  }

  recordSuccess(): void {
    this.consecutive = 0;
    this.isDegraded = false;
  }

  recordFailure(threshold: number, nowMs: number): void {
    this.consecutive++;
    if (this.consecutive >= threshold) {
      this.isDegraded = true;
      this.degradedSinceMs = nowMs;
    }
  }
}

/**
 * Wraps an ordered set of brains; switches on failure, retries the primary on
 * cool-down. Mirrors CircleAI.Hosting.CloudFallback.BackupBrainOrchestrator.
 */
export class BackupBrainOrchestrator implements IChatGenerator {
  private readonly brains: BrainEntry[];
  private readonly policy: Required<BackupBrainPolicy>;
  private readonly clock: () => number;

  constructor(
    brains: Iterable<IChatGenerator>,
    policy: BackupBrainPolicy = {},
    clock?: () => number,
  ) {
    if (!brains) throw new Error("brains required");
    this.brains = [...brains].map((b) => new BrainEntry(b));
    if (this.brains.length === 0) throw new Error("At least one brain is required.");
    this.policy = { ...DEFAULT_POLICY, ...policy };
    this.clock = clock ?? (() => Date.now());
  }

  get statuses(): readonly BrainStatus[] {
    const now = this.clock();
    return this.brains.map((e) => {
      const h = e.healthAt(now, this.policy.coolDownDurationMs);
      const label = isConfigurable(e.brain)
        ? e.brain.engineLabel
        : e.brain.constructor.name;
      return { label, health: h, consecutiveFailures: e.consecutive };
    });
  }

  async generateAsync(
    messages: readonly ChatMessage[],
    options?: GenerationOptions,
  ): Promise<string> {
    const maxRetries = Math.min(this.policy.maxRetriesPerTurn, this.brains.length);
    const tried = new Set<BrainEntry>();
    for (let attempt = 0; attempt < maxRetries; attempt++) {
      const pick = this.pickAvailable(tried);
      if (pick === null) break;
      tried.add(pick);
      try {
        const result = await pick.brain.generateAsync(messages, options);
        pick.recordSuccess();
        return result;
      } catch {
        pick.recordFailure(this.policy.degradedAfterFailures, this.clock());
      }
    }
    return "[All brains failed.]";
  }

  async *streamAsync(
    messages: readonly ChatMessage[],
    options?: GenerationOptions,
  ): AsyncGenerator<string> {
    const maxRetries = Math.min(this.policy.maxRetriesPerTurn, this.brains.length);
    const tried = new Set<BrainEntry>();
    for (let attempt = 0; attempt < maxRetries; attempt++) {
      const pick = this.pickAvailable(tried);
      if (pick === null) break;
      tried.add(pick);
      let streamedAny = false;
      let failed = false;

      for await (const chunk of iterateStreamSafe(pick, messages, options)) {
        if (chunk === null) {
          failed = true;
          break;
        }
        streamedAny = true;
        yield chunk;
      }

      if (failed) {
        pick.recordFailure(this.policy.degradedAfterFailures, this.clock());
        if (!streamedAny) continue; // try the backup
      }
      if (streamedAny) {
        pick.recordSuccess();
        return;
      }
    }
    yield "[All brains failed.]";
  }

  dispose(): void {
    /* nothing owned */
  }

  private pickAvailable(skip: Set<BrainEntry>): BrainEntry | null {
    const now = this.clock();
    for (const e of this.brains) {
      if (skip.has(e)) continue;
      const h = e.healthAt(now, this.policy.coolDownDurationMs);
      if (h === BrainHealth.Healthy || h === BrainHealth.CoolingDown) return e;
    }
    // None healthy — pick first untried brain anyway (degraded might recover).
    for (const e of this.brains) {
      if (!skip.has(e)) return e;
    }
    return null;
  }
}

/** Mirrors BackupBrainOrchestrator.IterateStreamSafe — null sentinel on fault. */
async function* iterateStreamSafe(
  pick: BrainEntry,
  messages: readonly ChatMessage[],
  options: GenerationOptions | undefined,
): AsyncGenerator<string | null> {
  let iterator: AsyncIterator<string> | null = null;
  try {
    iterator = pick.brain.streamAsync(messages, options)[Symbol.asyncIterator]();
  } catch {
    yield null;
    return;
  }

  try {
    for (;;) {
      let res: IteratorResult<string>;
      try {
        res = await iterator.next();
      } catch {
        yield null;
        return;
      }
      if (res.done) return;
      yield res.value;
    }
  } finally {
    await iterator.return?.(undefined);
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// ServerSentEventsReader
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Yields the payload of every `data:` frame from a streaming HTTP response;
 * a `[DONE]` sentinel terminates cleanly. Mirrors ServerSentEventsReader.
 */
export async function* readSseFrames(resp: HttpResponse): AsyncGenerator<string> {
  for await (const line of sseLines(resp)) {
    if (!line.startsWith("data:")) continue;
    const payload = line.slice(5).replace(/^\s+/, "");
    if (payload === "[DONE]") return;
    yield payload;
  }
}

async function* sseLines(resp: HttpResponse): AsyncGenerator<string> {
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

// ─────────────────────────────────────────────────────────────────────────────
// Provider option shapes (Options.cs)
// ─────────────────────────────────────────────────────────────────────────────

export interface OpenAiChatOptions {
  readonly baseAddress?: string;
  readonly apiKey?: string | null;
  readonly model?: string;
  readonly temperature?: number;
  readonly maxTokens?: number;
}
export interface AnthropicChatOptions {
  readonly baseAddress?: string;
  readonly apiKey?: string | null;
  readonly model?: string;
  readonly temperature?: number;
  readonly maxTokens?: number;
  readonly anthropicVersion?: string;
}
export interface GeminiChatOptions {
  readonly baseAddress?: string;
  readonly apiKey?: string | null;
  readonly model?: string;
  readonly temperature?: number;
  readonly maxOutputTokens?: number;
}
export interface OpenAiCompatibleOptions {
  readonly baseAddress?: string;
  readonly apiKey?: string | null;
  readonly model?: string;
  readonly temperature?: number;
  readonly maxTokens?: number;
}

// ─────────────────────────────────────────────────────────────────────────────
// OpenAI-compatible base (Groq / Cerebras / Together / DeepSeek / OpenAI)
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Shared OpenAI-compatible streaming chat generator. Sends via the injected
 * {@link IHttpTransport}. Mirrors OpenAiCompatibleChatGeneratorBase +
 * OpenAiChatGenerator.
 */
export class OpenAiCompatibleChatGenerator implements IConfigurableChatGenerator {
  protected readonly transport: IHttpTransport;
  protected readonly options: Required<Omit<OpenAiCompatibleOptions, "apiKey">> & {
    apiKey: string | null;
  };
  private readonly id: string;
  private readonly chatCompletionsPath: string;

  constructor(
    transport: IHttpTransport,
    options: OpenAiCompatibleOptions,
    id = "openai",
    chatCompletionsPath = "/v1/chat/completions",
  ) {
    if (!transport) throw new Error("transport required");
    if (!options) throw new Error("options required");
    this.transport = transport;
    this.options = {
      baseAddress: options.baseAddress ?? "https://api.openai.com",
      apiKey: options.apiKey ?? null,
      model: options.model ?? "gpt-4o-mini",
      temperature: options.temperature ?? 0.7,
      maxTokens: options.maxTokens ?? 1024,
    };
    this.id = id;
    this.chatCompletionsPath = chatCompletionsPath;
  }

  get engineLabel(): string {
    return `${cap(this.id)} · ${this.options.model}`;
  }
  get isConfigured(): boolean {
    return this.options.apiKey != null && this.options.apiKey.trim().length > 0;
  }
  get statusMessage(): string {
    return this.isConfigured
      ? `Ready · ${this.options.model}`
      : `${this.id} API key not configured.`;
  }

  async generateAsync(
    messages: readonly ChatMessage[],
    options?: GenerationOptions,
  ): Promise<string> {
    let out = "";
    for await (const chunk of this.streamAsync(messages, options)) out += chunk;
    return out;
  }

  async *streamAsync(
    messages: readonly ChatMessage[],
    options?: GenerationOptions,
  ): AsyncGenerator<string> {
    if (!this.isConfigured) {
      yield `[${this.statusMessage}]`;
      return;
    }

    const body = {
      model: this.options.model,
      stream: true,
      temperature: options?.temperature ?? this.options.temperature,
      max_tokens: options?.maxTokens ?? this.options.maxTokens,
      messages: messages.map((m) => ({ role: m.role, content: m.content })),
    };

    const resp = await this.transport.sendAsync(
      "POST",
      this.chatCompletionsPath,
      { Authorization: `Bearer ${this.options.apiKey}`, "Content-Type": "application/json" },
      JSON.stringify(body),
    );

    if (resp.status < 200 || resp.status >= 300) {
      yield `[${this.id} error ${resp.status}: ${truncate(resp.body ?? "", 240)}]`;
      return;
    }

    for await (const frame of readSseFrames(resp)) {
      const delta = parseOpenAiDelta(frame);
      if (delta != null && delta.length > 0) yield delta;
    }
  }

  dispose(): void {
    /* transport is externally owned */
  }
}

/** OpenAI (official endpoint / compatible gateways). Mirrors OpenAiChatGenerator. */
export class OpenAiChatGenerator extends OpenAiCompatibleChatGenerator {
  constructor(transport: IHttpTransport, options: OpenAiChatOptions) {
    super(transport, options, "openai", "/v1/chat/completions");
  }
  override get engineLabel(): string {
    return `OpenAI · ${this.options.model}`;
  }
}

/** Groq — /openai/v1/chat/completions. */
export class GroqChatGenerator extends OpenAiCompatibleChatGenerator {
  constructor(transport: IHttpTransport, options: OpenAiCompatibleOptions) {
    super(
      transport,
      { model: "llama-3.3-70b-versatile", baseAddress: "https://api.groq.com", ...options },
      "groq",
      "/openai/v1/chat/completions",
    );
  }
  override get engineLabel(): string {
    return `Groq · ${this.options.model}`;
  }
}

/** Cerebras — /v1/chat/completions. */
export class CerebrasChatGenerator extends OpenAiCompatibleChatGenerator {
  constructor(transport: IHttpTransport, options: OpenAiCompatibleOptions) {
    super(
      transport,
      { model: "llama3.3-70b", baseAddress: "https://api.cerebras.ai", ...options },
      "cerebras",
    );
  }
  override get engineLabel(): string {
    return `Cerebras · ${this.options.model}`;
  }
}

/** Together AI — /v1/chat/completions. */
export class TogetherChatGenerator extends OpenAiCompatibleChatGenerator {
  constructor(transport: IHttpTransport, options: OpenAiCompatibleOptions) {
    super(
      transport,
      {
        model: "meta-llama/Llama-3.3-70B-Instruct-Turbo",
        baseAddress: "https://api.together.xyz",
        ...options,
      },
      "together",
    );
  }
  override get engineLabel(): string {
    return `Together · ${this.options.model}`;
  }
}

/** DeepSeek — /v1/chat/completions. */
export class DeepSeekChatGenerator extends OpenAiCompatibleChatGenerator {
  constructor(transport: IHttpTransport, options: OpenAiCompatibleOptions) {
    super(
      transport,
      { model: "deepseek-chat", baseAddress: "https://api.deepseek.com", ...options },
      "deepseek",
    );
  }
  override get engineLabel(): string {
    return `DeepSeek · ${this.options.model}`;
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Anthropic
// ─────────────────────────────────────────────────────────────────────────────

/** Anthropic Messages API generator. Mirrors AnthropicChatGenerator. */
export class AnthropicChatGenerator implements IConfigurableChatGenerator {
  private readonly transport: IHttpTransport;
  private readonly options: Required<Omit<AnthropicChatOptions, "apiKey">> & {
    apiKey: string | null;
  };

  constructor(transport: IHttpTransport, options: AnthropicChatOptions) {
    if (!transport) throw new Error("transport required");
    if (!options) throw new Error("options required");
    this.transport = transport;
    this.options = {
      baseAddress: options.baseAddress ?? "https://api.anthropic.com",
      apiKey: options.apiKey ?? null,
      model: options.model ?? "claude-3-5-sonnet-latest",
      temperature: options.temperature ?? 0.7,
      maxTokens: options.maxTokens ?? 1024,
      anthropicVersion: options.anthropicVersion ?? "2023-06-01",
    };
  }

  get engineLabel(): string {
    return `Anthropic · ${this.options.model}`;
  }
  get isConfigured(): boolean {
    return this.options.apiKey != null && this.options.apiKey.trim().length > 0;
  }
  get statusMessage(): string {
    return this.isConfigured
      ? `Ready · ${this.options.model}`
      : "Anthropic API key not configured.";
  }

  async generateAsync(
    messages: readonly ChatMessage[],
    options?: GenerationOptions,
  ): Promise<string> {
    let out = "";
    for await (const chunk of this.streamAsync(messages, options)) out += chunk;
    return out;
  }

  async *streamAsync(
    messages: readonly ChatMessage[],
    options?: GenerationOptions,
  ): AsyncGenerator<string> {
    if (!this.isConfigured) {
      yield `[${this.statusMessage}]`;
      return;
    }

    const system = messages
      .filter((m) => m.role.toLowerCase() === "system")
      .map((m) => m.content)
      .join("\n\n");
    const chat = messages
      .filter((m) => m.role.toLowerCase() !== "system")
      .map((m) => ({ role: m.role.toLowerCase(), content: m.content }));

    const base = {
      model: this.options.model,
      max_tokens: options?.maxTokens ?? this.options.maxTokens,
      temperature: options?.temperature ?? this.options.temperature,
      stream: true,
      messages: chat,
    };
    const body = system.length === 0 ? base : { ...base, system };

    const resp = await this.transport.sendAsync(
      "POST",
      "/v1/messages",
      {
        "x-api-key": this.options.apiKey!,
        "anthropic-version": this.options.anthropicVersion,
        "Content-Type": "application/json",
      },
      JSON.stringify(body),
    );

    if (resp.status < 200 || resp.status >= 300) {
      yield `[Anthropic error ${resp.status}: ${truncate(resp.body ?? "", 240)}]`;
      return;
    }

    for await (const frame of readSseFrames(resp)) {
      const delta = parseAnthropicDelta(frame);
      if (delta != null && delta.length > 0) yield delta;
    }
  }

  dispose(): void {
    /* transport is externally owned */
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Gemini
// ─────────────────────────────────────────────────────────────────────────────

/** Google Gemini streamGenerateContent generator. Mirrors GeminiChatGenerator. */
export class GeminiChatGenerator implements IConfigurableChatGenerator {
  private readonly transport: IHttpTransport;
  private readonly options: Required<Omit<GeminiChatOptions, "apiKey">> & {
    apiKey: string | null;
  };

  constructor(transport: IHttpTransport, options: GeminiChatOptions) {
    if (!transport) throw new Error("transport required");
    if (!options) throw new Error("options required");
    this.transport = transport;
    this.options = {
      baseAddress: options.baseAddress ?? "https://generativelanguage.googleapis.com",
      apiKey: options.apiKey ?? null,
      model: options.model ?? "gemini-2.0-flash",
      temperature: options.temperature ?? 0.7,
      maxOutputTokens: options.maxOutputTokens ?? 1024,
    };
  }

  get engineLabel(): string {
    return `Gemini · ${this.options.model}`;
  }
  get isConfigured(): boolean {
    return this.options.apiKey != null && this.options.apiKey.trim().length > 0;
  }
  get statusMessage(): string {
    return this.isConfigured
      ? `Ready · ${this.options.model}`
      : "Gemini API key not configured.";
  }

  async generateAsync(
    messages: readonly ChatMessage[],
    options?: GenerationOptions,
  ): Promise<string> {
    let out = "";
    for await (const chunk of this.streamAsync(messages, options)) out += chunk;
    return out;
  }

  async *streamAsync(
    messages: readonly ChatMessage[],
    options?: GenerationOptions,
  ): AsyncGenerator<string> {
    if (!this.isConfigured) {
      yield `[${this.statusMessage}]`;
      return;
    }

    const system = messages
      .filter((m) => m.role.toLowerCase() === "system")
      .map((m) => m.content)
      .join("\n\n");
    const contents = messages
      .filter((m) => m.role.toLowerCase() !== "system")
      .map((m) => ({
        role: m.role.toLowerCase() === "assistant" ? "model" : m.role.toLowerCase(),
        parts: [{ text: m.content }],
      }));

    const generationConfig = {
      temperature: options?.temperature ?? this.options.temperature,
      maxOutputTokens: options?.maxTokens ?? this.options.maxOutputTokens,
    };
    const body =
      system.length === 0
        ? { contents, generationConfig }
        : {
            contents,
            systemInstruction: { parts: [{ text: system }] },
            generationConfig,
          };

    const path = `/v1beta/models/${encodeURIComponent(this.options.model)}:streamGenerateContent?alt=sse&key=${encodeURIComponent(this.options.apiKey!)}`;
    const resp = await this.transport.sendAsync(
      "POST",
      path,
      { "Content-Type": "application/json" },
      JSON.stringify(body),
    );

    if (resp.status < 200 || resp.status >= 300) {
      yield `[Gemini error ${resp.status}: ${truncate(resp.body ?? "", 240)}]`;
      return;
    }

    for await (const frame of readSseFrames(resp)) {
      const delta = parseGeminiDelta(frame);
      if (delta != null && delta.length > 0) yield delta;
    }
  }

  dispose(): void {
    /* transport is externally owned */
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// FakeConfigurableChatGenerator — deterministic local fake for tests
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Deterministic {@link IConfigurableChatGenerator} for tests. When configured,
 * streams a fixed reply word-by-word; when not, yields the fail-soft frame so
 * a {@link CloudFallbackChain} skips it. Optionally throws to exercise the
 * {@link BackupBrainOrchestrator} failover paths.
 */
export class FakeConfigurableChatGenerator implements IConfigurableChatGenerator {
  private readonly reply: string;
  private readonly configured: boolean;
  private readonly throwOnCall: boolean;
  readonly engineLabel: string;

  constructor(opts: {
    reply?: string;
    configured?: boolean;
    engineLabel?: string;
    throwOnCall?: boolean;
  } = {}) {
    this.reply = opts.reply ?? "hello world";
    this.configured = opts.configured ?? true;
    this.throwOnCall = opts.throwOnCall ?? false;
    this.engineLabel = opts.engineLabel ?? "Fake · deterministic";
  }

  get isConfigured(): boolean {
    return this.configured;
  }
  get statusMessage(): string {
    return this.configured ? "Ready · fake" : "fake API key not configured.";
  }

  async generateAsync(): Promise<string> {
    if (this.throwOnCall) throw new Error("fake failure");
    if (!this.configured) return `[${this.statusMessage}]`;
    return this.reply;
  }

  async *streamAsync(): AsyncGenerator<string> {
    if (this.throwOnCall) throw new Error("fake failure");
    if (!this.configured) {
      yield `[${this.statusMessage}]`;
      return;
    }
    const words = this.reply.split(" ");
    for (let i = 0; i < words.length; i++) yield i === 0 ? words[i] : ` ${words[i]}`;
  }

  dispose(): void {
    /* nothing owned */
  }
}

// ── delta parsers ──────────────────────────────────────────────────────────────

function parseOpenAiDelta(frame: string): string | null {
  try {
    const doc = JSON.parse(frame) as {
      choices?: { delta?: { content?: unknown } }[];
    };
    const c = doc.choices;
    if (Array.isArray(c) && c.length > 0) {
      const content = c[0].delta?.content;
      if (typeof content === "string") return content;
    }
  } catch {
    return null;
  }
  return null;
}

function parseAnthropicDelta(frame: string): string | null {
  try {
    const doc = JSON.parse(frame) as {
      type?: string;
      delta?: { text?: unknown };
    };
    if (doc.type === "content_block_delta" && typeof doc.delta?.text === "string")
      return doc.delta.text;
  } catch {
    return null;
  }
  return null;
}

function parseGeminiDelta(frame: string): string | null {
  try {
    const doc = JSON.parse(frame) as {
      candidates?: { content?: { parts?: { text?: unknown }[] } }[];
    };
    const cand = doc.candidates;
    if (Array.isArray(cand) && cand.length > 0) {
      const parts = cand[0].content?.parts;
      if (Array.isArray(parts) && parts.length > 0 && typeof parts[0].text === "string")
        return parts[0].text;
    }
  } catch {
    return null;
  }
  return null;
}

function truncate(value: string, max: number): string {
  return value.length <= max ? value : value.slice(0, max) + "…";
}

function cap(s: string): string {
  return s.length === 0 ? s : s[0].toUpperCase() + s.slice(1);
}
