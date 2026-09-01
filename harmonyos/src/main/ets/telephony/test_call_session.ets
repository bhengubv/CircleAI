/**
 * telephony/test_call_session
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// telephony/test_call_session.ts
//
// Build voice loops without paying for a real carrier minute — faithful port of
// TestCallSession.cs. An in-memory ICallSession that lets a test harness inject
// inbound audio + DTMF, capture outbound audio, and drive lifecycle events on
// demand.
//
// C# `Channel<T>` (unbounded, single-reader) → {@link AsyncChannel}. The
// StatusChanged event → on/off handler registration matching the ICallSession
// contract. `Guid.NewGuid().ToString("n")` → a 32-char lowercase hex id.

import type { CallStatusChangedHandler, ICallSession } from "./contracts";
import type { AudioFrame, CallInfo, DtmfEvent } from "./primitives";
import { CallDirection, CallMediaFormat, CallStatus, TransferMode, callInfo } from "./primitives";
import { AsyncChannel } from "./internal";

function newGuidN(): string {
  // 32 lowercase hex chars, no dashes (matches Guid "n" format).
  const bytes = new Uint8Array(16);
  for (let i = 0; i < bytes.length; i++) bytes[i] = Math.floor(Math.random() * 256);
  let s = "";
  for (const b of bytes) s += b.toString(16).padStart(2, "0");
  return s;
}

/** In-memory ICallSession for harnesses + unit tests. Mirrors `TestCallSession`. */
export class TestCallSession implements ICallSession {
  private readonly inboundAudio = new AsyncChannel<AudioFrame>();
  private readonly inboundDtmf = new AsyncChannel<DtmfEvent>();
  private readonly outboundAudio: AudioFrame[] = [];
  private readonly outboundDtmf: string[] = [];
  private statusValue: CallStatus = CallStatus.Active;
  private readonly statusHandlers = new Set<CallStatusChangedHandler>();

  readonly info: CallInfo;

  constructor(info?: CallInfo) {
    this.info =
      info ??
      callInfo(
        newGuidN(),
        CallDirection.Inbound,
        "+15555550100",
        "+15555550200",
        "test",
        CallMediaFormat.Pcm16000,
        new Date(),
      );
  }

  get status(): CallStatus {
    return this.statusValue;
  }

  onStatusChanged(handler: CallStatusChangedHandler): void {
    this.statusHandlers.add(handler);
  }

  offStatusChanged(handler: CallStatusChangedHandler): void {
    this.statusHandlers.delete(handler);
  }

  /** Outbound audio frames the AI has emitted, captured for assertions. */
  get sentAudioFrames(): readonly AudioFrame[] {
    return [...this.outboundAudio];
  }

  /** Outbound DTMF strings the AI has emitted. */
  get sentDtmf(): readonly string[] {
    return [...this.outboundDtmf];
  }

  /** Inject one inbound audio frame for the AI to consume via receiveAudioAsync. */
  injectInboundAudio(frame: AudioFrame): void {
    if (frame === null || frame === undefined) throw new Error("frame is required");
    this.inboundAudio.tryWrite(frame);
  }

  /** Inject one inbound DTMF event. */
  injectInboundDtmf(ev: DtmfEvent): void {
    if (ev === null || ev === undefined) throw new Error("ev is required");
    this.inboundDtmf.tryWrite(ev);
  }

  /** Stop the inbound streams cleanly. */
  endInboundStreams(): void {
    this.inboundAudio.tryComplete();
    this.inboundDtmf.tryComplete();
  }

  /** Trigger a status change (e.g. caller hangs up). */
  triggerStatusChange(newStatus: CallStatus): void {
    this.statusValue = newStatus;
    for (const handler of [...this.statusHandlers]) handler(newStatus);
  }

  receiveAudioAsync(signal?: AbortSignal): AsyncIterable<AudioFrame> {
    return this.inboundAudio.readAllAsync(signal);
  }

  receiveDtmfAsync(signal?: AbortSignal): AsyncIterable<DtmfEvent> {
    return this.inboundDtmf.readAllAsync(signal);
  }

  sendAudioAsync(frame: AudioFrame, _signal?: AbortSignal): Promise<void> {
    if (frame === null || frame === undefined) throw new Error("frame is required");
    this.outboundAudio.push(frame);
    return Promise.resolve();
  }

  sendDtmfAsync(digits: string, _signal?: AbortSignal): Promise<void> {
    if (digits === null || digits === undefined) throw new Error("digits is required");
    this.outboundDtmf.push(digits);
    return Promise.resolve();
  }

  transferAsync(
    _target: string,
    _mode: TransferMode,
    _briefing?: string,
    _signal?: AbortSignal,
  ): Promise<void> {
    this.triggerStatusChange(CallStatus.Transferred);
    return Promise.resolve();
  }

  hangUpAsync(_signal?: AbortSignal): Promise<void> {
    this.triggerStatusChange(CallStatus.EndedByAgent);
    this.endInboundStreams();
    return Promise.resolve();
  }

  disposeAsync(): Promise<void> {
    this.endInboundStreams();
    return Promise.resolve();
  }
}
