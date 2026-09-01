/**
 * hosting/observers
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// hosting/observers.ts
//
// Port of CircleAI.Hosting IAIObserver + event records + BrownoutReason,
// plus the PushAIObserver / AetherAIObserver bridges and their injected
// transport/sender contracts (IPushNotificationSender, ICircleAetherTransport).
//
// C# uses default-interface-methods for the observer no-ops; TS interfaces
// can't carry those, so every method is optional and `AIObserverBase` is the
// no-op base class equivalent. AIService fires each hook defensively.

import type { ChatMessage } from "../models/index";
import type { UpgradeInfo } from "../models/index";
import type { ToolInvocation, ToolResult } from "../tools/index";

// ─────────────────────────────────────────────────────────────────────────────
// Event records (AIChatEvent / AIStreamEvent / AIToolEvent)
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Payload delivered to {@link IAIObserver.onChatCompletedAsync}. Carries the
 * full conversation and the model's reply. Mirrors CircleAI.Hosting.AIChatEvent.
 */
export interface AIChatEvent {
  /** Per-call GUID for end-to-end tracing. */
  readonly correlationId: string;
  /** The input messages passed to the generator. */
  readonly messages: readonly ChatMessage[];
  /** The complete response text. */
  readonly response: string;
  /** Wall-clock elapsed time in milliseconds (C# TimeSpan → ms). */
  readonly elapsedMs: number;
  /** UTC moment the call completed (ISO 8601). */
  readonly timestamp: string;
}

/**
 * Payload delivered to {@link IAIObserver.onStreamStartedAsync} and
 * {@link IAIObserver.onStreamCompletedAsync}. Mirrors
 * CircleAI.Hosting.AIStreamEvent.
 */
export interface AIStreamEvent {
  readonly correlationId: string;
  readonly messages: readonly ChatMessage[];
  /** Time-to-first-token (started) or total generation time (completed), ms. */
  readonly elapsedMs: number;
  /** 0 on started; number of tokens yielded on completed. */
  readonly tokenCount: number;
  readonly timestamp: string;
}

/**
 * Payload delivered to {@link IAIObserver.onToolInvokedAsync}. Mirrors
 * CircleAI.Hosting.AIToolEvent.
 */
export interface AIToolEvent {
  readonly correlationId: string;
  readonly invocation: ToolInvocation;
  readonly result: ToolResult;
  readonly elapsedMs: number;
  readonly timestamp: string;
}

// ─────────────────────────────────────────────────────────────────────────────
// BrownoutReason
// ─────────────────────────────────────────────────────────────────────────────

/**
 * (RT-04) Why a brownout swap fired. Mirrors CircleAI.Hosting.BrownoutReason.
 */
export enum BrownoutReason {
  /** OS-reported memory pressure (Android onTrimMemory critical / iOS warning). */
  MemoryPressure = 0,
  /** Battery dropped below the brownout floor — typically 10 %. */
  BatteryFloor = 1,
  /** Thermal throttle declared the runtime must downshift. */
  ThermalCritical = 2,
  /** Application requested the swap explicitly (e.g. test, manual UI toggle). */
  Manual = 3,
}

// ─────────────────────────────────────────────────────────────────────────────
// IAIObserver
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Observability hook for {@link AIService}. Receives lifecycle and inference
 * events. All methods are optional (default = no-op) and must complete quickly.
 * Mirrors CircleAI.Hosting.IAIObserver — exceptions thrown by observer methods
 * are caught by AIService and never propagate to the caller.
 */
export interface IAIObserver {
  /** Called once after the model has loaded and Butler is ready. */
  onStartedAsync?(): Promise<void>;
  /** Called once when Butler is stopping / being disposed. */
  onStoppedAsync?(): Promise<void>;
  /** Called after a complete (non-streaming) chat response. */
  onChatCompletedAsync?(event: AIChatEvent): Promise<void>;
  /** Called when a streaming response emits its first token (tokenCount 0). */
  onStreamStartedAsync?(event: AIStreamEvent): Promise<void>;
  /** Called after a streaming response has finished (all tokens or cancelled). */
  onStreamCompletedAsync?(event: AIStreamEvent): Promise<void>;
  /** Called after a tool invocation has completed (success or failure). */
  onToolInvokedAsync?(event: AIToolEvent): Promise<void>;
  /**
   * Called once when StartAsync has resolved which model to load — either from
   * AIOptions.modelId directly or via IModelSelector. Fires before the file
   * fetch so observers can surface progress UI.
   */
  onModelFetchingAsync?(modelId: string, autoSelected: boolean): Promise<void>;
  /** Called once per detected upgrade during CheckForUpgrades. */
  onUpgradeAvailableAsync?(upgrade: UpgradeInfo): Promise<void>;
  /**
   * (RT-04) Called when the runtime hot-swaps from one model in the fallback
   * chain to the next under memory pressure.
   */
  onBrownoutAsync?(
    from: string,
    to: string,
    reason: BrownoutReason,
  ): Promise<void>;
}

