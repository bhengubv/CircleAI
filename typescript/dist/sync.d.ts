/** How urgently a SyncDelta must be delivered. */
export declare enum SyncDeliveryMode {
    BestEffort = "BestEffort",
    Guaranteed = "Guaranteed",
    Urgent = "Urgent"
}
/**
 * Standard domain keys used when constructing SyncDelta records.
 * Custom keys are allowed — these are the built-in ones.
 */
export declare const SyncDomainKeys: {
    readonly MemoryEpisodic: "memory.episodic";
    readonly AffectState: "affect.state";
    readonly Persona: "persona";
    readonly Goals: "goals";
    readonly Feedback: "feedback";
};
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
    /**
     * Target device ID, or "" (empty string) for broadcast to all owned devices.
     */
    readonly targetDeviceId: string;
    /**
     * Domain key: "memory.episodic" | "affect.state" | "persona" | custom.
     */
    readonly domainKey: string;
    /** Serialised payload (e.g. MessagePack or JSON bytes). */
    readonly payload: Uint8Array;
    /** Monotonically increasing sequence number per owner+domain. */
    readonly sequence: number;
    readonly deliveryMode: SyncDeliveryMode;
    /**
     * Optional time-to-live in milliseconds. null means no expiry.
     */
    readonly ttlMs: number | null;
    readonly createdAt: Date;
}
/**
 * The cross-device continuity primitive.
 * Pushes memory/state deltas across whatever transport is available.
 */
export declare abstract class ISyncChannel {
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
