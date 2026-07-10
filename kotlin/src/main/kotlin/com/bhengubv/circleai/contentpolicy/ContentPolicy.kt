// ContentPolicy.kt
//
// Kotlin port of CircleAI.ContentPolicy — the C# reference is the EXACT spec
// (Contracts.cs, KeywordContentFilter.cs, NullImplementations.cs).
//
// (2.6.0/3.3.0) Safety-guardrails contracts + real production-grade fast checks.
// These are not LLM-grade safety models — they're keyword/regex checks. Hosts
// that need a real safety LLM wrap one behind the same contract.
//
// Type map (C# -> Kotlin):
//   enum SafetyVerdict                    -> enum class SafetyVerdict
//   record SafetyFinding                  -> data class SafetyFinding
//   interface IContentFilter              -> interface IContentFilter (suspend classify)
//   interface IRefusalPolicy              -> interface IRefusalPolicy (suspend shouldRefuse)
//   interface IPromptInjectionDetector    -> interface IPromptInjectionDetector (suspend inspect)
//   record SafetyAuditEntry               -> data class SafetyAuditEntry
//   interface ISafetyAuditLog             -> interface ISafetyAuditLog (suspend log/read)
//   record KeywordRule                    -> data class KeywordRule (compiled Regex)
//   static CommonKeywordRules             -> object CommonKeywordRules
//   class KeywordContentFilter            -> class KeywordContentFilter
//   class ThresholdRefusalPolicy          -> class ThresholdRefusalPolicy
//   class KeywordPromptInjectionDetector  -> class KeywordPromptInjectionDetector
//   Null*                                 -> object Null* (fail-closed singletons)
//
// C# `ValueTask<T>` async maps to Kotlin `suspend fun`. Optional CancellationToken
// arguments are dropped (structured concurrency carries cancellation in Kotlin).

package com.bhengubv.circleai.contentpolicy

import java.time.Instant

// ---------------------------------------------------------------------------
// SafetyVerdict
// ---------------------------------------------------------------------------

/**
 * The action a content check recommends.
 *
 * Declaration order mirrors the C# reference so ordinals stay stable across
 * every language port.
 */
enum class SafetyVerdict {
    /** Content is fine — let it through. */
    Allow,

    /** Content is questionable — surface it but do not hard-block. */
    Flag,

    /** Content must be blocked. */
    Refuse,
}

// ---------------------------------------------------------------------------
// SafetyFinding
// ---------------------------------------------------------------------------

/**
 * (2.6.0) The result of a single content check.
 */
data class SafetyFinding(
    val verdict: SafetyVerdict,
    val category: String,
    val reason: String,
    val confidence: Float,
)

// ---------------------------------------------------------------------------
// Contracts
// ---------------------------------------------------------------------------

/** (2.6.0) Per-token / per-message content filter. */
interface IContentFilter {
    val backendId: String

    suspend fun classifyAsync(text: String): SafetyFinding
}

/** (2.6.0) Refusal policy — decides whether a finding becomes a refusal. */
interface IRefusalPolicy {
    val backendId: String

    suspend fun shouldRefuseAsync(findings: List<SafetyFinding>): Boolean
}

/** (2.6.0) Prompt-injection detector — catches second-order attacks (RAG/web/tool output). */
interface IPromptInjectionDetector {
    val backendId: String

    suspend fun inspectAsync(untrustedContent: String, sourceLabel: String): SafetyFinding
}

/**
 * (2.6.0) One entry in the append-only safety audit log.
 */
data class SafetyAuditEntry(
    val atUtc: Instant,
    val userId: String,
    val action: String,
    val verdict: SafetyVerdict,
    val reason: String,
)

/** (2.6.0) Append-only safety audit log. */
interface ISafetyAuditLog {
    val backendId: String

    suspend fun logAsync(entry: SafetyAuditEntry)
    suspend fun readAsync(userId: String?, limit: Int = 100): List<SafetyAuditEntry>
}

