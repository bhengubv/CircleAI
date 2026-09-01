/**
 * inference/server/handlers
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// inference/server/handlers.ts
//
// In-memory port of the inference server's HTTP endpoints. Per the port brief,
// no real socket is stood up — the routing logic of ChatCompletionsEndpoint /
// EmbeddingsEndpoint / CompanionEndpoint / AdminEndpoints is exposed behind the
// IInferenceServerHandler interface, returning typed HttpResult values (and,
// for streaming, writing SSE frames to a caller-supplied byte sink via the
// ServerSentEventsWriter). Every validation branch, status code, admission
// gate, and OpenAI response shape is ported faithfully.

import type { ICompanionSession } from "../../companion/index";
import type { ITextEmbedder } from "../../embeddings/index";
import {
  InferenceFragmentKind,
  InferenceStatus,
  type IInferenceBridge,
  type InferenceRequest,
} from "./bridge";
import {
  BackendKind,
  CapabilityTier,
  parseBackendKind,
  parseCapabilityTier,
} from "./bridge";
import type { IBridgeFactory } from "./bridge_factory";
import {
  LoadOutcome,
  UnloadOutcome,
  type IModelLifecycleManager,
  type ModelLoadDescriptor,
  type ModelLoadState,
} from "./lifecycle";
import {
  errorResponse,
  type ChatCompletionRequest,
  type ChatCompletionResponse,
  type ChatCompletionStreamChunk,
  type EmbeddingsRequest,
  type EmbeddingsResponse,
} from "./openai";
import type { ICompanionSessionResolver } from "./resolver";
import {
  AdmissionControl,
  type IInferenceServerModelRegistry,
  type ServerCounters,
} from "./runtime";
import type { InferenceServerOptions } from "./options";
import { ServerSentEventsWriter, type SseByteSink } from "./sse";

/** HTTP status codes used by the endpoints (subset of ASP.NET StatusCodes). */
export const StatusCodes = {
  Ok: 200,
  BadRequest: 400,
  NotFound: 404,
  InternalServerError: 500,
  ServiceUnavailable: 503,
  GatewayTimeout: 504,
  InsufficientStorage: 507,
} as const;

/** A non-streaming HTTP result: a status code + JSON body (+ optional headers). */
export interface HttpResult {
  readonly statusCode: number;
  readonly body: Object;
  readonly headers?: Readonly<Map<string, string>>;
  /** True for streaming responses whose body was written to the SSE sink. */
  readonly streamed?: boolean;
}

/** Request body for POST /v1/admin/models/load. Ported from AdminLoadRequest. */
export interface AdminLoadRequest {
  modelId: string;
  backend?: string;
  tier?: string;
  vramRequiredBytes?: number;
  ramRequiredBytes?: number;
}

/** Response body for /v1/admin/lifecycle. Ported from AdminLifecycleResponse. */
export interface AdminLifecycleResponse {
  totalAllocatedVramBytes: number;
  totalAllocatedRamBytes: number;
  loaded: ModelLoadState[];
}

/** Request body for POST /v1/companion/turn. Ported from CompanionTurnRequest. */
export interface CompanionTurnRequest {
  sessionId: string;
  identityId: string;
  message: string;
  agentic?: boolean;
  stream?: boolean;
}

/** Response body for a non-streaming companion turn. Ported from CompanionTurnResponse. */
export interface CompanionTurnResponse {
  sessionId: string;
  reply: string;
  agentic: boolean;
  turnIndex: number;
}

/**
 * In-memory equivalent of the server's route table. Each method mirrors one
 * ASP.NET endpoint handler. Streaming methods accept an SseByteSink and return
 * a `streamed` HttpResult once the SSE body has been written.
 */
export interface IInferenceServerHandler {
  chatCompletions(body: ChatCompletionRequest, sse?: SseByteSink, signal?: AbortSignal): Promise<HttpResult>;
  embeddings(body: EmbeddingsRequest, signal?: AbortSignal): Promise<HttpResult>;
  companionTurn(body: CompanionTurnRequest, sse?: SseByteSink, signal?: AbortSignal): Promise<HttpResult>;
  adminLifecycle(): HttpResult;
  adminLoad(body: AdminLoadRequest, signal?: AbortSignal): Promise<HttpResult>;
  adminUnload(modelId: string, signal?: AbortSignal): Promise<HttpResult>;
}

