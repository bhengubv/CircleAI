// models/index.ts
//
// Core records shared across the CircleAI TypeScript modules. Mirrors
// CircleAI.Inference / CircleAI.Core in the C# port at version 1.5.0.

/**
 * A single message in a chat conversation.
 * Role is one of "system" | "user" | "assistant" | "tool".
 *
 * `imageBytes` is optional raw image bytes (JPEG / PNG / WebP) attached
 * to this turn. Consumed by vision-capable generators; text-only generators
 * ignore it. Matches CircleAI.Inference.ChatMessage.ImageBytes.
 */
export interface ChatMessage {
  readonly role: string;
  readonly content: string;
  readonly imageBytes?: Uint8Array;
}

/**
 * Progress report for a file download operation.
 */
export interface DownloadProgress {
  readonly fileName: string;
  readonly bytesReceived: number;
  readonly totalBytes: number;
  readonly bytesPerSecond: number;
  /** Estimated seconds remaining. */
  readonly estimatedTimeRemaining: number;
}

// ── ChatResponse / FinishReason ───────────────────────────────────────────

/** Why a generation call stopped emitting tokens. */
export enum FinishReason {
  /** Hit a stop sequence — normal completion. */
  Stop = 0,
  /** Hit GenerationOptions.maxTokens. */
  Length = 1,
  /** Cancellation token fired. */
  Cancelled = 2,
  /** Native generation reported an error before a stop sequence. */
  Error = 3,
  /** Generator didn't surface a finish reason; treat as Stop. */
  Unknown = 4,
}

/**
 * Structured response from IChatGenerator.generateResponse.
 * Carries the generated text alongside token counts, latency, and finish
 * reason — the metadata callers need for rate-limiting, billing, telemetry,
 * and trace stitching.
 */
export interface ChatResponse {
  readonly text: string;
  readonly tokensIn: number;
  readonly tokensOut: number;
  readonly latencyMs: number;
  readonly finishReason: FinishReason;
}

// ── BundleFile / InstalledManifest / UpgradeInfo ──────────────────────────

/** One file inside a model bundle. */
export interface BundleFile {
  readonly name: string;
  readonly sha256: string;
  readonly sizeBytes: number;
}

/**
 * On-disk record of what was installed for a given model. Written by the
 * downloader after every successful bundle install. Read by
 * ModelRegistryService.checkForUpgrades to detect drift.
 */
export interface InstalledManifest {
  readonly modelId: string;
  readonly version: string;
  readonly repo: string | null;
  readonly totalBytes: number;
  readonly files: readonly BundleFile[];
  /** ISO 8601 UTC timestamp. */
  readonly installedAtUtc: string;
}

/** Why checkForUpgrades flagged a model. */
export enum UpgradeReason {
  /** Registry's Version string differs from installed. */
  VersionChanged = 0,
  /** One or more file SHAs differ; Version string is identical. */
  ShaChanged = 1,
  /** Both Version and at least one SHA differ — common release case. */
  Both = 2,
  /** No local installed.json found, but directory exists. */
  Unknown = 3,
}

/** One detected upgrade for a locally-installed model. */
export interface UpgradeInfo {
  readonly modelId: string;
  readonly installedVersion: string | null;
  readonly availableVersion: string;
  readonly reason: UpgradeReason;
  readonly estimatedDownloadBytes: number;
  /** ISO 8601 UTC timestamp. */
  readonly detectedAt: string;
}
