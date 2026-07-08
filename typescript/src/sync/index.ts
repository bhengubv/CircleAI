// sync/index.ts
// Cross-device state synchronisation: SyncDelta, ISyncChannel, SyncDomainKeys.
// Ported from Circle.AI.Networking + Circle.AI.Sync (C#).

// ─────────────────────────────────────────────────────────────────────────────
// SyncDeliveryMode enum
// ─────────────────────────────────────────────────────────────────────────────

/** Delivery semantics for a SyncDelta. */
export enum SyncDeliveryMode {
  /** Fire-and-forget. No retries. Acceptable for non-critical state. */
  BEST_EFFORT = "BestEffort",
  /** Retry until acknowledged. Required for episodic memory and persona. */
  GUARANTEED = "Guaranteed",
  /** Bypass batching windows; deliver as fast as transport allows. */
  URGENT = "Urgent",
}

// ─────────────────────────────────────────────────────────────────────────────
// SchedulingHint
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Advisory scheduling information attached to a SyncDelta by the Circle AI
 * reasoning layer. The Aether transport is free to disregard these hints,
 * but honouring them minimises unnecessary wakeups and battery drain.
 */
export interface SchedulingHint {
  /**
   * Device IDs that are strongly preferred as the first delivery targets.
   * Empty means "no preference".
   */
  readonly preferredPeerIds: readonly string[];
  /**
   * The earliest UTC timestamp at which the transport should attempt delivery.
   * null means "forward immediately".
   */
  readonly suggestedWindowUtc?: Date;
  /**
   * How confident the AI layer is that these hints are accurate. [0.0, 1.0].
   * Below 0.5 = weak advisory; above 0.8 = strong advisory.
   */
  readonly confidenceScore: number;
}

// ─────────────────────────────────────────────────────────────────────────────
// SyncDelta
// ─────────────────────────────────────────────────────────────────────────────

/**
 * An incremental state change that must reach every device owned by ownerId.
 * This is the primitive that makes Circle AI cross-device continuous —
 * HER + JARVIS memory following the person.
 */
export interface SyncDelta {
  /** Identity whose state this belongs to. */
  readonly ownerId: string;
  /** Origin device. */
  readonly sourceDeviceId: string;
  /** Target device. Empty string = broadcast to all owned devices. */
  readonly targetDeviceId: string;
  /** Domain key (e.g. "memory.episodic" | "affect.state" | "persona"). */
  readonly domainKey: string;
  /** Serialised payload bytes. */
  readonly payload: Uint8Array;
  /** Monotonic sequence number per owner+domain. */
  readonly sequence: number;
  readonly deliveryMode: SyncDeliveryMode;
  /** Time-to-live in milliseconds, or undefined for no expiry. */
  readonly ttlMs?: number;
  readonly createdAt: Date;
  /** Optional AI-layer routing advisory. */
  readonly schedulingHint?: SchedulingHint;
}

// ─────────────────────────────────────────────────────────────────────────────
// ISyncChannel
// ─────────────────────────────────────────────────────────────────────────────

/**
 * The cross-device continuity primitive.
 * Pushes memory/state deltas across whatever transport is available:
 * gRPC over 5G, BLE mesh via a neighbour, DTN bundle arriving 6 hours later.
 * App code is identical in every case.
 */
export interface ISyncChannel {
  /**
   * Push a delta. Channel selects transport and handles retries.
   * Resolves when accepted (not necessarily delivered for DTN/LocalStore).
   */
  pushDeltaAsync(delta: SyncDelta): Promise<void>;

  /**
   * Receive deltas for ownerId as they arrive.
   * The generator yields continuously until the caller breaks or disposes.
   */
  receiveDeltasAsync(ownerId: string): AsyncGenerator<SyncDelta>;

  /** Returns the last seen sequence number for ownerId + domainKey. */
  getLastSequenceAsync(ownerId: string, domainKey: string): Promise<number>;
}

// ─────────────────────────────────────────────────────────────────────────────
// SyncDomainKeys
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Well-known domain keys for SyncDelta.domainKey.
 */
export const SyncDomainKeys = {
  EPISODIC_MEMORY: "memory.episodic",
  AFFECT_STATE: "affect.state",
  PERSONA: "persona",
  GOALS: "goals",
  SKILLS: "skills",
  PREFERENCES: "preferences",
} as const;

export type SyncDomainKey = typeof SyncDomainKeys[keyof typeof SyncDomainKeys];

// ─────────────────────────────────────────────────────────────────────────────
// MemorySyncService — push/receive orchestrator (CircleAI.Sync).
// ─────────────────────────────────────────────────────────────────────────────

export {
  MemorySyncService,
  JsonEpisodicDeltaCodec,
} from "./memory_sync_service.js";
export type {
  IMemorySyncService,
  IEpisodicDeltaCodec,
} from "./memory_sync_service.js";
