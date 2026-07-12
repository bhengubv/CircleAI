// telephony/contracts.ts
//
// The CircleAI.Telephony contract surface — carrier-agnostic. Faithful port of
// Contracts.cs, IMediaStream.cs, and IDtmfSendable.cs (C# is the exact spec).
// Any consumer (txtMe, Panik, salon receptionist) talks to this; the real
// Twilio / Telnyx / Plivo adapters ship as sibling packages.
//
// Type mappings (C# → TS):
//   interface                    → interface
//   IAsyncEnumerable<T>          → AsyncIterable<T>
//   CancellationToken            → AbortSignal (optional, named `signal`)
//   ValueTask / Task             → Promise
//   IAsyncDisposable             → { disposeAsync(): Promise<void> }
//   event EventHandler<CallStatus> → on/off("statusChanged", handler)
//   Uri                          → string (absolute URL)
//   IDisposable (subscription)   → { dispose(): void }
//
// HTTP SEAM. Twilio/Telnyx/Plivo carriers are HTTP boundaries. The C# takes an
// injected `HttpClient`; we mirror that with the `IHttpClient` transport
// abstraction (identical to integration/index.ts's convention) so orchestration
// runs deterministically against any injected transport.

import type { CallInfo, CallStatus, ProvisionedNumber } from "./primitives.js";
import type { AudioFrame, DtmfEvent } from "./primitives.js";

// ─────────────────────────────────────────────────────────────────────────────
// HTTP transport seam (mirrors integration/index.ts)
// ─────────────────────────────────────────────────────────────────────────────

/** HTTP verbs the carrier + consult + MCP boundaries use. */
export type HttpMethod = "GET" | "POST" | "PUT" | "DELETE" | "PATCH";

/** An outbound HTTP request assembled by a carrier / consult / MCP seam. Mirrors `HttpRequestMessage`. */
export interface HttpRequest {
  readonly method: HttpMethod;
  /** Absolute URL, or a path resolved against the transport's `baseAddress`. */
  readonly url: string;
  /** Request headers (case-insensitive by convention; keys as supplied). */
  readonly headers: ReadonlyMap<string, string>;
  /** Request body (already-serialized string), or undefined for no body. */
  readonly body?: string;
}

/** An HTTP response. Mirrors the parts of `HttpResponseMessage` the seams read. */
export interface HttpResponse {
  readonly statusCode: number;
  readonly body: string;
}

/**
 * The injected HTTP transport. Mirrors the slice of `System.Net.Http.HttpClient`
 * the seams use: an optional base address + a single `send`. Production hosts
 * wrap `fetch`; tests inject a deterministic fake.
 */
export interface IHttpClient {
  /** Base address prepended to relative request URLs (mirrors `HttpClient.BaseAddress`). */
  readonly baseAddress?: string;
  /** Send one request and resolve its response. */
  send(request: HttpRequest, signal?: AbortSignal): Promise<HttpResponse>;
}

/** True when `statusCode` is in the 2xx success range (mirrors `IsSuccessStatusCode`). */
export function isSuccessStatusCode(statusCode: number): boolean {
  return statusCode >= 200 && statusCode < 300;
}

// ─────────────────────────────────────────────────────────────────────────────
// OutboundDialOptions — record
// ─────────────────────────────────────────────────────────────────────────────

/** Optional knobs for an outbound dial. Mirrors `OutboundDialOptions`. */
export interface OutboundDialOptions {
  /** If true, detect voicemail and surface {@link CallStatus.Voicemail}. */
  readonly detectAnsweringMachine?: boolean;
  /** How long to ring before treating it as no-answer. Default 30 s. */
  readonly ringTimeoutSeconds?: number;
  /** Optional caller-id override (must be a number you own). */
  readonly callerIdOverride?: string;
  /** Optional list of E.164 numbers to also dial if the primary doesn't answer (round-robin). */
  readonly followMeNumbers?: readonly string[];
}

/** Default ring timeout (seconds) when {@link OutboundDialOptions.ringTimeoutSeconds} is absent. */
export const DEFAULT_RING_TIMEOUT_SECONDS = 30;

