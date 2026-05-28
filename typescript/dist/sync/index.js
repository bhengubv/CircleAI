"use strict";
// sync/index.ts
// Cross-device state synchronisation: SyncDelta, ISyncChannel, SyncDomainKeys.
// Ported from Circle.AI.Networking + Circle.AI.Sync (C#).
Object.defineProperty(exports, "__esModule", { value: true });
exports.SyncDomainKeys = exports.SyncDeliveryMode = void 0;
// ─────────────────────────────────────────────────────────────────────────────
// SyncDeliveryMode enum
// ─────────────────────────────────────────────────────────────────────────────
/** Delivery semantics for a SyncDelta. */
var SyncDeliveryMode;
(function (SyncDeliveryMode) {
    /** Fire-and-forget. No retries. Acceptable for non-critical state. */
    SyncDeliveryMode["BEST_EFFORT"] = "BestEffort";
    /** Retry until acknowledged. Required for episodic memory and persona. */
    SyncDeliveryMode["GUARANTEED"] = "Guaranteed";
    /** Bypass batching windows; deliver as fast as transport allows. */
    SyncDeliveryMode["URGENT"] = "Urgent";
})(SyncDeliveryMode || (exports.SyncDeliveryMode = SyncDeliveryMode = {}));
// ─────────────────────────────────────────────────────────────────────────────
// SyncDomainKeys
// ─────────────────────────────────────────────────────────────────────────────
/**
 * Well-known domain keys for SyncDelta.domainKey.
 */
exports.SyncDomainKeys = {
    EPISODIC_MEMORY: "memory.episodic",
    AFFECT_STATE: "affect.state",
    PERSONA: "persona",
    GOALS: "goals",
    SKILLS: "skills",
    PREFERENCES: "preferences",
};
