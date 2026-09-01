// The inference server, the cloud chat providers, and the web surface.
//
// THIS SERVER BINDS TO LOOPBACK. It exists so a program on the same device can
// use the model already loaded on it, not so a device becomes a service on a
// network. Binding to 0.0.0.0 turns a phone into an open inference endpoint on
// whatever Wi-Fi it joins, so the default refuses and a wider bind must be
// asked for by name AND carry a key.
//
// THE HANDLERS ARE PURE. Each takes a parsed request and returns a status and a
// body; nothing here touches a socket. That is what makes the auth logic
// testable without a server, and the auth logic is the part worth testing.
//
// THE DTOs ARE SEPARATE FROM THE DOMAIN TYPES ON PURPOSE. A response shape is a
// promise to whoever is calling; a domain type changes when the code changes.
// Serialising domain types directly is how an internal rename becomes somebody
// else's broken client.

// ─────────────────────────────────────────────────────────────────────────────
// Auth

/**
 * How the server authenticates callers.
 *
 * NO DEFAULT KEY. A default key is a published key: it reaches a README, then a
 * search engine, and every device that never changed it is open.
 */
export interface ApiKeyAuthSchemeOptions {
  readonly headerName: string;
  /** Hashes, never the keys. A process that holds the plaintext will leak it
   * from a core dump, a log or a debugger, and the server never needs it. */
  readonly keyHashes: readonly string[];
  /** When false, every request is allowed. Only safe on loopback, and the
   * builder refuses to combine it with a wider bind. */
  readonly required: boolean;
  readonly allowLoopbackWithoutKey: boolean;
}

export const apiKeyAuthSchemeOptions = (
  partial: Partial<ApiKeyAuthSchemeOptions> = {},
): ApiKeyAuthSchemeOptions =>
  Object.freeze({
    headerName: partial.headerName ?? "X-CircleAI-Key",
    keyHashes: Object.freeze([...(partial.keyHashes ?? [])]),
    required: partial.required ?? true,
    allowLoopbackWithoutKey: partial.allowLoopbackWithoutKey ?? true,
  });

/** Compares two strings without revealing where they differ. */
function constantTimeEquals(a: string, b: string): boolean {
  if (a.length !== b.length) return false;
  let difference = 0;
  for (let i = 0; i < a.length; i++) difference |= a.charCodeAt(i) ^ b.charCodeAt(i);
  return difference === 0;
}

/** Checks a key against the configured hashes. */
export class ApiKeyAuthHandler {
  constructor(
    readonly options: ApiKeyAuthSchemeOptions = apiKeyAuthSchemeOptions(),
    private readonly hashKey: (key: string) => string = (k) => k,
  ) {}

