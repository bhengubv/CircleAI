/**
 * inference/server/sse
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// inference/server/sse.ts
//
// Port of CircleAI.Inference.Server.Streaming.ServerSentEventsWriter. Frames a
// JSON payload as a `data: <json>\n\n` chunk. The C# writer flushes to an
// HttpResponse body; here the byte sink is an injected callback so the SSE
// framing (the load-bearing wire format) can be exercised in memory without a
// socket. `WhenWritingNull` semantics are matched by stripping null/undefined
// properties before serialising.

/** A sink that receives SSE frame bytes as they are written. */
export type SseByteSink = (bytes: Uint8Array) => void | Promise<void>;

/**
 * Writes SSE-framed JSON chunks to a byte sink. Each writeAsync emits one frame;
 * the caller writes the terminator explicitly. Ported from
 * ServerSentEventsWriter.
 */
export class ServerSentEventsWriter {
  private readonly sink: SseByteSink;
  private started = false;
  /** Header lines the C# writer sets on first frame; captured for inspection. */
  readonly headers = new Map<string, string>();

  constructor(sink: SseByteSink) {
    if (!sink) throw new Error("sink required");
    this.sink = sink;
  }

  /** Write one SSE frame: `data: <json>\n\n`. */
  async writeAsync<T>(payload: T): Promise<void> {
    this.ensureHeaders();
    const json = JSON.stringify(payload, stripNullReplacer);
    await this.sink(new TextEncoder().encode(`data: ${json}\n\n`));
  }

  /** Write the OpenAI terminator `data: [DONE]\n\n`. */
  async writeTerminatorAsync(): Promise<void> {
    this.ensureHeaders();
    await this.sink(new TextEncoder().encode("data: [DONE]\n\n"));
  }

  private ensureHeaders(): void {
    if (this.started) return;
    this.started = true;
    this.headers.set("Content-Type", "text/event-stream; charset=utf-8");
    this.headers.set("Cache-Control", "no-cache, no-store");
    this.headers.set("Connection", "keep-alive");
    this.headers.set("X-Accel-Buffering", "no");
  }
}

/**
 * JSON.stringify replacer that drops null/undefined values, matching the C#
 * JsonIgnoreCondition.WhenWritingNull on the OpenAI DTOs (reasoning_content,
 * delta fields, error.param/code, etc.).
 */
export function stripNullReplacer(_key: string, value: unknown): unknown {
  return value === null || value === undefined ? undefined : value;
}