// ─────────────────────────────────────────────────────────────────────────────
// ICallSession — live call session
// ─────────────────────────────────────────────────────────────────────────────

/** Handler invoked when a call's lifecycle status changes. */
export type CallStatusChangedHandler = (status: CallStatus) => void;

/**
 * Live call session. The agent talks to this — it doesn't know or care which
 * carrier is on the other side. Audio in / audio out / hang up / transfer /
 * DTMF. Mirrors `ICallSession` (`IAsyncDisposable`).
 */
export interface ICallSession {
  /** Stable carrier-supplied info captured at call start. */
  readonly info: CallInfo;

  /** Current lifecycle status (Active / EndedByCaller / Transferred / ...). */
  readonly status: CallStatus;

  /** Audio frames arriving from the caller. Abort the signal to stop receiving. */
  receiveAudioAsync(signal?: AbortSignal): AsyncIterable<AudioFrame>;

  /** Send an audio frame to the caller. */
  sendAudioAsync(frame: AudioFrame, signal?: AbortSignal): Promise<void>;

  /** DTMF tones the caller is pressing. */
  receiveDtmfAsync(signal?: AbortSignal): AsyncIterable<DtmfEvent>;

  /** Send DTMF tones from the AI side (for navigating other people's menus). */
  sendDtmfAsync(digits: string, signal?: AbortSignal): Promise<void>;

  /**
   * Transfer the call to `targetNumber`. Cold = drop and forget. Warm = park the
   * caller, dial the human, brief them, bridge both.
   */
  transferAsync(
    targetNumber: string,
    mode: import("./primitives.js").TransferMode,
    briefing?: string,
    signal?: AbortSignal,
  ): Promise<void>;

  /** End the call from our side. */
  hangUpAsync(signal?: AbortSignal): Promise<void>;

  /** Subscribe to lifecycle status changes. */
  onStatusChanged(handler: CallStatusChangedHandler): void;
  /** Unsubscribe a previously-registered status-change handler. */
  offStatusChanged(handler: CallStatusChangedHandler): void;

  /** Release resources (mirrors `IAsyncDisposable.DisposeAsync`). */
  disposeAsync(): Promise<void>;
}

// ─────────────────────────────────────────────────────────────────────────────
// ITelephonyCarrier — carrier integration
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Carrier integration — the place where CircleAI talks to a phone-network
 * operator (Twilio, Telnyx, Plivo, or a SIP gateway). Mirrors `ITelephonyCarrier`.
 * Inbound: carrier delivers a call to us → carrier emits {@link ICallSession}
 * via the host's webhook plumbing. Outbound: caller asks us to dial → we call
 * {@link dialAsync}.
 */
export interface ITelephonyCarrier {
  /** Stable carrier id — "twilio" / "telnyx" / "plivo" / "null". */
  readonly carrierId: string;

  /** True when the carrier has the credentials + base addresses it needs. */
  readonly isConfigured: boolean;

  /**
   * Buy a new phone number from this carrier for the given country code
   * (ISO 3166-1 alpha-2, e.g. "ZA"). Caller chooses one of the offered area
   * codes via `areaCode`; pass undefined for "any".
   */
  provisionNumberAsync(
    countryCode: string,
    areaCode?: string,
    signal?: AbortSignal,
  ): Promise<ProvisionedNumber>;

  /**
   * Configure a number we already own to route inbound calls to our
   * host-provided WebSocket endpoint.
   */
  configureInboundWebhookAsync(
    phoneNumber: string,
    inboundWebhook: string,
    signal?: AbortSignal,
  ): Promise<void>;

  /**
   * Place an outbound call. `streamUrl` is where the carrier should stream the
   * live media (WebSocket URL on our host). Returns a session the caller can
   * attach an agent to.
   */
  dialAsync(
    fromNumber: string,
    toNumber: string,
    streamUrl: string,
    options?: OutboundDialOptions,
    signal?: AbortSignal,
  ): Promise<ICallSession>;

