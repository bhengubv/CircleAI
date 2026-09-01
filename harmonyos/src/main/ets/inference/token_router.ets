/**
 * inference/token_router
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// inference/token_router.ts
//
// Port of CircleAI.Inference.MnnTokenRouter — the shared token-callback +
// <think>...</think> reasoning router used by every generator. Decoded text
// fragments get routed into either the Content or Reasoning channel based on a
// small state machine over the emitted text, with a holdback so a tag that
// straddles a fragment boundary is never mis-classified.
//
// In C# this drove the native MNN callback; here it is a pure text router the
// deterministic generator feeds decoded chunks into. UTF-8 draining
// (TryDrainUtf8) is ported verbatim for callers that decode byte-by-byte.

import { ChatFragmentKind } from "../models/index";
import type { ChatFragment } from "../models/index";

// Reasoning-trace tags emitted by Qwen3 / DeepSeek / o1-style models.
export const THINK_OPEN = "<think>";
export const THINK_CLOSE = "</think>";

/**
 * Holdback size — we never flush the last `THINK_CLOSE.length - 1` characters
 * of decoded text until a new fragment arrives, so a tag that straddles a
 * token boundary is never mis-classified as content or reasoning.
 */
export const THINK_HOLDBACK = 7; // = THINK_CLOSE.length - 1

/**
 * Per-call routing state. Mirrors the fields of C#'s MnnTokenSink that survive
 * across the routing calls. `writer` receives tagged fragments in order.
 */
export class TokenRouterSink {
  /** All decoded text so far. */
  emitted = "";
  /** Whether we are currently inside a <think> block. */
  inThink = false;
  /** How many chars of `emitted` have already been routed to `writer`. */
  flushedChars = 0;
  /** Set once a stop sequence has fired. */
  stopped = false;

  constructor(
    readonly stopSequences: readonly string[],
    readonly writer: (fragment: ChatFragment) => void,
    readonly includeReasoning: boolean,
  ) {}
}

/**
 * Feed one decoded text chunk through the router. Appends to `emitted`, checks
 * for stop sequences, then routes the newly-safe prefix (holding back the last
 * THINK_HOLDBACK chars). Returns `true` when a stop sequence fired (caller
 * should stop generating), matching the non-zero return of C#'s OnTokenNative.
 */
export function routeChunk(sink: TokenRouterSink, chunk: string): boolean {
  if (sink.stopped) return true;
  if (chunk.length === 0) return false;

  sink.emitted += chunk;

  // Stop-sequence safety check.
  const stop = tryFindStopSequence(sink.emitted, sink.stopSequences);
  if (stop >= 0) {
    routeUntil(sink, stop);
    sink.stopped = true;
    return true;
  }

  const safeUpTo = Math.max(sink.flushedChars, sink.emitted.length - THINK_HOLDBACK);
  routeUntil(sink, safeUpTo);
  return false;
}

/**
 * After the generation loop ends, flush any holdback remainder. Idempotent.
 * When `stopped` is set the stop-sequence flush already wrote everything up to
 * (but not including) the stop marker — the remaining text IS the marker, so
 * it must NOT be routed. Mirrors MnnTokenRouter.DrainRemainder.
 */
export function drainRemainder(sink: TokenRouterSink): void {
  if (sink.stopped) return;
  routeUntil(sink, sink.emitted.length);
}

/**
 * Push emitted[flushedChars..upTo] through the <think> state machine and write
 * tagged fragments to the sink's writer. Always advances flushedChars.
 * Ported verbatim from MnnTokenRouter.RouteUntil.
 */
function routeUntil(sink: TokenRouterSink, upTo: number): void {
  if (upTo <= sink.flushedChars) return;

  const src = sink.emitted;
  while (sink.flushedChars < upTo) {
    if (sink.inThink) {
      // Hunt for </think> in [flushedChars, upTo + THINK_CLOSE.length].
      const searchEnd = Math.min(src.length, upTo + THINK_CLOSE.length);
      const closeIdx = indexOfIn(src, THINK_CLOSE, sink.flushedChars, searchEnd);
      if (closeIdx >= 0 && closeIdx + THINK_CLOSE.length <= src.length) {
        if (closeIdx > sink.flushedChars && sink.includeReasoning) {
          const text = src.substring(sink.flushedChars, closeIdx);
          sink.writer({ kind: ChatFragmentKind.Reasoning, text });
        }
        sink.flushedChars = closeIdx + THINK_CLOSE.length;
        sink.inThink = false;
        continue;
      }
      if (upTo > sink.flushedChars && sink.includeReasoning) {
        const text = src.substring(sink.flushedChars, upTo);
        sink.writer({ kind: ChatFragmentKind.Reasoning, text });
      }
      sink.flushedChars = upTo;
      return;
    } else {
      const searchEnd = Math.min(src.length, upTo + THINK_OPEN.length);
      const openIdx = indexOfIn(src, THINK_OPEN, sink.flushedChars, searchEnd);
      if (openIdx >= 0 && openIdx + THINK_OPEN.length <= src.length) {
        if (openIdx > sink.flushedChars) {
          const text = src.substring(sink.flushedChars, openIdx);
          sink.writer({ kind: ChatFragmentKind.Content, text });
        }
        sink.flushedChars = openIdx + THINK_OPEN.length;
        sink.inThink = true;
        continue;
      }
      if (upTo > sink.flushedChars) {
        const text = src.substring(sink.flushedChars, upTo);
        sink.writer({ kind: ChatFragmentKind.Content, text });
      }
      sink.flushedChars = upTo;
      return;
    }
  }
}

function indexOfIn(src: string, needle: string, start: number, end: number): number {
  if (end <= start) return -1;
  const slice = src.substring(start, end);
  const found = slice.indexOf(needle);
  return found < 0 ? -1 : start + found;
}

/**
 * Drains as many complete UTF-8 codepoints as possible from `pending` and
 * returns them as a decoded string. Trailing partial-codepoint bytes stay in
 * the buffer for the next call. Returns null when nothing complete is available.
 * Ported verbatim from MnnTokenRouter.TryDrainUtf8 (the byte-boundary logic is
 * the load-bearing part for streaming decoders).
 */
export function tryDrainUtf8(pending: number[]): string | null {
  if (pending.length === 0) return null;

  let safeLen = pending.length;
  for (let i = pending.length - 1; i >= 0 && i >= pending.length - 4; i--) {
    const b = pending[i]!;
    if ((b & 0x80) === 0) break;
    if ((b & 0xc0) === 0xc0) {
      const needed =
        (b & 0xe0) === 0xc0 ? 2 : (b & 0xf0) === 0xe0 ? 3 : (b & 0xf8) === 0xf0 ? 4 : 1;
      const have = pending.length - i;
      if (have < needed) safeLen = i;
      break;
    }
  }

  if (safeLen === 0) return null;

  const arr = pending.splice(0, safeLen);
  const decoded = new TextDecoder("utf-8").decode(new Uint8Array(arr));
  return decoded.length > 0 ? decoded : null;
}

/**
 * Returns the index of the first stop sequence found in `s`, or -1.
 * Ported from MnnTokenRouter.TryFindStopSequence.
 */
export function tryFindStopSequence(s: string, stops: readonly string[]): number {
  for (const stop of stops) {
    if (!stop) continue;
    const idx = s.indexOf(stop);
    if (idx >= 0) return idx;
  }
  return -1;
}