/** Dependencies the handler resolves (the C# DI container's registrations). */
export interface InferenceServerHandlerDeps {
  readonly registry: IInferenceServerModelRegistry;
  readonly admission: AdmissionControl;
  readonly counters: ServerCounters;
  readonly options: InferenceServerOptions;
  readonly lifecycle: IModelLifecycleManager;
  readonly bridgeFactory: IBridgeFactory;
  readonly companionResolver: ICompanionSessionResolver;
}

/** Default handler wiring all endpoints. */
export class InferenceServerHandler implements IInferenceServerHandler {
  constructor(private readonly deps: InferenceServerHandlerDeps) {}

  // ── POST /v1/chat/completions ───────────────────────────────────────────────

  async chatCompletions(
    body: ChatCompletionRequest,
    sse?: SseByteSink,
    signal?: AbortSignal,
  ): Promise<HttpResult> {
    if (!body || !body.model || body.model.trim().length === 0) {
      return badRequest(errorResponse("Missing or empty 'model' field.", "invalid_request_error", "missing_model"));
    }
    if (!body.messages || body.messages.length === 0) {
      return badRequest(errorResponse("Missing 'messages' array.", "invalid_request_error", "missing_messages"));
    }

    const bridge = this.deps.registry.resolve(body.model);
    if (bridge === null) {
      return json(
        StatusCodes.NotFound,
        errorResponse(`Model '${body.model}' is not loaded.`, "invalid_request_error", "model_not_found"),
      );
    }

    const slot = this.deps.admission.tryEnter();
    if (slot === null) {
      return {
        statusCode: StatusCodes.ServiceUnavailable,
        headers: { "Retry-After": "1" },
        body: errorResponse(
          `Server is at concurrency cap (${this.deps.admission.maxConcurrentRequests}). Retry after a brief delay.`,
          "server_busy",
          "concurrency_cap",
        ),
      };
    }

    try {
      const request = buildInferenceRequest(body);
      if (body.stream) {
        if (!sse) throw new Error("streaming requested but no SSE sink was supplied");
        await this.streamChat(sse, bridge, request, body);
        return { statusCode: StatusCodes.Ok, body: null, streamed: true };
      }
      return await this.nonStreamChat(bridge, request, body, signal);
    } finally {
      slot.release();
    }
  }

  private async nonStreamChat(
    bridge: IInferenceBridge,
    request: InferenceRequest,
    body: ChatCompletionRequest,
    signal?: AbortSignal,
  ): Promise<HttpResult> {
    let resp;
    try {
      resp = await bridge.complete(request, signal);
    } catch (e) {
      this.deps.counters.accountFailed();
      if (isAbort(e)) {
        return json(
          StatusCodes.GatewayTimeout,
          errorResponse("Request cancelled or timed out.", "timeout", "request_timeout"),
        );
      }
      const message = e instanceof Error ? e.message : String(e);
      return json(StatusCodes.InternalServerError, errorResponse(message, "internal_error", "bridge_failure"));
    }

    if (resp.status === InferenceStatus.Failed) {
      this.deps.counters.accountFailed();
      return json(
        StatusCodes.InternalServerError,
        errorResponse(resp.failureMessage ?? "Inference failed.", "internal_error", "inference_failed"),
      );
    }

    const response: ChatCompletionResponse = {
      id: `chatcmpl-${guidN()}`,
      object: "chat.completion",
      created: unixSeconds(),
      model: body.model,
      choices: [
        {
          index: 0,
          message: {
            role: "assistant",
            content: resp.outputText,
            reasoning_content: resp.reasoningText ?? null,
          },
          finish_reason: mapFinish(resp.status),
        },
      ],
      usage: {
        prompt_tokens: resp.promptTokenCount,
        completion_tokens: resp.outputTokenCount,
        total_tokens: resp.promptTokenCount + resp.outputTokenCount,
      },
    };
    return json(StatusCodes.Ok, response);
  }

