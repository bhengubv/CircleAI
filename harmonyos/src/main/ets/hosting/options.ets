/**
 * hosting/options
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// hosting/options.ts
//
// Port of CircleAI.Hosting.AIOptions — the configuration bag for the long-lived
// butler service. C# is a sealed class with `init` properties + safe defaults;
// TS models it as a readonly interface plus DEFAULT_AI_OPTIONS and the
// generateRandomToken() helper (AIOptions.GenerateRandomToken).

import { randomBytes } from "../platform";
import type { IChatGenerator, ChatCapability } from "../inference/index";
import { ChatCapability as ChatCapabilityValues } from "../inference/index";
import type { GenerationOptions } from "../inference/index";
import type { IDeviceContext } from "../device/index";
import type { ModelScopeCatalogClient } from "../catalog/index";
import type {
  IEpisodicMemoryStore,
  IPersonaStore,
  IFeedbackStore,
  IAffectStore,
  IGoalStore,
} from "../memory/index";
import type { RagContextBuilder } from "../memory/rag";
import type { IToolBridge } from "./tool_bridge";
import type { IAIObserver } from "./observers";
import type { INeuronRouter } from "./neuron/router";
import type { IThermalThrottleService } from "./thermal";
import type { IScheduledTaskStore } from "./scheduled";
import type { ISkillStore } from "./skills";
import type { VoiceOptions } from "./voice";

/**
 * Configuration for {@link AIService} and the loopback transport. Every field
 * has a safe default (see {@link DEFAULT_AI_OPTIONS}). Mirrors
 * CircleAI.Hosting.AIOptions.
 */
export interface AIOptions {
  // ── Model ────────────────────────────────────────────────────────────────
  /**
   * Logical model identifier. `null`/undefined tells the SDK to BestFit a
   * model for the live device via the registered IModelSelector.
   */
  readonly modelId?: string | null;
  /** Optional absolute path to a model file — bypasses the loader registry. */
  readonly modelPath?: string | null;

  // ── Inference ──────────────────────────────────────────────────────────────
  /** System prompt prepended when a conversation carries no system message. */
  readonly systemPrompt?: string;
  /** Default sampling knobs applied when a caller passes none. */
  readonly defaultGenerationOptions?: GenerationOptions | null;
  /** Max context window in tokens. null → derive from device tier. */
  readonly contextSize?: number | null;
  /** CPU decode threads. null → inference layer default. */
  readonly threadCount?: number | null;
  /** Run a 1-token warm-up generation after loading. Default true. */
  readonly warmOnStart?: boolean;

  // ── Tools ────────────────────────────────────────────────────────────────
  /** Optional bridge for tool calls. null → InvokeTool returns a failure. */
  readonly toolBridge?: IToolBridge | null;

  // ── Observers ────────────────────────────────────────────────────────────
  /** Optional observer receiving lifecycle + inference events. */
  readonly observer?: IAIObserver | null;

  // ── v2.0 Sensorium ─────────────────────────────────────────────────────────
  /** Platform-provided device context snapshot injected into the prompt. */
  readonly deviceContext?: IDeviceContext | null;
  /** Optional ModelScope catalog client. */
  readonly catalogClient?: ModelScopeCatalogClient | null;
  /** Capabilities the chat model must declare. Default ChatCapability.Default. */
  readonly requiredCapabilities?: ChatCapability | number;
  /** Run CheckForUpgrades after model load and fire observer events. Default false. */
  readonly checkForUpgradesOnStart?: boolean;
  /** Directory where downloaded bundles live (for upgrade detection). */
  readonly modelStorageDirectory?: string | null;

  // ── v2.0 Memory / RAG ──────────────────────────────────────────────────────
  /** Episodic memory store enabling storage + RAG retrieval. */
  readonly episodicMemory?: IEpisodicMemoryStore | null;
  /** Pre-configured RAG context builder. null + episodicMemory → recency-only. */
  readonly ragBuilder?: RagContextBuilder | null;
  /** Number of relevant past episodes per call. Default 5. 0 disables RAG. */
  readonly ragTopK?: number;

  // ── v2.0 Persona evolution ─────────────────────────────────────────────────
  /** Per-user persona store. */
  readonly personaStore?: IPersonaStore | null;
  /** User id for load/save. Default "default". */
  readonly personaUserId?: string;

  // ── v2.0 Feedback signals ──────────────────────────────────────────────────
  /** Feedback store for user reactions. */
  readonly feedbackStore?: IFeedbackStore | null;

  // ── v2.0 Agentic loop ──────────────────────────────────────────────────────
  /** Max tool-call/re-prompt iterations. null → derive from device tier. */
  readonly agenticMaxIterations?: number | null;

  // ── HttpLoopbackEndpoint config ────────────────────────────────────────────
  /** TCP port the loopback endpoint binds to. 0 → OS picks. */
  readonly loopbackPort?: number;
  /** Shared-secret token clients send in X-Butler-Token. null → random. */
  readonly loopbackToken?: string | null;