  /** List the numbers we own on this carrier. */
  listNumbersAsync(signal?: AbortSignal): Promise<readonly ProvisionedNumber[]>;
}

// ─────────────────────────────────────────────────────────────────────────────
// IInboundCallDispatcher — inbound webhook dispatcher
// ─────────────────────────────────────────────────────────────────────────────

/** Handle returned by a subscription; call {@link dispose} to unsubscribe. Mirrors `IDisposable`. */
export interface ISubscription {
  dispose(): void;
}

/**
 * Inbound webhook dispatcher — the carrier-provided HTTP handler (host wires this
 * into routing) calls into the dispatcher to materialise an {@link ICallSession}
 * the agent can attach to. Mirrors `IInboundCallDispatcher`.
 */
export interface IInboundCallDispatcher {
  /** Stable id of the carrier feeding inbound calls into this dispatcher. */
  readonly carrierId: string;

  /**
   * Subscribe to inbound call sessions. Each new call yields a session the
   * consumer attaches their agent to.
   */
  subscribe(handler: (session: ICallSession) => Promise<void>): ISubscription;
}

// ─────────────────────────────────────────────────────────────────────────────
// IMediaStream — host-supplied media channel (IMediaStream.cs)
// ─────────────────────────────────────────────────────────────────────────────

/**
 * A live media channel for one call. The carrier host's WebSocket handler
 * implements this; the carrier session consumes it. Mirrors `IMediaStream`
 * (`IAsyncDisposable`).
 */
export interface IMediaStream {
  /** The carrier call id + metadata captured at connect. */
  readonly callInfo: CallInfo;

  /** Inbound audio frames from the caller. */
  receiveAudioAsync(signal?: AbortSignal): AsyncIterable<AudioFrame>;

  /** Outbound audio frames to the caller. */
  sendAudioAsync(frame: AudioFrame, signal?: AbortSignal): Promise<void>;

  /** Inbound DTMF events. */
  receiveDtmfAsync(signal?: AbortSignal): AsyncIterable<DtmfEvent>;

  /** Mark the call ended from our side. Closes the WebSocket. */
  endAsync(signal?: AbortSignal): Promise<void>;

  /** Fires when the carrier reports the call status changed. */
  onStatusChanged(handler: CallStatusChangedHandler): void;
  /** Unsubscribe a status-change handler. */
  offStatusChanged(handler: CallStatusChangedHandler): void;

  /** The current lifecycle state. */
  readonly currentStatus: CallStatus;

  /** Release resources (mirrors `IAsyncDisposable.DisposeAsync`). */
  disposeAsync(): Promise<void>;
}

// ─────────────────────────────────────────────────────────────────────────────
// IDtmfSendable — optional carrier-native out-of-band DTMF (IDtmfSendable.cs)
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Optional sister interface a host can layer on its {@link IMediaStream}
 * implementation to support carrier-native out-of-band DTMF (Twilio mark
 * control frame, Telnyx Call Control send_dtmf, Plivo control event). When the
 * media stream doesn't implement this, the session falls back to in-band tones
 * via {@link DtmfToneGenerator}. Mirrors `IDtmfSendable`.
 */
export interface IDtmfSendable {
  sendDtmfAsync(digits: string, signal?: AbortSignal): Promise<void>;
}

/** Runtime type-guard for {@link IDtmfSendable} (mirrors a C# `is IDtmfSendable` check). */
export function isDtmfSendable(value: unknown): value is IDtmfSendable {
  return (
    typeof value === "object" &&
    value !== null &&
    typeof (value as IDtmfSendable).sendDtmfAsync === "function"
  );
}

// ─────────────────────────────────────────────────────────────────────────────
// BriefingSynthesiser — shared TTS delegate (declared in WarmTransferOrchestrator.cs)
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Synthesise text to PCM-16 mono audio. Mirrors the `BriefingSynthesiser`
 * delegate the transfer / filler / handoff / preamble / progress drivers share.
 * Returns the PCM bytes; an empty buffer means "nothing to speak".
 */
export type BriefingSynthesiser = (text: string, signal?: AbortSignal) => Promise<Uint8Array>;