/** No-op base class. Subclass and override only what you care about. */
export class AIObserverBase implements IAIObserver {
  async onStartedAsync(): Promise<void> {}
  async onStoppedAsync(): Promise<void> {}
  async onChatCompletedAsync(_event: AIChatEvent): Promise<void> {}
  async onStreamStartedAsync(_event: AIStreamEvent): Promise<void> {}
  async onStreamCompletedAsync(_event: AIStreamEvent): Promise<void> {}
  async onToolInvokedAsync(_event: AIToolEvent): Promise<void> {}
  async onModelFetchingAsync(
    _modelId: string,
    _autoSelected: boolean,
  ): Promise<void> {}
  async onUpgradeAvailableAsync(_upgrade: UpgradeInfo): Promise<void> {}
  async onBrownoutAsync(
    _from: string,
    _to: string,
    _reason: BrownoutReason,
  ): Promise<void> {}
}

// ─────────────────────────────────────────────────────────────────────────────
// IPushNotificationSender + PushAIObserver
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Platform-agnostic push notification sender abstraction. Implement with an
 * APN or FCM SDK for real delivery. Mirrors
 * CircleAI.Hosting.IPushNotificationSender.
 */
export interface IPushNotificationSender {
  /** Send a push notification to the device identified by `deviceToken`. */
  sendAsync(deviceToken: string, title: string, body: string): Promise<void>;
}

/**
 * {@link IAIObserver} that delivers butler responses as push notifications
 * via {@link IPushNotificationSender}. Mirrors CircleAI.Hosting.PushAIObserver.
 */
export class PushAIObserver implements IAIObserver {
  private static readonly MaxBodyLength = 100;

  private readonly sender: IPushNotificationSender;
  private readonly deviceToken: string;

  constructor(sender: IPushNotificationSender, deviceToken: string) {
    if (!sender) throw new Error("sender required");
    if (deviceToken == null || deviceToken.trim().length === 0)
      throw new Error("Device token is required.");
    this.sender = sender;
    this.deviceToken = deviceToken;
  }

  async onStartedAsync(): Promise<void> {}
  async onStoppedAsync(): Promise<void> {}

  async onChatCompletedAsync(event: AIChatEvent): Promise<void> {
    this.sendResponse(event.response);
  }

  async onStreamStartedAsync(_event: AIStreamEvent): Promise<void> {}
  async onStreamCompletedAsync(_event: AIStreamEvent): Promise<void> {}
  async onToolInvokedAsync(_event: AIToolEvent): Promise<void> {}

  /**
   * Sends an error push notification. Call from error-handling code that
   * cannot surface through the standard observer lifecycle.
   */
  onError(err: Error): void {
    if (!err) throw new Error("err required");
    const msg = err.message;
    const body =
      msg.length > PushAIObserver.MaxBodyLength
        ? msg.slice(0, PushAIObserver.MaxBodyLength) + "…"
        : msg;
    void this.sender.sendAsync(this.deviceToken, "B! Error", body);
  }

  private sendResponse(fullResponse: string): void {
    const body =
      fullResponse.length > PushAIObserver.MaxBodyLength
        ? fullResponse.slice(0, PushAIObserver.MaxBodyLength) + "…"
        : fullResponse;
    void this.sender.sendAsync(this.deviceToken, "B!", body);
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// ICircleAetherTransport + AetherAIObserver
// ─────────────────────────────────────────────────────────────────────────────

/**
 * (3.3.0) Publish/subscribe transport contract for the CircleAether mesh.
 * Host packages register an implementation and this is the contract the
 * observer publishes butler events through. Mirrors
 * CircleAI.Hosting.ICircleAetherTransport.
 */
export interface ICircleAetherTransport {
  /** Publish a payload to the given topic. */
  publishAsync(topic: string, payload: Uint8Array): Promise<void>;
}

/**
 * {@link IAIObserver} that forwards butler events to a CircleAether mesh
 * transport. Mirrors CircleAI.Hosting.AetherAIObserver.
 */
export class AetherAIObserver implements IAIObserver {
  private readonly transport: ICircleAetherTransport;

  constructor(transport: ICircleAetherTransport) {
    if (!transport) throw new Error("transport required");
    this.transport = transport;
  }

  async onStartedAsync(): Promise<void> {}
  async onStoppedAsync(): Promise<void> {}

  async onChatCompletedAsync(event: AIChatEvent): Promise<void> {
    const payload = utf8Json({ response: event.response });
    // Fire-and-forget — keep the callback non-blocking.
    void this.transport.publishAsync("butler/response", payload);
  }

  async onStreamStartedAsync(_event: AIStreamEvent): Promise<void> {}
  async onStreamCompletedAsync(_event: AIStreamEvent): Promise<void> {}
  async onToolInvokedAsync(_event: AIToolEvent): Promise<void> {}

  /**
   * Publishes an error payload to the `butler/error` topic. Call from
   * error-handling code that cannot surface through the standard lifecycle.
   */
  onError(err: Error): void {
    if (!err) throw new Error("err required");
    const payload = utf8Json({
      error: err.name ?? "Error",
      message: err.message,
    });
    void this.transport.publishAsync("butler/error", payload);
  }
}

/** Serialise a value to UTF-8 JSON bytes (System.Text.Json.SerializeToUtf8Bytes). */
function utf8Json(value: unknown): Uint8Array {
  return new TextEncoder().encode(JSON.stringify(value));
}