  private async streamChat(
    sink: SseByteSink,
    bridge: IInferenceBridge,
    request: InferenceRequest,
    body: ChatCompletionRequest,
  ): Promise<void> {
    const sse = new ServerSentEventsWriter(sink);
    const id = `chatcmpl-${guidN()}`;
    const created = unixSeconds();

    const frame = (choice: ChatCompletionStreamChunk["choices"][number]): ChatCompletionStreamChunk => ({
      id,
      object: "chat.completion.chunk",
      created,
      model: body.model,
      choices: [choice],
    });

    // First frame: role announcement.
    await sse.writeAsync(frame({ index: 0, delta: { role: "assistant" } }));

    try {
      for await (const f of bridge.streamFragments(request)) {
        if (!f.text) continue;
        const delta =
          f.kind === InferenceFragmentKind.Reasoning
            ? { reasoning_content: f.text }
            : { content: f.text };
        await sse.writeAsync(frame({ index: 0, delta }));
      }
    } catch (e) {
      this.deps.counters.accountFailed();
      if (!isAbort(e)) {
        const message = e instanceof Error ? e.message : String(e);
        await sse.writeAsync(
          frame({ index: 0, delta: { content: `[error: ${message}]` }, finish_reason: "error" }),
        );
      }
    }

    // Final frame: stop reason + [DONE].
    await sse.writeAsync(frame({ index: 0, delta: {}, finish_reason: "stop" }));
    await sse.writeTerminatorAsync();
  }

  // ── POST /v1/embeddings ─────────────────────────────────────────────────────

  async embeddings(body: EmbeddingsRequest, signal?: AbortSignal): Promise<HttpResult> {
    if (!body || !body.model || body.model.trim().length === 0) {
      return badRequest(errorResponse("Missing or empty 'model' field.", "invalid_request_error", "missing_model"));
    }

    const embedder = this.deps.registry.resolveEmbedder(body.model);
    if (embedder === null) {
      return json(
        StatusCodes.NotFound,
        errorResponse(`Embedding model '${body.model}' is not loaded.`, "invalid_request_error", "model_not_found"),
      );
    }

    const normalised = normaliseInput(body.input);
    if (!normalised.ok) return badRequest(normalised.error);
    const inputs = normalised.inputs;

    const slot = this.deps.admission.tryEnter();
    if (slot === null) {
      return json(
        StatusCodes.ServiceUnavailable,
        errorResponse("Server is at concurrency cap. Retry shortly.", "server_busy", "concurrency_cap"),
      );
    }

    try {
      const data: EmbeddingsResponse["data"] = [];
      let totalChars = 0;
      try {
        for (let i = 0; i < inputs.length; i++) {
          const vec = await callEmbedder(embedder, inputs[i]!, signal);
          data.push({ object: "embedding", index: i, embedding: Array.from(vec) });
          totalChars += inputs[i]!.length;
        }
      } catch (e) {
        this.deps.counters.accountFailed();
        if (isAbort(e)) {
          return json(
            StatusCodes.GatewayTimeout,
            errorResponse("Request cancelled or timed out.", "timeout", "request_timeout"),
          );
        }
        const message = e instanceof Error ? e.message : String(e);
        return json(StatusCodes.InternalServerError, errorResponse(message, "internal_error", "embedding_failure"));
      }

      const estimatedPromptTokens = Math.max(1, Math.floor(totalChars / 4));
      const response: EmbeddingsResponse = {
        object: "list",
        data,
        model: body.model,
        usage: {
          prompt_tokens: estimatedPromptTokens,
          completion_tokens: 0,
          total_tokens: estimatedPromptTokens,
        },
      };
      return json(StatusCodes.Ok, response);
    } finally {
      slot.release();
    }
  }

  // ── POST /v1/companion/turn ─────────────────────────────────────────────────

  async companionTurn(
    body: CompanionTurnRequest,
    sse?: SseByteSink,
    signal?: AbortSignal,
  ): Promise<HttpResult> {
    if (
      !body ||
      !body.sessionId ||
      body.sessionId.trim().length === 0 ||
      !body.identityId ||
      body.identityId.trim().length === 0 ||
      !body.message ||
      body.message.trim().length === 0
    ) {
      return badRequest(
        errorResponse("session_id, identity_id, and message are all required.", "invalid_request_error", "missing_field"),
      );
    }

    const session = await this.deps.companionResolver.resolve(body.sessionId, body.identityId, signal);
    if (session === null) {
      return json(
        StatusCodes.NotFound,
        errorResponse(
          `No Companion session for session_id='${body.sessionId}', identity_id='${body.identityId}'.`,
          "invalid_request_error",
          "session_not_found",
        ),
      );
    }

    const slot = this.deps.admission.tryEnter();
    if (slot === null) {
      return json(
        StatusCodes.ServiceUnavailable,
        errorResponse("Server is at concurrency cap. Retry shortly.", "server_busy", "concurrency_cap"),
      );
    }

    try {
      if (body.stream) {
        if (!sse) throw new Error("streaming requested but no SSE sink was supplied");
        await this.streamCompanion(sse, session, body);
        return { statusCode: StatusCodes.Ok, body: null, streamed: true };
      }

      try {
        const reply = body.agentic
          ? await session.agentAsync(body.message)
          : await session.sendAsync(body.message);
        const response: CompanionTurnResponse = {
          sessionId: body.sessionId,
          reply,
          agentic: body.agentic ?? false,
          turnIndex: session.history.length,
        };
        return json(StatusCodes.Ok, response);
      } catch (e) {
        this.deps.counters.accountFailed();
        if (isAbort(e)) {
          return json(
            StatusCodes.GatewayTimeout,
            errorResponse("Request cancelled or timed out.", "timeout", "request_timeout"),
          );
        }
        const message = e instanceof Error ? e.message : String(e);
        return json(StatusCodes.InternalServerError, errorResponse(message, "internal_error", "companion_failure"));
      }
    } finally {
      slot.release();
    }
  }

