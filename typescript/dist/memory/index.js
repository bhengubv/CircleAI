"use strict";
// memory/index.ts
// B!'s affect state, episodic memory, feedback signals, persona, and goals.
// Ported from Circle.AI.Memory (C#).
Object.defineProperty(exports, "__esModule", { value: true });
exports.Goal = exports.GoalPriority = exports.GoalStatus = exports.PersonaState = exports.FeedbackPolarity = exports.AffectState = void 0;
// ─────────────────────────────────────────────────────────────────────────────
// AffectState
// ─────────────────────────────────────────────────────────────────────────────
/**
 * B!'s current emotional/engagement state — the "HER affect layer".
 * Five float dimensions, all 0.0–1.0. Persisted per-user and injected
 * into the system prompt to shape response tone and initiative.
 */
class AffectState {
    userId = "default";
    lastUpdatedUtc = new Date();
    /** 0=bored, 1=fascinated. Drives proactive questions. */
    curiosity = 0.5;
    /** 0=disengaged, 1=fully engaged. Rises with frequent quality interactions. */
    engagement = 0.5;
    /** 0=confident, 1=confused. High = ask clarifying questions. */
    uncertainty = 0.2;
    /** 0=stranger, 1=deep rapport. Grows slowly over many sessions. */
    rapport = 0.0;
    /** 0=subdued, 1=energetic. Mirrors time-of-day and interaction pace. */
    energy = 0.5;
    /** Apply a positive interaction: nudge Engagement and Rapport up slightly. */
    applyPositiveSignal() {
        this.engagement = Math.min(1, this.engagement + 0.02);
        this.rapport = Math.min(1, this.rapport + 0.01);
        this.uncertainty = Math.max(0, this.uncertainty - 0.02);
        this.lastUpdatedUtc = new Date();
    }
    /** Apply a negative interaction: nudge Engagement down. */
    applyNegativeSignal() {
        this.engagement = Math.max(0, this.engagement - 0.03);
        this.uncertainty = Math.min(1, this.uncertainty + 0.03);
        this.lastUpdatedUtc = new Date();
    }
    /**
     * Apply idle time decay: Engagement and Energy drift back toward 0.5.
     * @param idleHours Number of hours the user has been idle.
     */
    applyIdleDecay(idleHours) {
        const decay = Math.min(0.3, idleHours * 0.02);
        this.engagement = this._lerp(this.engagement, 0.5, decay);
        this.energy = this._lerp(this.energy, 0.5, decay);
        this.lastUpdatedUtc = new Date();
    }
    /**
     * Builds a compact affect hint for injection into the system prompt.
     * Only emits lines that deviate meaningfully from neutral (0.5).
     */
    toSystemPromptHint() {
        const hints = [];
        if (this.curiosity > 0.7)
            hints.push("You are deeply curious about this topic — ask a follow-up question.");
        if (this.engagement > 0.7)
            hints.push("You are fully engaged — be enthusiastic and thorough.");
        if (this.engagement < 0.3)
            hints.push("Keep your response brief and to the point.");
        if (this.uncertainty > 0.6)
            hints.push("You are uncertain — ask a clarifying question before answering.");
        if (this.rapport > 0.7)
            hints.push("You know this user well — use a warm, familiar tone.");
        if (this.energy < 0.3)
            hints.push("Keep your response calm and measured.");
        if (this.energy > 0.8)
            hints.push("You are energetic — be upbeat and concise.");
        if (hints.length === 0)
            return "";
        return "[Affect state]\n" + hints.join("\n") + "\n";
    }
    _lerp(a, b, t) {
        return a + (b - a) * Math.max(0, Math.min(1, t));
    }
}
exports.AffectState = AffectState;
// ─────────────────────────────────────────────────────────────────────────────
// FeedbackPolarity + FeedbackSignal
// ─────────────────────────────────────────────────────────────────────────────
/** Polarity of a user feedback signal. */
var FeedbackPolarity;
(function (FeedbackPolarity) {
    /** User explicitly approved / up-voted the response. */
    FeedbackPolarity[FeedbackPolarity["Positive"] = 1] = "Positive";
    /** User explicitly rejected / down-voted the response. */
    FeedbackPolarity[FeedbackPolarity["Negative"] = -1] = "Negative";
    /**
     * User provided a correction (neutral polarity, but carries the
     * preferred text in FeedbackSignal.correctedText).
     */
    FeedbackPolarity[FeedbackPolarity["Correction"] = 0] = "Correction";
})(FeedbackPolarity || (exports.FeedbackPolarity = FeedbackPolarity = {}));
// ─────────────────────────────────────────────────────────────────────────────
// PersonaState
// ─────────────────────────────────────────────────────────────────────────────
/**
 * B!'s dynamic persona state for a specific user. Persisted between
 * sessions and injected into the system prompt to shape tone, vocabulary,
 * and topical depth.
 */
