"use strict";
// companion.ts
//
// Circle AI Companion layer.
// The Companion is the HER + JARVIS persona — available on every surface,
// with memory and identity that travels with the person.
Object.defineProperty(exports, "__esModule", { value: true });
exports.CONFUSION_THRESHOLD = exports.FACE_AFFECT_CONFIDENCE_THRESHOLD = exports.ICompanionSession = exports.InterfaceKind = void 0;
exports.applyFaceToAffect = applyFaceToAffect;
exports.observeFace = observeFace;
const tools_1 = require("./tools");
// ---------------------------------------------------------------------------
// Enumerations
// ---------------------------------------------------------------------------
/**
 * The surface on which the Companion session is running.
 * Determines sensory capabilities, available UI affordances, and
 * how the Companion adapts its communication style.
 */
var InterfaceKind;
(function (InterfaceKind) {
    /** Mobile phone or tablet (MAUI). */
    InterfaceKind["Mobile"] = "Mobile";
    /** Smartwatch or fitness band with a small display. */
    InterfaceKind["Wearable"] = "Wearable";
    /** Desktop or laptop computer (MAUI or WPF). */
    InterfaceKind["Desktop"] = "Desktop";
    /** Browser-based experience (Blazor). */
    InterfaceKind["Web"] = "Web";
    /** Embedded IoT device — voice in, voice out, minimal compute. */
    InterfaceKind["IoT"] = "IoT";
    /** Always-on ambient surface — smart speaker, room display, car. */
    InterfaceKind["Ambient"] = "Ambient";
    /** Programmatic / background / testing context (no UI). */
    InterfaceKind["Headless"] = "Headless";
})(InterfaceKind || (exports.InterfaceKind = InterfaceKind = {}));
// ---------------------------------------------------------------------------
// ICompanionSession
// ---------------------------------------------------------------------------
/**
 * A Companion conversation session. Combines identity awareness, cross-device
 * memory, language adaptation, affect sensing, and proactive reasoning into a
 * single coherent interface.
 */
class ICompanionSession {
}
exports.ICompanionSession = ICompanionSession;
// ---------------------------------------------------------------------------
// FaceAffectMapper
// ---------------------------------------------------------------------------
/**
 * Minimum confidence score for a face detection to be used as an affect signal.
 * Detections below this threshold are silently discarded.
 */
exports.FACE_AFFECT_CONFIDENCE_THRESHOLD = 0.5;
/**
 * Maps a FacialMetricMatrix expression observation to mutations of AffectState.
 * Mutates affect in place. No-op when confidence < 0.5 or expression is
 * NEUTRAL or UNKNOWN.
 *
 * Mapping table (validated against fixtures/facex_biometric_vectors.json):
 *   Happy     → engagement += 0.03, energy     += 0.02
 *   Surprised → curiosity  += 0.04
 *   Confused  → uncertainty += 0.05
 *   Stressed  → uncertainty += 0.08, energy    -= 0.05
 *   Angry     → engagement -= 0.04, rapport    -= 0.02
 *   Neutral   → no change
 *   Unknown   → no change
 */
function applyFaceToAffect(matrix, affect) {
    if (matrix.confidenceScore < exports.FACE_AFFECT_CONFIDENCE_THRESHOLD)
        return;
    switch (matrix.expression) {
        case tools_1.FaceExpressionClassification.HAPPY:
            affect.engagement = Math.min(1, affect.engagement + 0.03);
            affect.energy = Math.min(1, affect.energy + 0.02);
            break;
        case tools_1.FaceExpressionClassification.SURPRISED:
            affect.curiosity = Math.min(1, affect.curiosity + 0.04);
            break;
        case tools_1.FaceExpressionClassification.CONFUSED:
            affect.uncertainty = Math.min(1, affect.uncertainty + 0.05);
            break;
        case tools_1.FaceExpressionClassification.STRESSED:
            affect.uncertainty = Math.min(1, affect.uncertainty + 0.08);
            affect.energy = Math.max(0, affect.energy - 0.05);
            break;
        case tools_1.FaceExpressionClassification.ANGRY:
            affect.engagement = Math.max(0, affect.engagement - 0.04);
            affect.rapport = Math.max(0, affect.rapport - 0.02);
            break;
        case tools_1.FaceExpressionClassification.NEUTRAL:
        case tools_1.FaceExpressionClassification.UNKNOWN:
        default:
            return;
    }
    affect.lastUpdatedAt = new Date();
}
// ---------------------------------------------------------------------------
// FaceCompanionBridge
// ---------------------------------------------------------------------------
/**
 * AffectState.uncertainty level at or above which a proactive companion message
 * is triggered, provided the observed expression is also CONFUSED or STRESSED.
 */
exports.CONFUSION_THRESHOLD = 0.70;
/**
 * Apply a face observation to the affect state and optionally surface
 * a proactive companion event.
 * Returns null when no threshold is crossed.
 */
function observeFace(matrix, affect, sessionId, identityId, surface) {
    applyFaceToAffect(matrix, affect);
    const isConfused = affect.uncertainty >= exports.CONFUSION_THRESHOLD &&
        (matrix.expression === tools_1.FaceExpressionClassification.CONFUSED ||
            matrix.expression === tools_1.FaceExpressionClassification.STRESSED);
    if (!isConfused)
        return null;
    return {
        sessionId,
        identityId,
        interface: surface,
        message: 'I notice you might be finding this a bit tricky. ' +
            'Would you like me to slow down or explain it differently?',
        triggerName: 'face.confusion_detected',
        generatedAt: new Date(),
    };
}