  private async streamCompanion(
    sink: SseByteSink,
    session: ICompanionSession,
    body: CompanionTurnRequest,
  ): Promise<void> {
    const sse = new ServerSentEventsWriter(sink);
    try {
      for await (const chunk of session.streamAsync(body.message)) {
        if (!chunk) continue;
        await sse.writeAsync({ session_id: body.sessionId, delta: chunk });
      }
    } catch (e) {
      this.deps.counters.accountFailed();
      if (!isAbort(e)) {
        const message = e instanceof Error ? e.message : String(e);
        await sse.writeAsync({ session_id: body.sessionId, error: message });
      }
    }
    await sse.writeTerminatorAsync();
  }

  // ── /v1/admin/* ─────────────────────────────────────────────────────────────

  adminLifecycle(): HttpResult {
    const mgr = this.deps.lifecycle;
    const resp: AdminLifecycleResponse = {
      totalAllocatedVramBytes: mgr.totalAllocatedVramBytes,
      totalAllocatedRamBytes: mgr.totalAllocatedRamBytes,
      loaded: [...mgr.list()],
    };
    return json(StatusCodes.Ok, resp);
  }

  async adminLoad(body: AdminLoadRequest, signal?: AbortSignal): Promise<HttpResult> {
    if (!body || !body.modelId || body.modelId.trim().length === 0) {
      return badRequest(errorResponse("Missing 'modelId'.", "invalid_request_error", "missing_model"));
    }

    const backend = parseBackendKind(body.backend ?? "Cpu");
    if (backend === null) {
      return badRequest(
        errorResponse(
          `Unknown backend '${body.backend}'. Valid: Cpu, Cuda, Vulkan, OpenCL, Metal, Ascend, Cambricon, CoreML.`,
          "invalid_request_error",
          "invalid_backend",
        ),
      );
    }

    const tier = parseCapabilityTier(body.tier ?? "Tier1_Small");
    if (tier === null) {
      return badRequest(
        errorResponse(
          `Unknown tier '${body.tier}'. Valid: Tier0_Tiny..Tier4_Frontier.`,
          "invalid_request_error",
          "invalid_tier",
        ),
      );
    }

    const descriptor: ModelLoadDescriptor = {
      modelId: body.modelId,
      backend,
      requestedTier: tier,
      vramRequiredBytes: Math.max(0, body.vramRequiredBytes ?? 0),
      ramRequiredBytes: Math.max(0, body.ramRequiredBytes ?? 0),
      bridgeFactory: (cancel) => this.deps.bridgeFactory.create(body.modelId, backend, tier, cancel),
    };

    const result = await this.deps.lifecycle.load(descriptor, signal);
    switch (result.outcome) {
      case LoadOutcome.Loaded:
      case LoadOutcome.AlreadyLoaded:
        return json(StatusCodes.Ok, {
          outcome: LoadOutcome[result.outcome],
          state: result.state,
          rationale: result.rationale,
        });
      case LoadOutcome.InsufficientVram:
      case LoadOutcome.InsufficientRam:
        return json(
          StatusCodes.InsufficientStorage,
          errorResponse(result.rationale, "resource_exhausted", LoadOutcome[result.outcome]),
        );
      case LoadOutcome.FactoryFailed:
        return json(
          StatusCodes.InternalServerError,
          errorResponse(result.rationale, "internal_error", "factory_failed"),
        );
      default:
        return json(
          StatusCodes.InternalServerError,
          errorResponse(result.rationale, "internal_error", "unknown"),
        );
    }
  }