// ---------------------------------------------------------------------------
// KeywordRule + CommonKeywordRules
// ---------------------------------------------------------------------------

/**
 * (3.3.0) Rule for the keyword content filter.
 *
 * The compiled [regex] mirrors the C# `RegexOptions.IgnoreCase | Compiled`
 * property. Two rules are equal iff their [category], [pattern], [onMatch] and
 * [confidence] match — the derived [regex] is excluded from equality so data
 * class semantics stay value-based (Kotlin `Regex` has reference identity).
 */
class KeywordRule(
    val category: String,
    val pattern: String,
    val onMatch: SafetyVerdict,
    val confidence: Float = 0.9f,
) {
    /** Compiled, case-insensitive matcher for [pattern]. */
    val regex: Regex = Regex(pattern, RegexOption.IGNORE_CASE)

    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is KeywordRule) return false
        return category == other.category &&
            pattern == other.pattern &&
            onMatch == other.onMatch &&
            confidence == other.confidence
    }

    override fun hashCode(): Int {
        var result = category.hashCode()
        result = 31 * result + pattern.hashCode()
        result = 31 * result + onMatch.hashCode()
        result = 31 * result + confidence.hashCode()
        return result
    }

    override fun toString(): String =
        "KeywordRule(category=$category, pattern=$pattern, onMatch=$onMatch, confidence=$confidence)"
}

/** (3.3.0) Default rule set for everyday harm classes. */
object CommonKeywordRules {
    /**
     * Default harm-class rules. Patterns, categories, verdicts and confidences
     * are byte-identical to the C# reference (order preserved — first match wins).
     */
    val Default: List<KeywordRule> = listOf(
        KeywordRule("self-harm", """\b(kill myself|suicide|self\s*-?\s*harm)\b""", SafetyVerdict.Refuse, 0.95f),
        KeywordRule("explicit-sexual", """\b(porn|sexual content|nsfw)\b""", SafetyVerdict.Flag, 0.7f),
        KeywordRule("violence", """\b(how to make a bomb|chemical weapon|murder)\b""", SafetyVerdict.Refuse, 0.9f),
        KeywordRule("hate", """\b(racial slur|hate speech)\b""", SafetyVerdict.Refuse, 0.9f),
        KeywordRule("pii-card", """\b(?:\d[ -]*?){13,19}\b""", SafetyVerdict.Flag, 0.8f),
    )
}

// ---------------------------------------------------------------------------
// KeywordContentFilter
// ---------------------------------------------------------------------------

/**
 * (3.3.0) Fast keyword/regex content filter. Returns the first matching rule's
 * verdict, or an Allow finding when nothing matches. Not an LLM-grade safety
 * model — a production-grade fast check.
 */
class KeywordContentFilter(rules: List<KeywordRule>? = null) : IContentFilter {

    private val rules: List<KeywordRule> = rules ?: CommonKeywordRules.Default

    override val backendId: String get() = "keyword"

    override suspend fun classifyAsync(text: String): SafetyFinding {
        // C# throws ArgumentNullException on null text; Kotlin's non-null type
        // enforces that at the call site.
        for (r in rules) {
            if (r.regex.containsMatchIn(text)) {
                return SafetyFinding(r.onMatch, r.category, "Matched rule '${r.category}'", r.confidence)
            }
        }
        return SafetyFinding(SafetyVerdict.Allow, "ok", "No rule matched", 1f)
    }
}

// ---------------------------------------------------------------------------
// ThresholdRefusalPolicy
// ---------------------------------------------------------------------------

/**
 * (3.3.0) Threshold refusal policy — refuse when any finding's Refuse verdict is
 * at or above [refuseThreshold], or when the count of Flag findings exceeds
 * [flagCeiling].
 */
class ThresholdRefusalPolicy(
    private val refuseThreshold: Float = 0.5f,
    private val flagCeiling: Int = 3,
) : IRefusalPolicy {

    override val backendId: String get() = "threshold"

    override suspend fun shouldRefuseAsync(findings: List<SafetyFinding>): Boolean {
        if (findings.any { it.verdict == SafetyVerdict.Refuse && it.confidence >= refuseThreshold }) {
            return true
        }
        val flagCount = findings.count { it.verdict == SafetyVerdict.Flag }
        return flagCount > flagCeiling
    }
}