  // ── v2.1 Native runtime ────────────────────────────────────────────────────
  /** Override the native library directory. */
  readonly nativeLibDir?: string | null;

  // ── v2.1 Model management ──────────────────────────────────────────────────
  /** Directory where downloaded model files are stored. */
  readonly modelStorageDir?: string;
  /** Only download models over Wi-Fi/Ethernet. Default true. */
  readonly wifiOnlyModelDownload?: boolean;

  // ── v2.1 Cloud fallback ────────────────────────────────────────────────────
  /** Route to the cloud endpoint when no local model / low RAM. Default false. */
  readonly cloudFallbackEnabled?: boolean;
  /** Base URI of the ButlerAPI cloud endpoint. */
  readonly cloudFallbackEndpoint?: string | null;
  /** Bearer token for the ButlerAPI cloud endpoint. */
  readonly cloudFallbackToken?: string | null;
  /** Minimum RAM in bytes for local inference. Default 2 GB. */
  readonly cloudFallbackRamThresholdBytes?: number;

  // ── v2.1 Thermal management ────────────────────────────────────────────────
  /** Pause inference on Serious/Critical thermal. Default true. */
  readonly thermalPauseEnabled?: boolean;
  /** Optional thermal throttle service. */
  readonly thermalService?: IThermalThrottleService | null;

  // ── v3.0 Voice pipeline ────────────────────────────────────────────────────
  /** Voice pipeline configuration. null → text-only mode. */
  readonly voice?: VoiceOptions | null;

  // ── v3.0 Scheduled tasks ───────────────────────────────────────────────────
  /** Optional scheduled task store. */
  readonly scheduledTaskStore?: IScheduledTaskStore | null;

  // ── v3.0 Skills ────────────────────────────────────────────────────────────
  /** Optional skill store for skill-context injection. */
  readonly skillStore?: ISkillStore | null;
  /** Max skills injected per call. Default 5. */
  readonly skillTopK?: number;

  // ── v3.0 Affect model ──────────────────────────────────────────────────────
  /** Affect state store injected into the system prompt. */
  readonly affectStore?: IAffectStore | null;

  // ── v3.0 Goal tracking ─────────────────────────────────────────────────────
  /** Goal store for proactive help. */
  readonly goalStore?: IGoalStore | null;

  // ── Neuron: concierge two-slot residency ───────────────────────────────────
  /**
   * Per-turn concierge router. null/undefined → single-slot behaviour that is
   * byte-identical to a plain AIService (the generalist answers every turn).
   * A router lets the service hot-load one capability-matched specialist beside
   * the always-warm generalist floor.
   */
  readonly router?: INeuronRouter | null;
  /**
   * Bytes to reserve for the always-warm generalist floor when admitting a
   * specialist. null → derive from the generalist's selection estimate (0 when
   * the model was pinned by path rather than selected).
   */
  readonly generalistReservedBytes?: number | null;
  /**
   * RAM ceiling in bytes for the two-slot admission gate. null → probe the live
   * device at start.
   */
  readonly ramCeilingBytes?: number | null;

  // ── TS-only injection seam ─────────────────────────────────────────────────
  /**
   * Factory producing the in-process {@link IChatGenerator} for a resolved
   * model path. In C# this is the `Func<string, IChatGenerator>?` constructor
   * argument; in the TS port the native QwenTextGenerator is always injected
   * (no native default), so it lives on the options bag for convenience.
   */
  readonly generatorFactory?: ((modelPath: string) => IChatGenerator) | null;
}

/** Default AIOptions — matches the C# `init`-value defaults. */
export const DEFAULT_AI_OPTIONS: Readonly<Required<
  Pick<
    AIOptions,
    | "systemPrompt"
    | "warmOnStart"
    | "requiredCapabilities"
    | "checkForUpgradesOnStart"
    | "ragTopK"
    | "personaUserId"
    | "loopbackPort"
    | "wifiOnlyModelDownload"
    | "cloudFallbackEnabled"
    | "cloudFallbackRamThresholdBytes"
    | "thermalPauseEnabled"
    | "skillTopK"
  >
>> = {
  systemPrompt: "You are B!, a helpful on-device assistant.",
  warmOnStart: true,
  requiredCapabilities: ChatCapabilityValues.Default,
  checkForUpgradesOnStart: false,
  ragTopK: 5,
  personaUserId: "default",
  loopbackPort: 0,
  wifiOnlyModelDownload: true,
  cloudFallbackEnabled: false,
  cloudFallbackRamThresholdBytes: 2 * 1024 * 1024 * 1024,
  thermalPauseEnabled: true,
  skillTopK: 5,
};

/**
 * Generates a cryptographically random 32-byte token, base64-encoded. Used by
 * the loopback endpoint when loopbackToken is null. Mirrors
 * AIOptions.GenerateRandomToken().
 */
export function generateRandomToken(): string {
  return randomBytes(32).toString("base64");
}
