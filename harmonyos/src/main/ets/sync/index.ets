// sync/index.ets — Cross-device continuity primitives for Circle AI
// ArkTS port: no `any`, numeric enums, explicit types.

/**
 * How urgently a SyncDelta must be delivered.
 */
export enum SyncDeliveryMode {
  Immediate   = 0,
  Batched     = 1,
  BestEffort  = 2,
}

/**
 * Scheduling hint attached to a SyncDelta, allowing the transport layer
 * to choose optimal delivery timing and peer selection.
 */
export interface SchedulingHint {
  preferredPeerIds: string[];
  suggestedWindowAt?: number;   // unix ms, optional
  confidenceScore: number;      // 0.0..1.0
}

/**
 * An incremental state change that must reach every device owned by ownerId.
 * payload is serialised as Uint8Array (e.g. JSON or MessagePack bytes).
 */
export interface SyncDelta {
  domainKey: string;
  entityId: string;
  payload: Uint8Array;
  timestamp: number;               // unix ms
  deliveryMode: SyncDeliveryMode;
  schedulingHint?: SchedulingHint;
}

/**
 * Standard domain key constants.
 * Use these when constructing SyncDelta records.
 */
export const SyncDomainKeys = {
  AFFECT:   'affect',
  EPISODIC: 'episodic',
  PERSONA:  'persona',
  GOALS:    'goals',
  IDENTITY: 'identity',
  FEEDBACK: 'feedback',
} as const;

/**
 * The cross-device continuity primitive.
 * Pushes memory/state deltas across whatever transport is available:
 * gRPC over 5G, BLE mesh, or DTN bundle.
 */
export interface ISyncChannel {
  send(delta: SyncDelta): Promise<void>;
  receive(): AsyncGenerator<SyncDelta>;
}
