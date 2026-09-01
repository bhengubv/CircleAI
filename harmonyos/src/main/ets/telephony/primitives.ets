/**
 * telephony/primitives
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// telephony/primitives.ts
//
// Shared value types for the telephony surface — a faithful port of
// CircleAI.Telephony/Primitives.cs (C# is the exact spec). Direction +
// call-lifecycle states + media-format negotiation primitives, kept minimal so
// a real-world inbound or outbound call needs nothing else in scope.
//
// Type mappings (C# → TS):
//   enum                       → const-object enum + string-literal union type
//   record                     → readonly interface (+ positional factory)
//   ReadOnlyMemory<byte>       → Uint8Array
//   TimeSpan                   → milliseconds (number)
//   DateTimeOffset             → Date (UTC instant)
//   decimal                    → number

// ─────────────────────────────────────────────────────────────────────────────
// CallDirection
// ─────────────────────────────────────────────────────────────────────────────

/** Call direction. Mirrors `CallDirection`. */
export const CallDirection = {
  Inbound: "Inbound",
  Outbound: "Outbound",
} as const;
export type CallDirection = (typeof CallDirection)[keyof typeof CallDirection];

// ─────────────────────────────────────────────────────────────────────────────
// CallStatus
// ─────────────────────────────────────────────────────────────────────────────

/** Call lifecycle states. Mirrors `CallStatus`. */
export const CallStatus = {
  /** Carrier accepted the dial but the other end has not picked up yet. */
  Ringing: "Ringing",
  /** Both sides connected; media flowing. */
  Active: "Active",
  /** Caller hung up. */
  EndedByCaller: "EndedByCaller",
  /** Callee hung up. */
  EndedByCallee: "EndedByCallee",
  /** AI agent (us) ended the call. */
  EndedByAgent: "EndedByAgent",
  /** Carrier-detected voicemail / answering machine on outbound dial. */
  Voicemail: "Voicemail",
  /** Call did not connect (busy, no answer, network). */
  Failed: "Failed",
  /** Call transferred to a human or a different agent. */
  Transferred: "Transferred",
} as const;
export type CallStatus = (typeof CallStatus)[keyof typeof CallStatus];

// ─────────────────────────────────────────────────────────────────────────────
// CallMediaFormat
// ─────────────────────────────────────────────────────────────────────────────

/** Audio wire formats supported across carriers. Mirrors `CallMediaFormat`. */
export const CallMediaFormat = {
  /** µ-law 8 kHz mono — Twilio default, Plivo default, fallback Telnyx. */
  Mulaw8000: "Mulaw8000",
  /** A-law 8 kHz mono — some European carriers. */
  Alaw8000: "Alaw8000",
  /** Linear PCM 16-bit 16 kHz mono — Telnyx negotiated path. */
  Pcm16000: "Pcm16000",
  /** Linear PCM 16-bit 24 kHz mono — high-quality WebRTC, OpenAI Realtime. */
  Pcm24000: "Pcm24000",
} as const;
export type CallMediaFormat = (typeof CallMediaFormat)[keyof typeof CallMediaFormat];

// ─────────────────────────────────────────────────────────────────────────────
// TransferMode
// ─────────────────────────────────────────────────────────────────────────────

/** Transfer mode the AI requests from the carrier. Mirrors `TransferMode`. */
export const TransferMode = {
  /** Drop the caller into the new line and hang up — fast, no context handover. */
  Cold: "Cold",
  /** Park caller, dial human, brief human verbally, then bridge both — context preserved. */
  Warm: "Warm",
} as const;
export type TransferMode = (typeof TransferMode)[keyof typeof TransferMode];

// ─────────────────────────────────────────────────────────────────────────────
// CallInfo — record (immutable metadata captured once at call start)
// ─────────────────────────────────────────────────────────────────────────────

