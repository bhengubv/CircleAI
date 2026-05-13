// sync.ts
//
// Cross-device continuity primitive — ArkTS port.
// Pushes memory/state deltas across whatever transport is available:
// gRPC over 5G, BLE mesh via a neighbour, DTN bundle arriving 6 hours later.

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
 */
export interface SyncDelta {
  /** Identity whose state this belongs to. */
  readonly ownerId:        string;
  /** Origin device. */
  readonly sourceDeviceId: string;
  /** Target device ID, or "" for broadcast to all owned devices. */
  readonly targetDeviceId: string;
  /** Domain key: "memory.episodic" | "affect.state" | "persona" | custom. */
  readonly domainKey:      string;
  /** Serialised payload (e.g. MessagePack or JSON bytes). */
  readonly payload:        Uint8Array;
  /** Monotonically increasing sequence number per owner+domain. */
  readonly sequence:       number;
  readonly deliveryMode:   SyncDeliveryMode;
  /** Optional time-to-live in milliseconds. null means no expiry. */
  readonly ttlMs:          number | null;
  readonly createdAt:      Date;
}

// ---------------------------------------------------------------------------
// ISyncChannel
// ---------------------------------------------------------------------------

/**
 * The cross-device continuity primitive.
 */
export abstract class ISyncChannel {
  abstract pushDelta(delta: SyncDelta): Promise<void>;
  abstract receiveDeltas(ownerId: string): AsyncGenerator<SyncDelta, void, unknown>;
  abstract getLastSequence(ownerId: string, domainKey: string): Promise<number>;
}
