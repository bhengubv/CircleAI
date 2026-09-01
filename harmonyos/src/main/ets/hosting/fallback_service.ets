/**
 * hosting/fallback_service
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

import * as __platform from "../platform";
// hosting/fallback_service.ts
//
// Port of CircleAI.Hosting.FallbackAIService — a transparent IAIService wrapper
// that prefers local inference and silently falls back to the cloud
// AIApiClient when the device can't run the model locally (insufficient RAM or
// local start failure).
//
// C# reads available RAM via GC.GetGCMemoryInfo().TotalAvailableMemoryBytes.
// The TS port injects a RAM probe (default: os.freemem()) so the RAM-threshold
// branch is deterministic under test.

import type { GenerationOptions } from "../inference/index";
import type { ChatMessage } from "../models/index";
import type { UpgradeInfo } from "../models/index";
import type { ToolInvocation, ToolResult } from "../tools/index";
import type { FeedbackSignal } from "../memory/index";
import type { IAIService } from "./service";
import type { AIApiClient } from "./ai_api_client";

const DEFAULT_RAM_THRESHOLD_BYTES = 2 * 1024 * 1024 * 1024;

/** Returns the available RAM in bytes. Injected so tests are deterministic. */
export type RamProbe = () => number;

/**
 * Wraps a local {@link IAIService} with a cloud {@link AIApiClient} fallback.
 * Mirrors CircleAI.Hosting.FallbackAIService.
 */
export class FallbackAIService implements IAIService {
  private readonly local: IAIService;
  private readonly cloud: AIApiClient;
  private readonly ramThresholdBytes: number;
  private readonly ramProbe: RamProbe;
  private active: IAIService | null = null;
  private disposed = false;

  /**
   * @param local Local on-device inference service.
   * @param cloud AIApiClient pointed at ButlerAPI.
   * @param ramThresholdBytes Minimum RAM for local inference (default 2 GB).
   * @param ramProbe RAM probe (default: node os.freemem()).
   */
  constructor(
    local: IAIService,
    cloud: AIApiClient,
    ramThresholdBytes: number = DEFAULT_RAM_THRESHOLD_BYTES,
    ramProbe?: RamProbe,
  ) {
    if (!local) throw new Error("local required");
    if (!cloud) throw new Error("cloud required");
    this.local = local;
    this.cloud = cloud;
    this.ramThresholdBytes = ramThresholdBytes;
    this.ramProbe = ramProbe ?? defaultRamProbe;
  }

  get isReady(): boolean {
    return this.active?.isReady ?? false;
  }

  async startAsync(): Promise<void> {
    const availableRam = this.getAvailableRamBytes();

    if (availableRam >= this.ramThresholdBytes) {
      try {
        await this.local.startAsync();
        this.active = this.local;
        return;
      } catch {
        // Local start failed — fall back to cloud.
      }
    }

    await this.cloud.startAsync();
    this.active = this.cloud;
  }

  async stopAsync(): Promise<void> {
    if (this.active !== null) await this.active.stopAsync();
  }

  askAsync(question: string): Promise<string> {
    return this.requireActive().askAsync(question);
  }

  chatAsync(
    messages: readonly ChatMessage[],
    options?: GenerationOptions | null,
  ): Promise<string> {
    return this.requireActive().chatAsync(messages, options);
  }

  streamAsync(
    messages: readonly ChatMessage[],
    options?: GenerationOptions | null,
  ): AsyncGenerator<string> {
    return this.requireActive().streamAsync(messages, options);
  }

  agenticChatAsync(
    prompt: string,
    options?: GenerationOptions | null,
  ): Promise<string> {
    return this.requireActive().agenticChatAsync(prompt, options);
  }

  invokeToolAsync(invocation: ToolInvocation): Promise<ToolResult> {
    return this.requireActive().invokeToolAsync(invocation);
  }

  submitFeedbackAsync(signal: FeedbackSignal): Promise<void> {
    return this.requireActive().submitFeedbackAsync(signal);
  }

  /** Delegates to the active backend (C# uses the interface default → empty). */
  checkForUpgradesAsync(): Promise<readonly UpgradeInfo[]> {
    return (this.active ?? this.local).checkForUpgradesAsync();
  }

  /** Default prewarm — delegates to the active backend (or startAsync). */
  async prewarmAsync(): Promise<void> {
    if (this.active !== null) return this.active.prewarmAsync();
    await this.startAsync();
  }

  async disposeAsync(): Promise<void> {
    if (this.disposed) return;
    this.disposed = true;
    await this.local.disposeAsync();
    await this.cloud.disposeAsync();
  }

  private requireActive(): IAIService {
    if (this.active === null)
      throw new Error(
        "FallbackAIService has not been started. Call startAsync first.",
      );
    return this.active;
  }

  private getAvailableRamBytes(): number {
    try {
      return this.ramProbe();
    } catch {
      return 0;
    }
  }
}

function defaultRamProbe(): number {
  try {
    // eslint-disable-next-line @typescript-eslint/no-require-imports
    const os = __platform as typeof import("../platform");
    return os.freemem();
  } catch {
    return 0;
  }
}
