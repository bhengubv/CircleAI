// hosting/index.ts
//
// IAIObserver + AIOptions — port of CircleAI.Hosting.

import type { ModelScopeCatalogClient } from "../catalog/index.js";
import type { IDeviceContext } from "../device/index.js";
import { ChatCapability } from "../inference/index.js";
import type {
  ChatResponse,
  UpgradeInfo,
} from "../models/index.js";

// ─────────────────────────────────────────────────────────────────────────────
// IAIObserver
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Observer for AIService lifecycle + inference events.
 * Mirrors CircleAI.Hosting.IAIObserver. All methods are optional; the
 * AIObserverBase no-op class is the practical "default implementation"
 * equivalent of the C# default-interface-method pattern.
 */
export interface IAIObserver {
  onStartedAsync?(): Promise<void>;
  onStoppedAsync?(): Promise<void>;
  onChatCompletedAsync?(response: ChatResponse): Promise<void>;
  onStreamStartedAsync?(modelId: string): Promise<void>;
  onStreamCompletedAsync?(modelId: string, tokenCount: number): Promise<void>;
  onToolInvokedAsync?(toolName: string, success: boolean): Promise<void>;

  onModelFetchingAsync?(modelId: string, autoSelected: boolean): Promise<void>;
  onUpgradeAvailableAsync?(upgrade: UpgradeInfo): Promise<void>;
}

/** No-op base class. Subclass and override only what you care about. */
export class AIObserverBase implements IAIObserver {
  async onStartedAsync(): Promise<void> {}
  async onStoppedAsync(): Promise<void> {}
  async onChatCompletedAsync(_response: ChatResponse): Promise<void> {}
  async onStreamStartedAsync(_modelId: string): Promise<void> {}
  async onStreamCompletedAsync(
    _modelId: string,
    _tokenCount: number,
  ): Promise<void> {}
  async onToolInvokedAsync(_toolName: string, _success: boolean): Promise<void> {}
  async onModelFetchingAsync(
    _modelId: string,
    _autoSelected: boolean,
  ): Promise<void> {}
  async onUpgradeAvailableAsync(_upgrade: UpgradeInfo): Promise<void> {}
}

// ─────────────────────────────────────────────────────────────────────────────
// AIOptions
// ─────────────────────────────────────────────────────────────────────────────

/** Host configuration for AIService. */
export interface AIOptions {
  // Model selection
  /** When null/undefined, the SDK auto-resolves via IModelSelector + DeviceProbe. */
  readonly modelId?: string | null;
  /** Explicit model file path — bypasses the registry. */
  readonly modelPath?: string | null;

  // Inference
  readonly systemPrompt?: string;
  /** When undefined, derived from DeviceTierDefaults.contextWindow(tier). */
  readonly contextSize?: number;
  readonly threadCount?: number;
  readonly warmOnStart?: boolean;

  // Sensorium
  readonly deviceContext?: IDeviceContext;

  // Catalog
  /** When supplied, the registry primes from disk + refreshes per cadence. */
  readonly catalogClient?: ModelScopeCatalogClient;

  /** Capabilities the model must declare. Selector filters by these. */
  readonly requiredCapabilities?: number;

  // Agentic
  /** When undefined, derived from DeviceTierDefaults.agenticMaxIterations(tier). */
  readonly agenticMaxIterations?: number;

  // Observer
  readonly observer?: IAIObserver;

  // Upgrade detection
  /**
   * When true, AIService.start() runs checkForUpgrades after model load
   * and fires observer events per upgrade.
   */
  readonly checkForUpgradesOnStart?: boolean;

  /** Where downloaded bundles live. Required for upgrade detection. */
  readonly modelStorageDirectory?: string;
}

/** Default AIOptions. Everything-null until the host overrides. */
export const DEFAULT_AI_OPTIONS: AIOptions = {
  modelId: null,
  modelPath: null,
  systemPrompt: "You are B!, a helpful on-device assistant.",
  warmOnStart: true,
  requiredCapabilities: ChatCapability.Default,
  checkForUpgradesOnStart: false,
};