  async adminUnload(modelId: string, signal?: AbortSignal): Promise<HttpResult> {
    const outcome = await this.deps.lifecycle.unload(modelId, signal);
    switch (outcome) {
      case UnloadOutcome.Unloaded:
        return json(StatusCodes.Ok, { outcome: "Unloaded", modelId });
      case UnloadOutcome.NotLoaded:
        return json(
          StatusCodes.NotFound,
          errorResponse(`Model '${modelId}' is not loaded.`, "invalid_request_error", "not_loaded"),
        );
      default:
        return json(
          StatusCodes.InternalServerError,
          errorResponse("Unknown unload outcome.", "internal_error", "unknown"),
        );
    }
  }
}

// ── helpers ──────────────────────────────────────────────────────────────────

/**
 * Concatenate messages into a single prompt and build the InferenceRequest.
 * Ported verbatim from ChatCompletionsEndpoint.BuildInferenceRequest.
 */
export function buildInferenceRequest(body: ChatCompletionRequest): InferenceRequest {
  const prompt = body.messages.map((m) => `<|${m.role}|>\n${m.content}\n<|end|>`).join("\n");
  const metadata: Map<string, string> = {};
  if (body.user) metadata["user"] = body.user;

  return {
    id: guidDashed(),
    modelId: body.model,
    prompt,
    maxOutputTokens: body.max_tokens ?? 512,
    temperature: body.temperature ?? 0.7,
    topP: body.top_p ?? 0.9,
    stopSequences: body.stop ?? [],
    metadata,
    requestedAt: new Date().toISOString(),
  };
}

/** Map InferenceStatus to an OpenAI finish_reason. Ported from MapFinish. */
export function mapFinish(status: InferenceStatus): string {
  switch (status) {
    case InferenceStatus.Completed:
    case InferenceStatus.StoppedByToken:
      return "stop";
    case InferenceStatus.StoppedByLength:
      return "length";
    case InferenceStatus.Cancelled:
      return "cancelled";
    default:
      return "error";
  }
}

type NormaliseResult =
  | { ok: true; inputs: string[] }
  | { ok: false; error: ReturnType<typeof errorResponse> };

/** Normalise the OpenAI embeddings input. Ported from TryNormaliseInput. */
export function normaliseInput(input: string | string[]): NormaliseResult {
  if (typeof input === "string") {
    return { ok: true, inputs: [input] };
  }
  if (Array.isArray(input)) {
    const inputs: string[] = [];
    for (const el of input) {
      if (typeof el !== "string") {
        return {
          ok: false,
          error: errorResponse("Every 'input' array element must be a string.", "invalid_request_error", "invalid_input"),
        };
      }
      inputs.push(el);
    }
    if (inputs.length === 0) {
      return {
        ok: false,
        error: errorResponse("'input' array must not be empty.", "invalid_request_error", "invalid_input"),
      };
    }
    return { ok: true, inputs };
  }
  return {
    ok: false,
    error: errorResponse("'input' must be a string or array of strings.", "invalid_request_error", "invalid_input"),
  };
}

function json(statusCode: number, body: Object): HttpResult {
  return { statusCode, body };
}

function badRequest(body: Object): HttpResult {
  return { statusCode: StatusCodes.BadRequest, body };
}

function isAbort(e: Object): boolean {
  return e instanceof Error && (e.name === "AbortError" || e.name === "TimeoutError");
}

function unixSeconds(): number {
  return Math.floor(Date.now() / 1000);
}

/** Guid with no dashes (matches Guid.NewGuid():N). */
function guidN(): string {
  return guidDashed().replace(/-/g, "");
}

/** Random RFC-4122-shaped id. Matches the shape of Guid.NewGuid(). */
function guidDashed(): string {
  const b = new Uint8Array(16);
  for (let i = 0; i < 16; i++) b[i] = Math.floor(Math.random() * 256);
  b[6] = (b[6]! & 0x0f) | 0x40;
  b[8] = (b[8]! & 0x3f) | 0x80;
  const hex = Array.from(b, (x) => x.toString(16).padStart(2, "0")).join("");
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}

async function callEmbedder(embedder: ITextEmbedder, text: string, signal?: AbortSignal): Promise<Float32Array> {
  signal?.throwIfAborted();
  return embedder.generateAsync(text);
}