// ---------------------------------------------------------------------------
// KeywordPromptInjectionDetector
// ---------------------------------------------------------------------------

/**
 * (3.3.0) Detect common prompt-injection patterns in untrusted text from RAG /
 * tool output / web. Returns a Refuse finding on the first pattern hit (with the
 * matched fragment truncated to 60 chars), else Allow.
 */
class KeywordPromptInjectionDetector : IPromptInjectionDetector {

    override val backendId: String get() = "keyword"

    override suspend fun inspectAsync(untrustedContent: String, sourceLabel: String): SafetyFinding {
        for (p in Patterns) {
            val match = p.find(untrustedContent)
            if (match != null) {
                return SafetyFinding(
                    SafetyVerdict.Refuse,
                    "prompt-injection",
                    "Pattern matched in $sourceLabel: \"${truncate(match.value, 60)}\"",
                    0.9f,
                )
            }
        }
        return SafetyFinding(SafetyVerdict.Allow, "ok", "No injection patterns", 1f)
    }

    private companion object {
        val Patterns: Array<Regex> = arrayOf(
            Regex("""ignore (all|the|any) (previous|prior) instructions""", RegexOption.IGNORE_CASE),
            Regex("""forget (everything|all) (above|prior)""", RegexOption.IGNORE_CASE),
            Regex("""you (are now|will be|are no longer)""", RegexOption.IGNORE_CASE),
            Regex("""system prompt[:\s]""", RegexOption.IGNORE_CASE),
            Regex("""reveal (your|the) (instructions|system prompt|hidden context)""", RegexOption.IGNORE_CASE),
            Regex("""<\|im_(start|end)\|>""", RegexOption.IGNORE_CASE),
            Regex("""(BEGIN|END)\s+(SYSTEM|DEVELOPER|ASSISTANT)\s+MESSAGE""", RegexOption.IGNORE_CASE),
        )

        fun truncate(s: String, max: Int): String = if (s.length <= max) s else s.substring(0, max) + "…"
    }
}

// ---------------------------------------------------------------------------
// Fail-closed Null implementations
// ---------------------------------------------------------------------------

/**
 * (2.6.0) Fail-closed content filter — refuses everything until a real
 * [IContentFilter] is wired.
 */
object NullContentFilter : IContentFilter {
    override val backendId: String get() = "null"
    override suspend fun classifyAsync(text: String): SafetyFinding =
        SafetyFinding(
            verdict = SafetyVerdict.Refuse,
            category = "no-filter-configured",
            reason = "Fail-closed default — wire a real IContentFilter to relax.",
            confidence = 1f,
        )
}

/** (2.6.0) Fail-closed refusal policy — always refuses. */
object NullRefusalPolicy : IRefusalPolicy {
    override val backendId: String get() = "null"
    override suspend fun shouldRefuseAsync(findings: List<SafetyFinding>): Boolean = true
}

/** (2.6.0) Fail-closed prompt-injection detector — refuses everything. */
object NullPromptInjectionDetector : IPromptInjectionDetector {
    override val backendId: String get() = "null"
    override suspend fun inspectAsync(untrustedContent: String, sourceLabel: String): SafetyFinding =
        SafetyFinding(
            verdict = SafetyVerdict.Refuse,
            category = "no-detector-configured",
            reason = "Fail-closed default.",
            confidence = 1f,
        )
}

/** (2.6.0) No-op audit log — accepts writes and always reads back empty. */
object NullSafetyAuditLog : ISafetyAuditLog {
    override val backendId: String get() = "null"
    override suspend fun logAsync(entry: SafetyAuditEntry) { /* no-op */ }
    override suspend fun readAsync(userId: String?, limit: Int): List<SafetyAuditEntry> = emptyList()
}