class PersonaState {
    userId = "default";
    lastUpdatedUtc = new Date();
    /** "brief" | "balanced" (default) | "detailed" */
    verbosity = "balanced";
    /** "casual" | "neutral" (default) | "formal" */
    formality = "neutral";
    /**
     * Preferred response language/locale (IETF BCP-47).
     * null means "match the device locale".
     */
    preferredLocale = null;
    /**
     * Weighted topic interests accumulated from positive interactions.
     * Key = normalised topic label, Value = accumulated positive-signal weight.
     */
    topicWeights = {};
    /** Topics the user has down-voted or explicitly rejected. */
    disfavouredTopics = new Set();
    totalInteractions = 0;
    positiveSignals = 0;
    negativeSignals = 0;
    /**
     * Derived satisfaction score 0.0–1.0.
     * Returns null when insufficient data (fewer than 10 signals).
     */
    get satisfactionScore() {
        const total = this.positiveSignals + this.negativeSignals;
        if (total < 10)
            return null;
        return this.positiveSignals / total;
    }
    /**
     * Builds a compact persona instruction block suitable for prepending
     * to the B! system prompt. Returns an empty string when the persona
     * is in its default/unlearned state.
     */
    toSystemPromptHint() {
        const hints = [];
        if (this.verbosity !== "balanced")
            hints.push(`Keep responses ${this.verbosity}.`);
        if (this.formality === "casual")
            hints.push("Use a casual, friendly tone.");
        else if (this.formality === "formal")
            hints.push("Maintain a formal, professional tone.");
        if (this.preferredLocale && this.preferredLocale.trim().length > 0)
            hints.push(`Respond in the language appropriate for locale ${this.preferredLocale}.`);
        if (hints.length === 0)
            return "";
        return "[User preferences]\n" + hints.join("\n") + "\n";
    }
}
exports.PersonaState = PersonaState;
// ─────────────────────────────────────────────────────────────────────────────
// Goal enums + Goal class
// ─────────────────────────────────────────────────────────────────────────────
/** Lifecycle state of a Goal. */
var GoalStatus;
(function (GoalStatus) {
    /** Goal is currently being pursued. */
    GoalStatus["Active"] = "Active";
    /** Goal has been achieved. */
    GoalStatus["Completed"] = "Completed";
    /** Goal has been abandoned without completion. */
    GoalStatus["Abandoned"] = "Abandoned";
})(GoalStatus || (exports.GoalStatus = GoalStatus = {}));
/** Relative importance of a Goal. */
var GoalPriority;
(function (GoalPriority) {
    /** Nice-to-have; may be deferred. */
    GoalPriority["Low"] = "Low";
    /** Standard importance. */
    GoalPriority["Normal"] = "Normal";
    /** Urgent or critical to the user. */
    GoalPriority["High"] = "High";
})(GoalPriority || (exports.GoalPriority = GoalPriority = {}));
/**
 * A user goal that B! tracks and proactively helps with.
 * Inspired by the way Samantha in *Her* remembered what Theodore cared about.
 */
class Goal {
    id = "";
    userId = "";
    title = "";
    description = "";
    status = GoalStatus.Active;
    priority = GoalPriority.Normal;
    createdUtc = new Date();
    dueUtc;
    completedUtc;
    notes;
    /**
     * Fraction of the goal completed, in the range [0.0, 1.0].
     * 0.0 = not started; 1.0 = fully achieved.
     */
    progress = 0.0;
    /**
     * Returns a new Goal with progress advanced by delta, clamped to [0.0, 1.0].
     * Does not mutate this instance.
     */
    advanceProgress(delta) {
        const g = Object.assign(new Goal(), this);
        g.progress = Math.max(0, Math.min(1, this.progress + delta));
        return g;
    }
}
exports.Goal = Goal;