/** Information about one call. Captured once at call start, immutable. Mirrors `CallInfo`. */
export interface CallInfo {
  /** Carrier-supplied unique id (Twilio CallSid, Telnyx call_control_id, etc.). */
  readonly callId: string;
  /** Direction — who initiated. */
  readonly direction: CallDirection;
  /** Caller's phone number in E.164 format (e.g. +27821234567). */
  readonly from: string;
  /** Called party's phone number in E.164 format. */
  readonly to: string;
  /** Carrier id (e.g. "twilio", "telnyx", "plivo"). */
  readonly carrierId: string;
  /** Audio wire format the carrier is streaming. */
  readonly mediaFormat: CallMediaFormat;
  /** When the call started (UTC instant). */
  readonly startedAtUtc: Date;
}

/** Constructs a {@link CallInfo}. */
export function callInfo(
  callId: string,
  direction: CallDirection,
  from: string,
  to: string,
  carrierId: string,
  mediaFormat: CallMediaFormat,
  startedAtUtc: Date,
): CallInfo {
  return { callId, direction, from, to, carrierId, mediaFormat, startedAtUtc };
}

// ─────────────────────────────────────────────────────────────────────────────
// CallSnapshot — record (a call's current state)
// ─────────────────────────────────────────────────────────────────────────────

/** A snapshot of a call's current state. Returned by lifecycle queries. Mirrors `CallSnapshot`. */
export interface CallSnapshot {
  /** Carrier-captured call metadata. */
  readonly info: CallInfo;
  /** Current lifecycle state. */
  readonly status: CallStatus;
  /** How long since the call connected, in milliseconds. */
  readonly durationMs: number;
  /** Per-second cost so far (carrier minutes + any LLM/STT/TTS attached). */
  readonly costSoFar: number;
  /** If {@link CallStatus.Transferred}, the E.164 number we transferred to. */
  readonly transferTarget?: string;
}

/** Constructs a {@link CallSnapshot}. */
export function callSnapshot(
  info: CallInfo,
  status: CallStatus,
  durationMs: number,
  costSoFar: number,
  transferTarget?: string,
): CallSnapshot {
  return { info, status, durationMs, costSoFar, transferTarget };
}

// ─────────────────────────────────────────────────────────────────────────────
// AudioFrame — record (audio chunk in either direction)
// ─────────────────────────────────────────────────────────────────────────────

/** Audio chunk flowing from caller → AI or AI → caller. Mirrors `AudioFrame`. */
export interface AudioFrame {
  /** The PCM audio bytes. */
  readonly pcm: Uint8Array;
  /** The wire format of {@link pcm}. */
  readonly format: CallMediaFormat;
  /** When (relative to call start), in milliseconds. */
  readonly offsetMs: number;
}

/** Constructs an {@link AudioFrame}. */
export function audioFrame(pcm: Uint8Array, format: CallMediaFormat, offsetMs: number): AudioFrame {
  return { pcm, format, offsetMs };
}

// ─────────────────────────────────────────────────────────────────────────────
// DtmfEvent — record (a DTMF tone from the caller)
// ─────────────────────────────────────────────────────────────────────────────

/** DTMF tone from the caller. Mirrors `DtmfEvent`. */
export interface DtmfEvent {
  /** The digit (0-9, *, #). */
  readonly digit: string;
  /** How long the caller held it, in milliseconds. */
  readonly durationMs: number;
  /** When (relative to call start), in milliseconds. */
  readonly offsetMs: number;
}

/** Constructs a {@link DtmfEvent}. */
export function dtmfEvent(digit: string, durationMs: number, offsetMs: number): DtmfEvent {
  return { digit, durationMs, offsetMs };
}

// ─────────────────────────────────────────────────────────────────────────────
// ProvisionedNumber — record (result of a number-provisioning request)
// ─────────────────────────────────────────────────────────────────────────────

/** Result of a number-provisioning request. Mirrors `ProvisionedNumber`. */
export interface ProvisionedNumber {
  readonly phoneNumber: string;
  readonly carrierId: string;
  readonly provisionedAtUtc: Date;
  readonly monthlyRecurringCost: number;
}

/** Constructs a {@link ProvisionedNumber}. */
export function provisionedNumber(
  phoneNumber: string,
  carrierId: string,
  provisionedAtUtc: Date,
  monthlyRecurringCost: number,
): ProvisionedNumber {
  return { phoneNumber, carrierId, provisionedAtUtc, monthlyRecurringCost };
}
