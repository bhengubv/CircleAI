/**
 * inference/server/openai
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// inference/server/openai.ts
//
// OpenAI-compatible DTOs for /v1/chat/completions, /v1/embeddings, and the
// error envelope. Ported from CircleAI.Inference.Server.Models.OpenAI. Field
// names match the public OpenAI API JSON shape exactly (snake_case on the wire)
// so SDKs targeting OpenAI work against CircleAI with only a base-URL change.
// These are the serialized contracts, so the property names are the wire names.

// ── ChatCompletion ────────────────────────────────────────────────────────────

/** OpenAI-shaped chat-completion request body. Ported from ChatCompletionRequest. */
export interface ChatCompletionRequest {
  model: string;
  messages: ChatCompletionMessage[];
  temperature?: number | null;
  top_p?: number | null;
  max_tokens?: number | null;
  stream?: boolean;
  stop?: string[] | null;
  user?: string | null;
}

/** One message in the chat completion conversation. Ported from ChatCompletionMessage. */
export interface ChatCompletionMessage {
  /** OpenAI roles: system, user, assistant, tool. */
  role: string;
  content: string;
  name?: string | null;
  /** DeepSeek-style reasoning trace; omitted from JSON when null. */
  reasoning_content?: string | null;
}

/** OpenAI-shaped successful chat completion response. Ported from ChatCompletionResponse. */
export interface ChatCompletionResponse {
  id: string;
  object: string; // "chat.completion"
  created: number;
  model: string;
  choices: ChatCompletionChoice[];
  usage: UsageInfo;
}

/** One choice in a non-streaming chat completion response. */
export interface ChatCompletionChoice {
  index: number;
  message: ChatCompletionMessage;
  finish_reason: string;
}

/** Token-usage block. Ported from UsageInfo. */
export interface UsageInfo {
  prompt_tokens: number;
  completion_tokens: number;
  total_tokens: number;
}

/** One SSE delta frame in a streamed chat completion. Ported from ChatCompletionStreamChunk. */
export interface ChatCompletionStreamChunk {
  id: string;
  object: string; // "chat.completion.chunk"
  created: number;
  model: string;
  choices: ChatCompletionStreamChoice[];
}

/** One delta in a streamed chat completion chunk. */
export interface ChatCompletionStreamChoice {
  index: number;
  delta: ChatCompletionDelta;
  finish_reason?: string | null;
}

/** Delta payload — only non-null fields are emitted between SSE frames. */
export interface ChatCompletionDelta {
  role?: string | null;
  content?: string | null;
  reasoning_content?: string | null;
}

// ── Embeddings ────────────────────────────────────────────────────────────────

/**
 * OpenAI-shaped embeddings request. `input` is either a single string or an
 * array of strings (the C# used a raw JsonElement — here it is the union).
 * Ported from EmbeddingsRequest.
 */
export interface EmbeddingsRequest {
  model: string;
  input: string | string[];
  user?: string | null;
}

/** OpenAI-shaped embeddings response. Ported from EmbeddingsResponse. */
export interface EmbeddingsResponse {
  object: string; // "list"
  data: EmbeddingDatum[];
  model: string;
  usage: UsageInfo;
}

/** One embedding row in the response. Ported from EmbeddingDatum. */
export interface EmbeddingDatum {
  object: string; // "embedding"
  index: number;
  embedding: number[];
}

// ── ErrorResponse ─────────────────────────────────────────────────────────────

/** Inner error body. Ported from ErrorBody. */
export interface ErrorBody {
  message: string;
  type: string;
  param?: string | null;
  code?: string | null;
}

/** OpenAI-shaped error envelope `{"error": {...}}`. Ported from ErrorResponse. */
export interface ErrorResponse {
  error: ErrorBody;
}

/** Build an error envelope. Ported from ErrorResponse.Of. */
export function errorResponse(message: string, type: string, code: string | null = null): ErrorResponse {
  return { error: { message, type, code } };
}
