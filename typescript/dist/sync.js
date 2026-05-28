"use strict";
// sync.ts
//
// Cross-device continuity primitive.
// Pushes memory/state deltas across whatever transport is available:
// gRPC over 5G, BLE mesh via a neighbour, DTN bundle arriving 6 hours later.
// App code is identical in every case.
// This is the primitive that makes Circle AI HER + JARVIS:
// memory follows the person, not the device.
Object.defineProperty(exports, "__esModule", { value: true });
exports.ISyncChannel = exports.SyncDomainKeys = exports.SyncDeliveryMode = void 0;
// ---------------------------------------------------------------------------
// Enumerations
// ---------------------------------------------------------------------------
/** How urgently a SyncDelta must be delivered. */
var SyncDeliveryMode;
(function (SyncDeliveryMode) {
    SyncDeliveryMode["BestEffort"] = "BestEffort";
    SyncDeliveryMode["Guaranteed"] = "Guaranteed";
    SyncDeliveryMode["Urgent"] = "Urgent";
})(SyncDeliveryMode || (exports.SyncDeliveryMode = SyncDeliveryMode = {}));
// ---------------------------------------------------------------------------
// Well-known domain key constants
// ---------------------------------------------------------------------------
/**
 * Standard domain keys used when constructing SyncDelta records.
 * Custom keys are allowed — these are the built-in ones.
 */
exports.SyncDomainKeys = {
    MemoryEpisodic: 'memory.episodic',
    AffectState: 'affect.state',
    Persona: 'persona',
    Goals: 'goals',
    Feedback: 'feedback',
};
// ---------------------------------------------------------------------------
// ISyncChannel
// ---------------------------------------------------------------------------
/**
 * The cross-device continuity primitive.
 * Pushes memory/state deltas across whatever transport is available.
 */
class ISyncChannel {
}
exports.ISyncChannel = ISyncChannel;
