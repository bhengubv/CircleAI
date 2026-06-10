// models_v15.ts — 1.5.0 portable-surface extensions.
//
// Kept separate from models.ts so the 1.0–1.4 ChatMessage/DownloadProgress
// shape stays byte-stable for callers that don't need vision/upgrades.

/** Why a chat generation stopped. */
export enum FinishReason {
  Stop = 0,
  MaxTokens = 1,
  StopSequence = 2,
  Cancelled = 3,
  Error = 4,
  Unknown = 5,
}

/** Structured generation result. */
export interface ChatResponse {
  readonly text: string;
  readonly finishReason: FinishReason;
  /** Optional tokens-generated count. null when the generator can't report. */
  readonly tokensGenerated: number | null;
}

/** One file inside a model bundle, with its expected hash. */
export interface BundleFile {
  /** Relative path inside the model directory (e.g. "llm.mnn"). */
  readonly name: string;
  /** Lowercase hex SHA-256 of the file's bytes. */
  readonly sha256: string;
  /** File size in bytes. */
  readonly sizeBytes: number;
}

/**
 * Manifest written to disk after a successful model install. Lives at
 * `<storage>/<modelId>/installed.json`.
 */
export interface InstalledManifest {
  readonly modelId: string;
  readonly version: string;
  readonly repo: string | null;
  readonly totalBytes: number;
  readonly files: ReadonlyArray<BundleFile>;
  /** ISO-8601 string in UTC. */
  readonly installedAtUtc: string;
}

/** Why an installed model is considered out of date. */
export enum UpgradeReason {
  /** Model dir exists but no installed.json — can't tell what's there. */
  Unknown = 0,
  /** Manifest version differs from catalog version. */
  VersionChanged = 1,
  /** At least one bundle file's SHA differs from catalog. */
  ShaChanged = 2,
  /** Both version and SHA differ. */
  Both = 3,
}

/** A single upgrade detection result. */
export interface UpgradeInfo {
  readonly modelId: string;
  /** null when the installed manifest is missing (Unknown reason). */
  readonly installedVersion: string | null;
  readonly availableVersion: string;
  readonly reason: UpgradeReason;
  /**
   * Sum of `BundleFile.sizeBytes` for files that actually drifted.
   * 0 for `VersionChanged` (no SHAs differ), total catalog bytes for `Unknown`.
   */
  readonly estimatedDownloadBytes: number;
  /** ISO-8601 string in UTC. */
  readonly detectedAt: string;
}

/** Multimodal extension to ChatMessage. */
export interface VisionChatMessage {
  readonly role: string;
  readonly content: string;
  /** Optional raw image bytes (PNG/JPEG/etc.) for vision-capable models. */
  readonly imageBytes: Uint8Array | null;
}