  /**
   * Headers are matched CASE-INSENSITIVELY, because HTTP header names are
   * case-insensitive and a client that sends `x-circleai-key` is correct.
   * Rejecting it produces a 401 that nobody can explain.
   */
  authenticate(
    headers: Readonly<Record<string, string>>,
    isLoopback = false,
  ): { allowed: boolean; reason: string } {
    if (!this.options.required) return { allowed: true, reason: "this server does not require a key" };
    if (isLoopback && this.options.allowLoopbackWithoutKey) {
      return { allowed: true, reason: "loopback, where the caller is already on this device" };
    }
    if (this.options.keyHashes.length === 0) {
      // No keys and a required scheme means DENY. Falling open here would make
      // a misconfiguration into an open server.
      return { allowed: false, reason: "this server requires a key and none is configured" };
    }
    const lowered = new Map(Object.entries(headers).map(([k, v]) => [k.toLowerCase(), v]));
    const supplied = lowered.get(this.options.headerName.toLowerCase()) ?? "";
    if (!supplied) return { allowed: false, reason: "no key was supplied" };
    const candidate = this.hashKey(supplied);
    // Compared against EVERY hash without an early exit, so the time taken does
    // not say how many keys are configured or which one nearly matched.
    let matched = false;
    for (const known of this.options.keyHashes) {
      matched = constantTimeEquals(candidate, known) || matched;
    }
    // Says "not accepted", not "wrong key" - the second confirms to somebody
    // guessing that the header name and format were right.
    return matched
      ? { allowed: true, reason: "key accepted" }
      : { allowed: false, reason: "the key was not accepted" };
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Response shapes

/** What the host is, as told to a caller. */
export interface HostProfileDto {
  readonly platform: string;
  /** Never the device NAME. A phone's name is usually a person's name, and it
   * has no business in a diagnostics response. */
  readonly deviceClass: string;
  readonly cpuCount: number;
  readonly ramGb: number;
  /** Whether that RAM figure may be trusted for sizing. Carried through because
   * a caller choosing a model needs to know it is a real measurement and not a
   * heap reading. */
  readonly ramIsMeasured: boolean;
}

/** Where the native runtime came from. */
export interface NativeRuntimePathsDto {
  readonly abi: string;
  /** The base name only. The full path leaks the install layout and, on a
   * desktop, usually a person's home directory. */
  readonly library: string;
  readonly isLoaded: boolean;
}

/** Which backend was chosen and why. */
export interface BackendSelectionDto {
  readonly backend: string;
  readonly reason: string;
  readonly fellBack: boolean;
}

/** A model the server currently holds. */
export interface LoadedModelInfo {
  readonly modelId: string;
  readonly modality: string;
  readonly parametersBillion: number;
  readonly quantisation: string;
  readonly contextLength: number;
  readonly loadedSecondsAgo: number;
}

/** One counter. */
export interface CounterSnapshot {
  readonly name: string;
  readonly value: number;
}

/**
 * The cheap check.
 *
 * DELIBERATELY THIN and free of anything identifying. A health endpoint is the
 * one thing polled by anything on the network, so it says only whether the
 * server can answer.
 */
export interface HealthResponse {
  readonly ok: boolean;
  readonly ready: boolean;
  readonly uptimeSeconds: number;
}

export const healthResponse = (partial: Partial<HealthResponse> = {}): HealthResponse =>
  Object.freeze({
    ok: partial.ok ?? true,
    ready: partial.ready ?? false,
    uptimeSeconds: Math.round((partial.uptimeSeconds ?? 0) * 10) / 10,
  });

/** The full picture, for somebody debugging on the device. */
export interface DiagnosticsResponse {
  readonly host: HostProfileDto;
  readonly native: NativeRuntimePathsDto;
  readonly backend: BackendSelectionDto;
  readonly models: readonly LoadedModelInfo[];
  readonly counters: readonly CounterSnapshot[];
  readonly p95Ms: Readonly<Record<string, number>>;
}

export const diagnosticsResponse = (
  partial: Partial<DiagnosticsResponse> = {},
): DiagnosticsResponse =>
  Object.freeze({
    host: partial.host ?? {
      platform: "", deviceClass: "", cpuCount: 0, ramGb: 0, ramIsMeasured: false,
    },
    native: partial.native ?? { abi: "", library: "", isLoaded: false },
    backend: partial.backend ?? { backend: "", reason: "", fellBack: false },
    models: Object.freeze([...(partial.models ?? [])]),
    counters: Object.freeze([...(partial.counters ?? [])]),
    p95Ms: Object.freeze({ ...(partial.p95Ms ?? {}) }),
  });

// ─────────────────────────────────────────────────────────────────────────────
// Endpoints

/** What a handler returns. */
export interface EndpointResponse {
  readonly status: number;
  readonly body: Record<string, unknown>;
}

const endpointResponse = (status: number, body: Record<string, unknown>): EndpointResponse =>
  Object.freeze({ status, body });

export const endpointOk = (r: EndpointResponse): boolean => r.status >= 200 && r.status < 300;

/** One route. */
export interface Endpoint {
  readonly path: string;
  handle(request: Record<string, unknown>): Promise<EndpointResponse>;
}

/**
 * OpenAI-shaped chat completions, served locally.
 *
 * THE SHAPE IS COPIED ON PURPOSE. Anything already written against that API
 * works against this server by changing a base URL, and that is the whole
 * reason a local server exists rather than a bespoke protocol.
 */
export class ChatCompletionsEndpoint implements Endpoint {
  readonly path = "/v1/chat/completions";

  constructor(
    private readonly generate?: (
      turns: readonly { role: string; content: string }[],
      request: Record<string, unknown>,
    ) => Promise<string>,
  ) {}

  async handle(request: Record<string, unknown>): Promise<EndpointResponse> {
    const messages = request.messages;
    if (!Array.isArray(messages) || messages.length === 0) {
      // 400 with a reason, not a 500. A malformed request is the caller's to
      // fix, and naming the field is the difference between a usable API and a
      // guessing game.
      return endpointResponse(400, {
        error: {
          message: "messages is required and must be a non-empty list",
          type: "invalid_request_error",
          param: "messages",
        },
      });
    }
    if (!this.generate) {
      return endpointResponse(503, {
        error: { message: "no model is loaded on this device", type: "service_unavailable" },
      });
    }
    const turns = messages
      .filter((m): m is Record<string, unknown> => Boolean(m) && typeof m === "object")
      .map((m) => ({ role: String(m.role ?? "user"), content: String(m.content ?? "") }));
    try {
      const text = await this.generate(turns, request);
      return endpointResponse(200, {
        object: "chat.completion",
        model: String(request.model ?? ""),
        choices: [
          {
            index: 0,
            message: { role: "assistant", content: text },
            // Always populated. A client that switches on it gets undefined
            // from a server that omits it, and treats a finished reply as a
            // truncated one.
            finish_reason: "stop",
          },
        ],
      });
    } catch (error) {
      return endpointResponse(500, {
        error: {
          message: error instanceof Error ? error.message : String(error),
          type: "inference_error",
        },
      });
    }
  }
}

/** Embeddings, OpenAI-shaped. */
export class EmbeddingsEndpoint implements Endpoint {
  readonly path = "/v1/embeddings";

  constructor(private readonly embed?: (inputs: readonly string[]) => Promise<number[][]>) {}

  async handle(request: Record<string, unknown>): Promise<EndpointResponse> {
    // A single string and a list of strings are BOTH valid input in this API.
    // Accepting only the list rejects the commonest call.
    const raw = request.input;
    const inputs = typeof raw === "string" ? [raw] : raw;
    if (!Array.isArray(inputs) || inputs.length === 0) {
      return endpointResponse(400, {
        error: {
          message: "input is required, as a string or a list of strings",
          type: "invalid_request_error",
          param: "input",
        },
      });
    }
    if (!this.embed) {
      return endpointResponse(503, {
        error: {
          message: "no embedding model is loaded on this device",
          type: "service_unavailable",
        },
      });
    }
    const vectors = await this.embed(inputs.map(String));
    return endpointResponse(200, {
      object: "list",
      data: vectors.map((embedding, index) => ({ object: "embedding", index, embedding })),
    });
  }
}

/**
 * The companion's own surface.
 *
 * Separate from chat completions because it carries state a stateless
 * completion cannot: which conversation, which memories are in scope, and what
 * the companion is allowed to do on this turn.
 */
export class CompanionEndpoint implements Endpoint {
  readonly path = "/v1/companion";

  constructor(private readonly respond?: (sessionId: string, text: string) => Promise<string>) {}

  async handle(request: Record<string, unknown>): Promise<EndpointResponse> {
    const text = String(request.text ?? "").trim();
    if (!text) {
      return endpointResponse(400, {
        error: { message: "text is required", type: "invalid_request_error" },
      });
    }
    if (!this.respond) {
      return endpointResponse(503, {
        error: {
          message: "the companion is not available on this device",
          type: "service_unavailable",
        },
      });
    }
    const sessionId = String(request.session_id ?? "");
    return endpointResponse(200, {
      text: await this.respond(sessionId, text),
      // Echoed back so a caller that did not supply one learns the id the
      // server used, rather than starting a new conversation every turn.
      session_id: sessionId,
    });
  }
}

/**
 * Health and diagnostics.
 *
 * TWO PATHS, DIFFERENT AUDIENCES. Health is polled by anything and says almost
 * nothing; diagnostics answers a person debugging on the device and is treated
 * as privileged.
 */
export class DiagnosticsEndpoint implements Endpoint {
  readonly path = "/health";

  constructor(
    private readonly health?: () => HealthResponse,
    private readonly diagnostics?: () => DiagnosticsResponse,
  ) {}

  async handle(request: Record<string, unknown>): Promise<EndpointResponse> {
    const wantsFull = String(request.path ?? "").replace(/\/+$/, "").endsWith("diagnostics");
    if (!wantsFull) {
      return endpointResponse(200, { ...(this.health?.() ?? healthResponse()) });
    }
    const full = this.diagnostics?.() ?? diagnosticsResponse();
    return endpointResponse(200, {
      host: { ...full.host },
      native: { ...full.native },
      backend: { ...full.backend },
      models: full.models.map((m) => ({ ...m })),
      counters: Object.fromEntries(full.counters.map((c) => [c.name, c.value])),
      p95_ms: { ...full.p95Ms },
    });
  }
}

/**
 * Loading and unloading models.
 *
 * ALWAYS REQUIRES A KEY, even on loopback where the other endpoints do not.
 * Everything else here answers questions; this one changes what the device is
 * doing and can make it fetch several gigabytes.
 */
export class AdminEndpoints implements Endpoint {
  readonly path = "/admin/models";
  readonly requiresKeyAlways = true;

  constructor(
    private readonly load?: (modelId: string) => Promise<boolean>,
    private readonly unload?: (modelId: string) => Promise<boolean>,
    private readonly gate?: (modelId: string) => { allowed: boolean; reason: string },
  ) {}

  async handle(request: Record<string, unknown>): Promise<EndpointResponse> {
    const action = String(request.action ?? "").toLowerCase();
    const modelId = String(request.model_id ?? "").trim();
    if (!modelId) {
      return endpointResponse(400, {
        error: { message: "model_id is required", type: "invalid_request_error" },
      });
    }
    if (action === "load") {
      const verdict = this.gate?.(modelId);
      if (verdict && !verdict.allowed) {
        // 409, not 403. The request was legitimate and the device declined for
        // a reason the caller can act on - a conflict with the device's state,
        // not a refusal of authority.
        return endpointResponse(409, {
          error: { message: verdict.reason, type: "download_blocked" },
        });
      }
      if (!this.load) {
        return endpointResponse(503, {
          error: { message: "this server cannot load models", type: "service_unavailable" },
        });
      }
      return endpointResponse(200, { loaded: await this.load(modelId), model_id: modelId });
    }
    if (action === "unload") {
      if (!this.unload) {
        return endpointResponse(503, {
          error: { message: "this server cannot unload models", type: "service_unavailable" },
        });
      }
      return endpointResponse(200, { unloaded: await this.unload(modelId), model_id: modelId });
    }
    return endpointResponse(400, {
      error: { message: "action must be load or unload", type: "invalid_request_error", param: "action" },
    });
  }
}

/**
 * Builds the bridge to the native runtime, once.
 *
 * CACHED, because building it twice loads the model twice and a phone does not
 * have room for two. The cache is keyed on the model id, so switching models
 * releases the old bridge rather than accumulating them.
 */
export class MnnInferenceBridgeFactory {
  private modelId = "";
  private bridge?: unknown;

  constructor(private readonly build?: (modelId: string) => unknown) {}

  get currentModelId(): string {
    return this.modelId;
  }

  get(modelId: string): unknown {
    if (!modelId || !this.build) return undefined;
    if (this.bridge !== undefined && this.modelId === modelId) return this.bridge;
    // The old bridge is dropped BEFORE the new one is built. Holding both for
    // the length of a load needs twice the memory, at the one moment the device
    // has least of it.
    this.bridge = undefined;
    this.modelId = "";
    const built = this.build(modelId);
    this.bridge = built;
    this.modelId = modelId;
    return built;
  }

  release(): void {
    this.bridge = undefined;
    this.modelId = "";
  }
}

/** How the server is exposed. */
export interface InferenceServerOptions {
  /** LOOPBACK. A phone that binds 0.0.0.0 becomes an open inference endpoint on
   * whatever Wi-Fi it joins. */
  readonly host: string;
  readonly port: number;
  readonly auth: ApiKeyAuthSchemeOptions;
  readonly maxConcurrentRequests: number;
}

export const inferenceServerOptions = (
  partial: Partial<InferenceServerOptions> = {},
): InferenceServerOptions =>
  Object.freeze({
    host: partial.host ?? "127.0.0.1",
    port: partial.port ?? 8317,
    auth: partial.auth ?? apiKeyAuthSchemeOptions(),
    maxConcurrentRequests: partial.maxConcurrentRequests ?? 2,
  });

export const isLoopbackOnly = (o: InferenceServerOptions): boolean =>
  ["127.0.0.1", "::1", "localhost"].includes(o.host);

/** Assembles the server, refusing combinations that would open it up. */
export class InferenceServerBuilder {
  private readonly endpoints: Endpoint[] = [];

  constructor(readonly options: InferenceServerOptions = inferenceServerOptions()) {}

  add(endpoint: Endpoint): InferenceServerBuilder {
    this.endpoints.push(endpoint);
    return this;
  }

  /**
   * The one rule worth enforcing at build time.
   *
   * A wider bind with no key is an open inference endpoint on somebody's cafe
   * Wi-Fi. REFUSED here rather than warned about, because a warning at startup
   * is a line of log nobody reads.
   */
  validate(): { ok: boolean; reason: string } {
    const { auth } = this.options;
    if (!isLoopbackOnly(this.options) && (!auth.required || auth.keyHashes.length === 0)) {
      return {
        ok: false,
        reason: `binding to ${this.options.host} without a key would put this device's model on the network - configure a key or bind to 127.0.0.1`,
      };
    }
    if (this.options.maxConcurrentRequests < 1) {
      return { ok: false, reason: "at least one request must be allowed at a time" };
    }
    return { ok: true, reason: isLoopbackOnly(this.options) ? "loopback only" : "keyed" };
  }

  build(hashKey?: (key: string) => string): InferenceServer {
    const verdict = this.validate();
    if (!verdict.ok) throw new Error(verdict.reason);
    return new InferenceServer(this.options, this.endpoints, hashKey);
  }
}

/**
 * Routes a parsed request to an endpoint.
 *
 * Pure: no socket, no framework. A host binds whatever it likes and calls
 * `dispatch`, which means the auth and routing rules are testable exactly as
 * they will run.
 */
export class InferenceServer {
  private readonly byPath: Map<string, Endpoint>;
  private readonly auth: ApiKeyAuthHandler;
  private readonly startedAtMs: number;
  private inFlight = 0;

  constructor(
    private readonly options: InferenceServerOptions,
    endpoints: readonly Endpoint[],
    hashKey?: (key: string) => string,
    private readonly now: () => number = () => 0,
  ) {
    this.byPath = new Map(endpoints.map((e) => [e.path, e]));
    this.auth = new ApiKeyAuthHandler(options.auth, hashKey);
    this.startedAtMs = now();
  }

  get uptimeSeconds(): number {
    return (this.now() - this.startedAtMs) / 1000;
  }

  async dispatch(
    path: string,
    body: Record<string, unknown> = {},
    headers: Readonly<Record<string, string>> = {},
    isLoopback = true,
  ): Promise<EndpointResponse> {
    const key = path.replace(/\/+$/, "") || "/";
    let endpoint = this.byPath.get(key);
    if (!endpoint) {
      // The diagnostics endpoint owns /health and answers a sub-path, which the
      // flat table cannot express.
      for (const candidate of this.byPath.values()) {
        if (path.startsWith(candidate.path) && candidate instanceof DiagnosticsEndpoint) {
          endpoint = candidate;
          break;
        }
      }
    }
    if (!endpoint) {
      return endpointResponse(404, {
        error: { message: `no endpoint at ${path}`, type: "not_found" },
      });
    }

    // Admin overrides the loopback exemption, and is checked BEFORE the general
    // rule rather than after it.
    const loopbackOk =
      isLoopback && !(endpoint as { requiresKeyAlways?: boolean }).requiresKeyAlways;
    const decision = this.auth.authenticate(headers, loopbackOk);
    if (!decision.allowed) {
      return endpointResponse(401, { error: { message: decision.reason, type: "unauthorized" } });
    }

    if (this.inFlight >= this.options.maxConcurrentRequests) {
      // 503 with a retry hint, not a queue. Queueing inference requests on a
      // phone means the third caller waits behind two generations and times out
      // anyway, having also kept the model resident and the device hot.
      return endpointResponse(503, {
        error: {
          message: "this device is already busy generating",
          type: "busy",
          retry_after_seconds: 5,
        },
      });
    }
    this.inFlight += 1;
    try {
      return await endpoint.handle({ path, ...body });
    } finally {
      this.inFlight -= 1;
    }
  }
}

/**
 * The entry point.
 *
 * Here so the tree lines up with the C#, and so there is one obvious place that
 * shows the assembly order: options, then endpoints, then validate, then bind.
 * Validation happens BEFORE anything binds a port.
 */
export class Program {
  static build(
    options: InferenceServerOptions = inferenceServerOptions(),
    parts: {
      generate?: ConstructorParameters<typeof ChatCompletionsEndpoint>[0];
      embed?: ConstructorParameters<typeof EmbeddingsEndpoint>[0];
      respond?: ConstructorParameters<typeof CompanionEndpoint>[0];
      health?: () => HealthResponse;
      diagnostics?: () => DiagnosticsResponse;
    } = {},
  ): InferenceServer {
    return new InferenceServerBuilder(options)
      .add(new ChatCompletionsEndpoint(parts.generate))
      .add(new EmbeddingsEndpoint(parts.embed))
      .add(new CompanionEndpoint(parts.respond))
      .add(new DiagnosticsEndpoint(parts.health, parts.diagnostics))
      .add(new AdminEndpoints())
      .build();
  }

  /**
   * Returns an exit code and prints the reason on refusal.
   *
   * A refusal to start is a 2, not a 1: a caller scripting this can tell a
   * configuration it must fix from a crash it should report.
   */
  static main(argv: readonly string[] = []): number {
    let host = "127.0.0.1";
    let port = 8317;
    for (const arg of argv) {
      if (arg.startsWith("--host=")) host = arg.slice(7);
      else if (arg.startsWith("--port=")) port = Number(arg.slice(7));
    }
    const verdict = new InferenceServerBuilder(inferenceServerOptions({ host, port })).validate();
    if (!verdict.ok) {
      // eslint-disable-next-line no-console -- this is a program entry point
      console.log(verdict.reason);
      return 2;
    }
    return 0;
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Cloud chat providers

/**
 * The identifiers a person consents to, one per provider.
 *
 * STRINGS, not an enum, because a host may carry a provider this build has
 * never heard of - an OpenAI-compatible endpoint on somebody's own hardware is
 * the common case, and an enum would make that the one thing impossible.
 */
export class ProviderIds {
  static readonly OPENAI = "openai";
  static readonly ANTHROPIC = "anthropic";
  static readonly GEMINI = "gemini";
  static readonly GROQ = "groq";
  static readonly CEREBRAS = "cerebras";
  static readonly DEEPSEEK = "deepseek";
  static readonly TOGETHER = "together";

  static readonly ALL = Object.freeze([
    ProviderIds.OPENAI, ProviderIds.ANTHROPIC, ProviderIds.GEMINI, ProviderIds.GROQ,
    ProviderIds.CEREBRAS, ProviderIds.DEEPSEEK, ProviderIds.TOGETHER,
  ]);
}

/** What every cloud chat provider needs. */
export interface CloudChatOptions {
  /** OFF. A build that carries a provider does not use it, and turning it on is
   * a decision somebody makes rather than a default they inherit. */
  readonly enabled: boolean;
  readonly model: string;
  readonly baseUrl: string;
  readonly maxOutputTokens: number;
  readonly temperature: number;
  /** Redacted in toString and toJSON - a key reaches a log through
   * JSON.stringify(config) far more often than through a deliberate print. */
  readonly apiKey: { reveal(): string; isSet: boolean };
}

const chatOptions = (
  defaults: { model: string; baseUrl: string },
  partial: Partial<CloudChatOptions>,
): CloudChatOptions =>
  Object.freeze({
    enabled: partial.enabled ?? false,
    model: partial.model ?? defaults.model,
    baseUrl: partial.baseUrl ?? defaults.baseUrl,
    maxOutputTokens: partial.maxOutputTokens ?? 1024,
    temperature: partial.temperature ?? 0.7,
    apiKey: partial.apiKey ?? { reveal: () => "", isSet: false },
  });

export type GroqChatOptions = CloudChatOptions;
export const groqChatOptions = (p: Partial<CloudChatOptions> = {}): GroqChatOptions =>
  chatOptions({ model: "llama-3.3-70b-versatile", baseUrl: "https://api.groq.com/openai/v1" }, p);

export type CerebrasChatOptions = CloudChatOptions;
export const cerebrasChatOptions = (p: Partial<CloudChatOptions> = {}): CerebrasChatOptions =>
  chatOptions({ model: "llama3.1-8b", baseUrl: "https://api.cerebras.ai/v1" }, p);

export type DeepSeekChatOptions = CloudChatOptions;
export const deepSeekChatOptions = (p: Partial<CloudChatOptions> = {}): DeepSeekChatOptions =>
  chatOptions({ model: "deepseek-chat", baseUrl: "https://api.deepseek.com/v1" }, p);

export type TogetherChatOptions = CloudChatOptions;
export const togetherChatOptions = (p: Partial<CloudChatOptions> = {}): TogetherChatOptions =>
  chatOptions(
    { model: "meta-llama/Llama-3.3-70B-Instruct-Turbo", baseUrl: "https://api.together.xyz/v1" },
    p,
  );

/** What came back. */
export interface CloudChatResult {
  readonly text: string;
  /** The provider that answered. ALWAYS carried, so a caller can tell a person
   * where their words went. */
  readonly providerId: string;
  readonly inputTokens: number;
  readonly outputTokens: number;
  /** Names the PROVIDER, never the key. */
  readonly error: string;
}

/**
 * The shape five of these providers share.
 *
 * Groq, Cerebras, DeepSeek and Together all speak OpenAI's chat-completions
 * wire format. Writing them out five times would mean fixing a parsing bug five
 * times and forgetting once.
 */
export class OpenAiCompatibleChatGeneratorBase {
  constructor(
    readonly providerId: string,
    protected readonly options: CloudChatOptions,
    protected readonly post?: (
      url: string,
      headers: Record<string, string>,
      body: Record<string, unknown>,
    ) => Promise<Record<string, unknown>>,
  ) {}

  /** Configured AND given a transport. A generator with a key and no way to
   * send it is not available, and reporting otherwise makes the fallback choose
   * a provider that then fails. */
  get isAvailable(): boolean {
    return (
      this.options.enabled &&
      this.options.apiKey.isSet &&
      this.options.model.length > 0 &&
      this.post !== undefined
    );
  }

  headers(): Record<string, string> {
    return {
      Authorization: `Bearer ${this.options.apiKey.reveal()}`,
      "Content-Type": "application/json",
    };
  }

  body(
    turns: readonly { role: string; content: string }[],
    system: string,
  ): Record<string, unknown> {
    const messages = system ? [{ role: "system", content: system }, ...turns] : [...turns];
    return {
      model: this.options.model,
      messages,
      max_tokens: this.options.maxOutputTokens,
      temperature: this.options.temperature,
    };
  }

  parse(raw: Record<string, unknown>): CloudChatResult {
    const choices = raw.choices as { message?: { content?: string } }[] | undefined;
    const usage = (raw.usage ?? {}) as { prompt_tokens?: number; completion_tokens?: number };
    return Object.freeze({
      text: choices?.[0]?.message?.content ?? "",
      providerId: this.providerId,
      inputTokens: usage.prompt_tokens ?? 0,
      outputTokens: usage.completion_tokens ?? 0,
      error: "",
    });
  }

  async generate(
    turns: readonly { role: string; content: string }[],
    system = "",
  ): Promise<CloudChatResult> {
    if (!this.isAvailable) {
      // Says "not configured" rather than "auth failed" - the second sends
      // somebody to rotate a credential that was never the problem.
      return Object.freeze({
        text: "",
        providerId: this.providerId,
        inputTokens: 0,
        outputTokens: 0,
        error: `${this.providerId} is not configured on this device`,
      });
    }
    try {
      return this.parse(
        await this.post!(`${this.options.baseUrl}/chat/completions`, this.headers(), this.body(turns, system)),
      );
    } catch (error) {
      return Object.freeze({
        text: "",
        providerId: this.providerId,
        inputTokens: 0,
        outputTokens: 0,
        error: `${this.providerId} did not answer: ${error instanceof Error ? error.message : String(error)}`,
      });
    }
  }
}

/**
 * Wires the providers a host has consented to.
 *
 * BOTH configured AND consented, not either. A configured provider nobody
 * agreed to is the failure this exists to prevent.
 */
export class CloudFallbackServiceCollectionExtensions {
  static addCloudFallback(
    candidates: readonly OpenAiCompatibleChatGeneratorBase[],
    consented: readonly string[],
  ): readonly OpenAiCompatibleChatGeneratorBase[] {
    const allowed = new Set(consented.map((c) => c.trim().toLowerCase()).filter(Boolean));
    return Object.freeze(
      candidates.filter((c) => allowed.has(c.providerId.toLowerCase()) && c.isAvailable),
    );
  }

  /** What a person is shown before anything leaves the device. */
  static describe(generators: readonly { providerId: string }[]): string {
    if (generators.length === 0) return "nothing here would leave this device";
    return `if this device cannot answer, it would ask: ${generators.map((g) => g.providerId).join(", ")}`;
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// The web surface

/** What a page says about itself. */
export interface PageMetadata {
  readonly title: string;
  readonly description: string;
  /** The path this page IS. Used for the canonical link and for cache keys, so
   * it must be the normalised form rather than whatever was typed. */
  readonly path: string;
  /** Undefined means it does not expire, which is right for a static page and
   * wrong for anything with data on it. */
  readonly expiresAtMs?: number;
  readonly language: string;
}

/** One route the device serves. */
export interface RouteDescriptor {
  readonly path: string;
  readonly handler: string;
  /** Whether this route may be reached from OFF the device. Off by default: the
   * common case is a page for the person holding the phone. */
  readonly isPublic: boolean;
  /** Whether a cached copy may answer. A page showing a balance should say no
   * however convenient a stale answer would be. */
  readonly cacheable: boolean;
  readonly methods: readonly string[];
}

/**
 * Collapses slashes, strips a trailing one, and lower-cases.
 *
 * Without this `/Chat`, `/chat/` and `//chat` are three cache entries and three
 * routes, and the third one usually 404s.
 */
export const normalisePath = (path: string): string =>
  ((path || "/").trim().replace(/\/{2,}/g, "/").replace(/\/+$/, "") || "/").toLowerCase();

export const routeDescriptor = (
  path: string,
  partial: Partial<RouteDescriptor> = {},
): RouteDescriptor =>
  Object.freeze({
    path,
    handler: partial.handler ?? "",
    isPublic: partial.isPublic ?? false,
    cacheable: partial.cacheable ?? true,
    methods: Object.freeze(partial.methods ?? ["GET"]),
  });

/**
 * A page kept for when the radio is off.
 *
 * A stale page shown with a note beats a blank screen when there is no way to
 * fetch a new one - so "stale" is not the same as "gone".
 */
export interface CachedResponse {
  readonly body: string;
  readonly mediaType: string;
  readonly storedAtMs: number;
  readonly ttlMs: number;
  readonly etag: string;
}

export const cachedResponse = (
  body: string,
  storedAtMs: number,
  partial: Partial<CachedResponse> = {},
): CachedResponse =>
  Object.freeze({
    body,
    mediaType: partial.mediaType ?? "text/html",
    storedAtMs,
    ttlMs: partial.ttlMs ?? 15 * 60_000,
    etag: partial.etag ?? "",
  });

export const isFresh = (r: CachedResponse, nowMs: number): boolean =>
  nowMs - r.storedAtMs < r.ttlMs;

/**
 * What to tell somebody looking at an old page.
 *
 * In minutes and hours rather than a timestamp, because "as of 14:03" does not
 * tell a person whether that is now.
 */
export function stalenessNote(r: CachedResponse, nowMs: number): string {
  if (isFresh(r, nowMs)) return "";
  const minutes = Math.floor(Math.max(0, nowMs - r.storedAtMs) / 60_000);
  return minutes < 60
    ? `this is ${minutes} minutes old - there was no connection to refresh it`
    : `this is ${Math.floor(minutes / 60)} hours old - there was no connection to refresh it`;
}

/** The pages this device serves. */
export interface WebBoard {
  routes(): readonly RouteDescriptor[];
  /** Returns the staleness warning alongside, empty when fresh - so a caller
   * cannot serve a stale page without having been handed the words to say so. */
  get(path: string, method?: string): { response?: CachedResponse; note: string };
  put(path: string, response: CachedResponse): void;
}

/** Routes and their cached bodies, in memory. */
export class InMemoryWebBoard implements WebBoard {
  private readonly routeMap = new Map<string, RouteDescriptor>();
  private readonly cache = new Map<string, CachedResponse>();

  constructor(
    routes: readonly RouteDescriptor[] = [],
    private readonly now: () => number = () => 0,
  ) {
    for (const route of routes) this.routeMap.set(normalisePath(route.path), route);
  }

  routes(): readonly RouteDescriptor[] {
    return Object.freeze([...this.routeMap.values()]);
  }

  addRoute(route: RouteDescriptor): void {
    this.routeMap.set(normalisePath(route.path), route);
  }

  get(path: string, method = "GET"): { response?: CachedResponse; note: string } {
    const key = normalisePath(path);
    const route = this.routeMap.get(key);
    if (!route) return { note: "there is no page at that address" };
    if (!route.methods.includes(method.toUpperCase())) {
      return { note: `${method.toUpperCase()} is not something that page accepts` };
    }
    const cached = this.cache.get(key);
    if (!cached) return { note: "" };
    if (!route.cacheable) {
      // A route marked uncacheable does not serve a stale copy EVEN when one is
      // sitting there. A balance shown from an hour ago is worse than none.
      return isFresh(cached, this.now()) ? { response: cached, note: "" } : { note: "" };
    }
    return { response: cached, note: stalenessNote(cached, this.now()) };
  }

  put(path: string, response: CachedResponse): void {
    this.cache.set(normalisePath(path), response);
  }

  invalidate(path = ""): number {
    if (!path) {
      const count = this.cache.size;
      this.cache.clear();
      return count;
    }
    return this.cache.delete(normalisePath(path)) ? 1 : 0;
  }
}

/**
 * The companion behind a web page served by the device.
 *
 * EVERY RESPONSE IS RENDERED WITH ESCAPING. The text comes from a model and the
 * model's input came from a person, so a page that interpolates it raw is a
 * page anybody can put script into by asking the assistant to repeat something.
 */
export class WebCompanionService {
  constructor(
    readonly board: WebBoard = new InMemoryWebBoard(),
    private readonly respond?: (question: string) => string,
    private readonly now: () => number = () => 0,
  ) {}

  /**
   * AMPERSAND FIRST.
   *
   * Escaping the angle brackets first would then escape the ampersands it just
   * introduced, turning `&lt;` into `&amp;lt;` and showing the markup to the
   * reader.
   */
  static escape(text: string): string {
    return text
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;")
      .replace(/'/g, "&#39;");
  }

  render(metadata: PageMetadata, bodyText: string): string {
    const lang = metadata.language ? ` lang="${WebCompanionService.escape(metadata.language)}"` : "";
    return `<article${lang}><h1>${WebCompanionService.escape(metadata.title)}</h1><p>${WebCompanionService.escape(bodyText)}</p></article>`;
  }

  /** Serves a cached answer when there is one and the device cannot produce a
   * new one. */
  ask(path: string, question: string): { html: string; note: string } {
    const { response, note } = this.board.get(path);
    if (!this.respond) {
      return response
        ? { html: response.body, note: note || "this device cannot answer right now" }
        : { html: "", note: "this device cannot answer right now" };
    }
    const html = this.render(
      { title: question, description: "", path, language: "" },
      this.respond(question),
    );
    this.board.put(path, cachedResponse(html, this.now()));
    return { html, note: "" };
  }
}

/** Wires the web surface. */
export class WebServiceCollectionExtensions {
  static addWeb(
    routes: readonly RouteDescriptor[] = [],
    respond?: (question: string) => string,
    now: () => number = () => 0,
  ): WebCompanionService {
    return new WebCompanionService(new InMemoryWebBoard(routes, now), respond, now);
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Hosting odds and ends

/**
 * What gets added to a system prompt, and what it costs.
 *
 * BUDGETED IN CHARACTERS. Every enrichment competes with the conversation for
 * the model's context, and an unbudgeted one grows until the earliest turns
 * fall out of the window - which reads as the assistant forgetting what was
 * just said.
 */
export interface SystemPromptEnrichment {
  readonly deviceContext: string;
  readonly recalledMemory: string;
  readonly activeSkills: string;
  readonly timeAndPlace: string;
  /** Anything past this is dropped in REVERSE PRIORITY order, so the device
   * context survives and the recalled memory is what goes. */
  readonly maxCharacters: number;
}

export const systemPromptEnrichment = (
  partial: Partial<SystemPromptEnrichment> = {},
): SystemPromptEnrichment =>
  Object.freeze({
    deviceContext: partial.deviceContext ?? "",
    recalledMemory: partial.recalledMemory ?? "",
    activeSkills: partial.activeSkills ?? "",
    timeAndPlace: partial.timeAndPlace ?? "",
    maxCharacters: partial.maxCharacters ?? 2000,
  });

/** Most important first, which is the order things are KEPT in. */
const enrichmentSections = (e: SystemPromptEnrichment): [string, string][] =>
  (
    [
      ["device", e.deviceContext],
      ["now", e.timeAndPlace],
      ["skills", e.activeSkills],
      ["memory", e.recalledMemory],
    ] as [string, string][]
  ).filter(([, value]) => value.trim().length > 0);

/**
 * Drops WHOLE SECTIONS rather than truncating one.
 *
 * Half a recalled memory is worse than none: the model reads the fragment as a
 * complete fact and answers from it.
 */
export function buildSystemPrompt(e: SystemPromptEnrichment): string {
  const out: string[] = [];
  let used = 0;
  for (const [name, value] of enrichmentSections(e)) {
    const block = `[${name}]\n${value}`;
    if (used + block.length > e.maxCharacters) continue;
    out.push(block);
    used += block.length + 1;
  }
  return out.join("\n");
}

export const wasTruncated = (e: SystemPromptEnrichment): boolean =>
  buildSystemPrompt(e).length <
  enrichmentSections(e).reduce((n, [name, value]) => n + `[${name}]\n${value}`.length + 1, 0);

/** Filtering and describing a tool catalogue. */
export class ToolCatalogExtensions {
  static visibleTo(
    tools: readonly { name: string; description?: string }[],
    granted: readonly string[],
  ): readonly { name: string; description?: string }[] {
    const allowed = new Set(granted.map((g) => g.trim().toLowerCase()).filter(Boolean));
    return Object.freeze(tools.filter((t) => allowed.has(t.name.toLowerCase())));
  }

  /**
   * Names only, never the schemas.
   *
   * This is shown to a PERSON asking what the assistant can do. A JSON schema
   * answers a different question, for a different reader.
   */
  static describe(tools: readonly { name: string; description?: string }[]): string {
    if (tools.length === 0) return "this device has no tools wired up";
    return `this can: ${tools.map((t) => t.description || t.name).join(", ")}`;
  }
}

/** Wires the on-device brain. */
export class NeuronServiceCollectionExtensions {
  private readonly registered = new Map<string, unknown>();

  addNeuron(name: string, service: unknown): NeuronServiceCollectionExtensions {
    this.registered.set(name, service);
    return this;
  }

  build(): Readonly<Record<string, unknown>> {
    return Object.freeze(Object.fromEntries(this.registered));
  }

  names(): readonly string[] {
    return Object.freeze([...this.registered.keys()].sort());
  }
}

/** Wires the host. */
export class HostingServiceCollectionExtensions {
  private readonly registered = new Map<string, unknown>();

  add(name: string, service: unknown): HostingServiceCollectionExtensions {
    if (!name.trim()) throw new Error("a registration needs a name");
    this.registered.set(name, service);
    return this;
  }

  get(name: string): unknown {
    return this.registered.get(name);
  }

  build(): Readonly<Record<string, unknown>> {
    return Object.freeze(Object.fromEntries(this.registered));
  }
}

/**
 * Parses a cron expression.
 *
 * FIVE FIELDS, and day-of-month against day-of-week is an OR, not an AND - the
 * one rule in cron that is genuinely surprising. `0 0 1 * MON` fires on the
 * first of the month AND on every Monday, not only on a Monday the first, and
 * an implementation that ANDs them silently runs a job about a seventh as
 * often as intended.
 */
export class CronScheduleParser {
  static parseField(field: string, min: number, max: number): number[] {
    const out = new Set<number>();
    for (const part of field.split(",")) {
      const [range, stepText] = part.split("/");
      const step = stepText ? Number(stepText) : 1;
      if (!Number.isInteger(step) || step < 1) throw new Error(`'${part}' has a bad step`);
      let from = min;
      let to = max;
      if (range !== "*") {
        const bounds = range.split("-").map(Number);
        if (bounds.some((n) => !Number.isInteger(n))) throw new Error(`'${part}' is not a number`);
        from = bounds[0];
        to = bounds.length > 1 ? bounds[1] : bounds[0];
        // A bare number with a step means "from here to the end", not "this one
        // value" - `5/10` in the minute field is 5, 15, 25 and so on.
        if (bounds.length === 1 && stepText) to = max;
      }
      if (from < min || to > max || from > to) throw new Error(`'${part}' is outside ${min}-${max}`);
      for (let v = from; v <= to; v += step) out.add(v);
    }
    return [...out].sort((a, b) => a - b);
  }

  static parse(expression: string): {
    minutes: number[];
    hours: number[];
    daysOfMonth: number[];
    months: number[];
    daysOfWeek: number[];
  } {
    const fields = expression.trim().split(/\s+/);
    if (fields.length !== 5) throw new Error("a cron expression has five fields");
    return {
      minutes: CronScheduleParser.parseField(fields[0], 0, 59),
      hours: CronScheduleParser.parseField(fields[1], 0, 23),
      daysOfMonth: CronScheduleParser.parseField(fields[2], 1, 31),
      months: CronScheduleParser.parseField(fields[3], 1, 12),
      // 7 is Sunday as well as 0, which is what everybody writes.
      daysOfWeek: CronScheduleParser.parseField(fields[4], 0, 7).map((d) => d % 7),
    };
  }

  /** Whether a moment matches, applying the day-of-month OR day-of-week rule. */
  static matches(expression: string, at: Date): boolean {
    const parsed = CronScheduleParser.parse(expression);
    const restrictedDom = !/^\*/.test(expression.trim().split(/\s+/)[2]);
    const restrictedDow = !/^\*/.test(expression.trim().split(/\s+/)[4]);
    const domMatch = parsed.daysOfMonth.includes(at.getUTCDate());
    const dowMatch = parsed.daysOfWeek.includes(at.getUTCDay());
    const dayMatch =
      restrictedDom && restrictedDow ? domMatch || dowMatch : domMatch && dowMatch;
    return (
      parsed.minutes.includes(at.getUTCMinutes()) &&
      parsed.hours.includes(at.getUTCHours()) &&
      parsed.months.includes(at.getUTCMonth() + 1) &&
      dayMatch
    );
  }
}

/**
 * Reads a model's rendering instructions.
 *
 * IT NEVER EXECUTES ANYTHING. What comes back is a description of components
 * and their properties, and a host decides what to draw - so a model cannot
 * produce a page that runs code by describing one.
 */
export class JsonRenderParser {
  /** Components a model is allowed to name. An ALLOW-LIST: anything else is
   * dropped rather than passed to a host that might render it. */
  static readonly ALLOWED = Object.freeze([
    "text", "heading", "list", "table", "chart", "image", "button", "input", "card",
  ]);

  static parse(json: string): { components: Record<string, unknown>[]; dropped: string[] } {
    let parsed: unknown;
    try {
      parsed = JSON.parse(json);
    } catch {
      // A reply that will not parse yields NOTHING rather than a partial page.
      // Half a rendered form is worse than a plain text answer.
      return { components: [], dropped: ["the reply was not readable as JSON"] };
    }
    const raw = Array.isArray(parsed) ? parsed : [parsed];
    const components: Record<string, unknown>[] = [];
    const dropped: string[] = [];
    for (const item of raw) {
      if (!item || typeof item !== "object") continue;
      const record = item as Record<string, unknown>;
      const kind = String(record.type ?? "").toLowerCase();
      if (!JsonRenderParser.ALLOWED.includes(kind)) {
        dropped.push(kind || "(no type)");
        continue;
      }
      components.push(record);
    }
    return { components, dropped };
  }
}

/**
 * A bridge that answers without a model.
 *
 * For a harness and a demo. Named `Mock` so nothing mistakes its answers for a
 * model's, and DETERMINISTIC so a test that depends on it does not flake.
 */
export class MockInferenceBridge {
  private calls = 0;

  constructor(private readonly replies: readonly string[] = ["ok"]) {}

  get callCount(): number {
    return this.calls;
  }

  generate(prompt: string): string {
    const reply = this.replies[this.calls % this.replies.length];
    this.calls += 1;
    return reply;
  }
}

// The C# spellings, kept so the two trees line up.
export type IWebBoard = WebBoard;
