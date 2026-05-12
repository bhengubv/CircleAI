// sync.ts
//
// Cross-device continuity primitive.
// Pushes memory/state deltas across whatever transport is available:
// gRPC over 5G, BLE mesh via a neighbour, DTN bundle arriving 6 hours later.
// App code is identical in every case.
// This is the primitive that makes Circle AI HER + JARVIS:
// memory follows the person, not the device.

// ---------------------------------------------------------------------------
// Enumerations
// ---------------------------------------------------------------------------

/** How urgently a SyncDelta must be delivered. */
export enum SyncDeliveryMode {
  BestEffort = 'BestEffort',
  Guaranteed = 'Guaranteed',
  Urgent     = 'Urgent',
}

// ---------------------------------------------------------------------------
// Well-known domain key constants
// ---------------------------------------------------------------------------

/**
 * Standard domain keys used when constructing SyncDelta records.
 * Custom keys are allowed — these are the built-in ones.
 */
export const SyncDomainKeys = {
  MemoryEpisodic: 'memory.episodic',
  AffectState:    'affect.state',
  Persona:        'persona',
  Goals:          'goals',
  Feedback:       'feedback',
} as const;

// ---------------------------------------------------------------------------
// SyncDelta
// ---------------------------------------------------------------------------

/**
 * An incremental state change that must reach every device owned by ownerId.
 * This is the primitive that makes Circle AI cross-device continuous —
 * HER + JARVIS memory following the person.
 */
export interface SyncDelta {
  /** Identity whose state this belongs to. */
  readonly ownerId:       string;
  /** Origin device. */
  readonly sourceDeviceId: string;
  /**
   * Target device ID, or "" (empty string) for broadcast to all owned devices.
   */
  readonly targetDeviceId: string;
  /**
   * Domain key: "memory.episodic" | "affect.state" | "persona" | custom.
   */
  readonly domainKey:     string;
  /** Serialised payload (e.g. MessagePack or JSON bytes). */
  readonly payload:       Uint8Array;
  /** Monotonically increasing sequence number per owner+domain. */
  readonly sequence:      number;
  readonly deliveryMode:  SyncDeliveryMode;
  /**
   * Optional time-to-live in milliseconds. null means no expiry.
   */
  readonly ttlMs:         number | null;
  readonly createdAt:     Date;
}

// ---------------------------------------------------------------------------
// ISyncChannel
// ---------------------------------------------------------------------------

/**
 * The cross-device continuity primitive.
 * Pushes memory/state deltas across whatever transport is available.
 */
export abstract class ISyncChannel {
  /**
   * Push a delta. Channel selects transport and handles retries.
   * Returns when accepted (not necessarily delivered for DTN/LocalStore).
   */
  abstract pushDelta(delta: SyncDelta): Promise<void>;

  /**
   * Receive deltas for ownerId as an async stream.
   * The generator yields each incoming SyncDelta until cancelled.
   */
  abstract receiveDeltas(ownerId: string): AsyncGenerator<SyncDelta, void, unknown>;

  /**
   * Returns the last known sequence number for ownerId + domainKey.
   * Returns 0 when no deltas have been seen yet.
   */
  abstract getLastSequence(ownerId: string, domainKey: string): Promise<number>;
}
