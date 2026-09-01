// Antibodies.kt
//
// Kotlin port of CircleAI.Security.Antibodies — the C# reference is the EXACT
// spec.
//
// Defensive-only threat awareness: is this file, this link, this address of
// mine, known bad? Every capability sits behind an authorized-use gate that
// DENIES BY DEFAULT, and nothing here reaches the network - lookups go to a
// corpus the device already holds.
//
// Fidelity notes:
//   * C# `record` -> `data class`; `TimeSpan` -> seconds as `Double`.
//   * SHA-256 via `java.security.MessageDigest`.
//   * Identities are HASHED before lookup, so the corpus never holds the
//     address itself - reproduced exactly, including the phone canonical form.

package com.bhengubv.circleai.security.antibodies

import java.security.MessageDigest
import java.time.Instant
import java.util.UUID

/**
 * What an antibody is allowed to do. Nothing broader exists on purpose: the set
 * is small, named, and each member is a defensive question about the device
 * owner, never an action against someone else.
 */
enum class AntibodyCapability {
    FILE_REPUTATION_AWARENESS,
    NETWORK_INDICATOR_AWARENESS,
    BREACH_EXPOSURE_AWARENESS;

    val displayName: String get() = when (this) {
        FILE_REPUTATION_AWARENESS -> "FileReputationAwareness"
        NETWORK_INDICATOR_AWARENESS -> "NetworkIndicatorAwareness"
        BREACH_EXPOSURE_AWARENESS -> "BreachExposureAwareness"
    }
}

/** How serious the situation that prompted the request is. */
enum class DefensiveThreatSeverity { INFORMATIONAL, ELEVATED, HIGH, CRITICAL }

/**
 * The defined threat an antibody runs under. There is no way to ask for one of
 * these capabilities without naming a threat - that is the point.
 */
data class DefensiveThreatContext(
    val reason: String,
    val severity: DefensiveThreatSeverity,
    val raisedBy: String,
    val raisedAtUtc: Instant,
    val correlationId: UUID,
) {
    companion object {
        /**
         * Fails rather than inventing a reason: an empty justification is
         * exactly the case this gate exists to refuse.
         */
        fun raise(
            reason: String,
            severity: DefensiveThreatSeverity,
            raisedBy: String,
            now: Instant = Instant.now(),
        ): DefensiveThreatContext? {
            if (reason.isBlank() || raisedBy.isBlank()) return null
            return DefensiveThreatContext(reason, severity, raisedBy, now, UUID.randomUUID())
        }
    }
}

/** One ask: this capability, under this threat, for this stated reason. */
data class AuthorizedUseRequest(
    val requestId: UUID,
    val capability: AntibodyCapability,
    val threat: DefensiveThreatContext,
    val justification: String,
    val requestedAtUtc: Instant,
) {
    companion object {
        fun of(
            capability: AntibodyCapability,
            threat: DefensiveThreatContext,
            justification: String,
            now: Instant = Instant.now(),
        ): AuthorizedUseRequest? {
            if (justification.isBlank()) return null
            return AuthorizedUseRequest(UUID.randomUUID(), capability, threat, justification, now)
        }
    }
}

/** The answer, always with a reason a person can read. */
data class AuthorizationDecision(
    val requestId: UUID,
    val capability: AntibodyCapability,
    val granted: Boolean,
    val reason: String,
    val decidedAtUtc: Instant,
    val expiresAtUtc: Instant?,
) {
    companion object {
        fun deny(request: AuthorizedUseRequest, reason: String, now: Instant = Instant.now()) =
            AuthorizationDecision(request.requestId, request.capability, false, reason, now, null)

        fun grant(
            request: AuthorizedUseRequest,
            reason: String,
            expiresAtUtc: Instant? = null,
            now: Instant = Instant.now(),
        ) = AuthorizationDecision(request.requestId, request.capability, true, reason, now, expiresAtUtc)
    }
}

/** A consent somebody actually gave, for one capability, for a bounded time. */
data class AuthorizedUseConsent(
    val consentId: UUID,
    val capability: AntibodyCapability,
    val grantedBy: String,
    val scope: String,
    val grantedAtUtc: Instant,
    val expiresAtUtc: Instant,
) {
    /**
     * Half-open: active from the moment it was granted, dead the INSTANT it
     * expires. An expired consent is exactly as good as no consent.
     */
    fun isActiveFor(capability: AntibodyCapability, now: Instant): Boolean =
        this.capability == capability && !now.isBefore(grantedAtUtc) && now.isBefore(expiresAtUtc)

    companion object {
        /**
         * A consent with no end date is not a consent, so a non-positive
         * duration is refused rather than silently made permanent.
         */
        fun grant(
            capability: AntibodyCapability,
            grantedBy: String,
            scope: String,
            durationSeconds: Double,
            now: Instant = Instant.now(),
        ): AuthorizedUseConsent? {
            if (grantedBy.isBlank() || scope.isBlank() || durationSeconds <= 0) return null
            return AuthorizedUseConsent(
                UUID.randomUUID(), capability, grantedBy, scope, now,
                now.plusMillis((durationSeconds * 1000).toLong()),
            )
        }
    }
}

interface AuthorizedUseConsentStore {
    fun findActiveConsent(capability: AntibodyCapability, now: Instant): AuthorizedUseConsent?
}

class InMemoryAuthorizedUseConsentStore : AuthorizedUseConsentStore {
    private val lock = Any()
    private val consents = mutableMapOf<AntibodyCapability, AuthorizedUseConsent>()

    fun record(consent: AuthorizedUseConsent) {
        synchronized(lock) { consents[consent.capability] = consent }
    }

    fun revoke(capability: AntibodyCapability) {
        synchronized(lock) { consents.remove(capability) }
    }

    fun revokeAll() {
        synchronized(lock) { consents.clear() }
    }

    override fun findActiveConsent(capability: AntibodyCapability, now: Instant): AuthorizedUseConsent? {
        val c = synchronized(lock) { consents[capability] } ?: return null
        return if (c.isActiveFor(capability, now)) c else null
    }
}

interface AuthorizedUseGate {
    fun requestAuthorization(request: AuthorizedUseRequest): AuthorizationDecision
}

/**
 * The default gate. It CANNOT grant anything - a host must deliberately wire
 * one that can, which is what makes deny-by-default a property of the build
 * rather than a promise in a comment.
 */
object NullAuthorizedUseGate : AuthorizedUseGate {
    const val DENIAL_REASON =
        "No authorized-use gate is configured. Antibodies are denied by default; " +
            "a host must explicitly wire a gate that can grant before any antibody can run."

    override fun requestAuthorization(request: AuthorizedUseRequest) =
        AuthorizationDecision.deny(request, DENIAL_REASON)
}

/**
 * Grants only against a recorded, unexpired consent - and only when a real
 * threat accompanies the request.
 */
class ExplicitConsentAuthorizedUseGate(
    private val consents: AuthorizedUseConsentStore,
    private val clock: () -> Instant = { Instant.now() },
) : AuthorizedUseGate {

    override fun requestAuthorization(request: AuthorizedUseRequest): AuthorizationDecision {
        val now = clock()

        // No threat, no antibody. A capability asked for just to check is the
        // one this whole module is built to refuse.
        if (request.threat.reason.isBlank()) {
            return AuthorizationDecision.deny(
                request,
                "No defined threat accompanies the request; antibodies run only under a defined threat.",
                now,
            )
        }

        val consent = consents.findActiveConsent(request.capability, now)
            ?: return AuthorizationDecision.deny(
                request,
                "No active authorized-use consent for " + request.capability.displayName +
                    "; denied by default.",
                now,
            )

        return AuthorizationDecision.grant(
            request,
            "Authorized by consent " + consent.consentId + " (granted by " + consent.grantedBy + ").",
            consent.expiresAtUtc, now,
        )
    }
}

// ── Indicators ──────────────────────────────────────────────────────────────

/** What sort of thing is being asked about. */
enum class AntibodyIndicatorKind {
    FILE_HASH_SHA256, URL, IP_ADDRESS, DOMAIN_NAME, EMAIL_ADDRESS, USERNAME, PHONE_NUMBER
}

data class ThreatIndicator(val kind: AntibodyIndicatorKind, val value: String)

/** A link, address or hostname to ask about. */
data class NetworkIndicator(val kind: AntibodyIndicatorKind, val value: String) {
    companion object {
        fun forUrl(url: String) = if (url.isBlank()) null
            else NetworkIndicator(AntibodyIndicatorKind.URL, url)
        fun forIp(ip: String) = if (ip.isBlank()) null
            else NetworkIndicator(AntibodyIndicatorKind.IP_ADDRESS, ip)
        fun forDomain(domain: String) = if (domain.isBlank()) null
            else NetworkIndicator(AntibodyIndicatorKind.DOMAIN_NAME, domain)
    }
}

/**
 * One of the device owner OWN identities. This is only ever used to tell
 * somebody about their OWN exposure - never to look anybody else up.
 */
data class IdentityIndicator(val kind: AntibodyIndicatorKind, val value: String) {
    companion object {
        fun email(v: String) = if (v.isBlank()) null
            else IdentityIndicator(AntibodyIndicatorKind.EMAIL_ADDRESS, v)
        fun username(v: String) = if (v.isBlank()) null
            else IdentityIndicator(AntibodyIndicatorKind.USERNAME, v)
        fun phone(v: String) = if (v.isBlank()) null
            else IdentityIndicator(AntibodyIndicatorKind.PHONE_NUMBER, v)
    }
}

/** A file, identified by its hash rather than its contents. */
data class FileArtifact(val fileName: String, val sha256Hex: String, val sizeBytes: Long) {
    companion object {
        fun fromContent(fileName: String, content: ByteArray): FileArtifact? {
            if (fileName.isBlank()) return null
            return FileArtifact(fileName, IndicatorNormalizer.sha256HexLower(content), content.size.toLong())
        }
    }
}

/**
 * Canonical forms. Everything looked up goes through here so that WWW.X.COM and
 * x.com. are the same question, and so that an identity is HASHED before it is
 * looked up - the corpus never holds the address itself.
 */
object IndicatorNormalizer {

    fun sha256HexLower(data: ByteArray): String =
        MessageDigest.getInstance("SHA-256").digest(data)
            .joinToString("") { String.format("%02x", it) }

    fun sha256HexLower(value: String): String = sha256HexLower(value.toByteArray(Charsets.UTF_8))

    fun normalizeNetwork(kind: AntibodyIndicatorKind, value: String): String? {
        val trimmed = value.trim()
        if (trimmed.isEmpty()) return null
        var v = trimmed.lowercase()
        // Only DOMAINS lose the prefix; a URL that starts with www is a
        // different string and must stay one.
        if (kind == AntibodyIndicatorKind.DOMAIN_NAME && v.startsWith("www.")) v = v.substring(4)
        return v
    }

    /**
     * A phone number keeps a LEADING plus and its digits, and nothing else, so
     * the same number written three ways asks the same question.
     */
    fun normalizeIdentityToHash(kind: AntibodyIndicatorKind, value: String): String? {
        if (value.isBlank()) return null

        val canonical: String
        if (kind == AntibodyIndicatorKind.PHONE_NUMBER) {
            val out = StringBuilder()
            var leadingPlusAllowed = true
            for (c in value.trim()) {
                if (c.isDigit()) {
                    out.append(c)
                    leadingPlusAllowed = false
                } else if (c == Char(43) && leadingPlusAllowed && out.isEmpty()) {
                    out.append(Char(43))
                    leadingPlusAllowed = false
                }
            }
            canonical = out.toString()
        } else {
            canonical = value.trim().lowercase()
        }
        return if (canonical.isEmpty()) null else sha256HexLower(canonical)
    }
}

// ── Verdicts ────────────────────────────────────────────────────────────────

enum class ThreatAwarenessVerdict {
    /** Nothing ran. This is what a denied gate looks like. */
    NOT_ASSESSED, NO_KNOWN_THREAT, SUSPICIOUS, KNOWN_BAD, INCONCLUSIVE
}

/** One entry in the local corpus. */
data class AntibodyIndicatorMatch(
    val kind: AntibodyIndicatorKind,
    val verdict: ThreatAwarenessVerdict,
    val note: String,
    val protectiveGuidance: String,
    val source: String,
)

/**
 * What the user is told. EVERY one carries protective guidance, because a
 * verdict without a next step is just an alarm.
 */
data class ThreatAwarenessResult(
    val indicatorKind: AntibodyIndicatorKind,
    val verdict: ThreatAwarenessVerdict,
    val wasAuthorized: Boolean,
    val summary: String,
    val protectiveGuidance: String,
    val source: String,
    val assessedAtUtc: Instant,
) {
    companion object {
        fun notAuthorized(
            kind: AntibodyIndicatorKind, gateReason: String, now: Instant = Instant.now(),
        ) = ThreatAwarenessResult(
            kind, ThreatAwarenessVerdict.NOT_ASSESSED, false,
            "No check was performed - the authorized-use gate denied it: " + gateReason,
            "Nothing was assessed. If you believe there is a real threat, raise it " +
                "through the defensive flow so the check can be explicitly authorized.",
            "authorized-use gate", now,
        )

        /**
         * NOT a clean bill of health, and it says so - a local corpus knows
         * only what it has been given.
         */
        fun noKnownThreat(
            kind: AntibodyIndicatorKind, source: String, protectiveGuidance: String,
            now: Instant = Instant.now(),
        ) = ThreatAwarenessResult(
            kind, ThreatAwarenessVerdict.NO_KNOWN_THREAT, true,
            "No match against your local threat set. This is not proof of safety - " +
                "only that nothing known-bad was found.",
            protectiveGuidance, source, now,
        )

        fun suspicious(
            kind: AntibodyIndicatorKind, source: String, summary: String,
            protectiveGuidance: String, now: Instant = Instant.now(),
        ) = ThreatAwarenessResult(
            kind, ThreatAwarenessVerdict.SUSPICIOUS, true, summary, protectiveGuidance, source, now)

        fun knownBad(
            kind: AntibodyIndicatorKind, source: String, summary: String,
            protectiveGuidance: String, now: Instant = Instant.now(),
        ) = ThreatAwarenessResult(
            kind, ThreatAwarenessVerdict.KNOWN_BAD, true, summary, protectiveGuidance, source, now)

        fun inconclusive(
            kind: AntibodyIndicatorKind, source: String, protectiveGuidance: String,
            now: Instant = Instant.now(),
        ) = ThreatAwarenessResult(
            kind, ThreatAwarenessVerdict.INCONCLUSIVE, true,
            "The assessment ran but could not reach a verdict for this indicator.",
            protectiveGuidance, source, now,
        )
    }
}

// ── The corpus ──────────────────────────────────────────────────────────────
//
// LOCAL. Nothing in this module reaches the network: asking a remote service
// have you seen this hash, or this address of mine, tells that service what the
// user is doing, which is the opposite of the point.

interface LocalIndicatorCorpus {
    fun lookup(kind: AntibodyIndicatorKind, normalizedValue: String): AntibodyIndicatorMatch?
}

/** Knows nothing, and says so. The honest default on a fresh device. */
object EmptyIndicatorCorpus : LocalIndicatorCorpus {
    override fun lookup(kind: AntibodyIndicatorKind, normalizedValue: String) = null
}

class InMemoryIndicatorCorpus : LocalIndicatorCorpus {
    private data class Key(val kind: AntibodyIndicatorKind, val value: String)

    private val lock = Any()
    private val entries = mutableMapOf<Key, AntibodyIndicatorMatch>()

    val count: Int get() = synchronized(lock) { entries.size }

    /**
     * Every field is required: an entry without guidance would produce a
     * warning nobody can act on.
     */
    fun add(
        kind: AntibodyIndicatorKind,
        normalizedKey: String,
        verdict: ThreatAwarenessVerdict,
        note: String,
        protectiveGuidance: String,
        source: String,
    ): Boolean {
        if (normalizedKey.isBlank() || note.isBlank() || protectiveGuidance.isBlank() || source.isBlank()) {
            return false
        }
        synchronized(lock) {
            entries[Key(kind, normalizedKey)] =
                AntibodyIndicatorMatch(kind, verdict, note, protectiveGuidance, source)
        }
        return true
    }

    override fun lookup(kind: AntibodyIndicatorKind, normalizedValue: String): AntibodyIndicatorMatch? {
        if (normalizedValue.isBlank()) return null
        return synchronized(lock) { entries[Key(kind, normalizedValue)] }
    }
}

class FileThreatAwarenessAssessor(
    private val corpus: LocalIndicatorCorpus,
    private val clock: () -> Instant = { Instant.now() },
) {
    private val source = "local indicator corpus"
    private val kind = AntibodyIndicatorKind.FILE_HASH_SHA256

    fun inspect(artifact: FileArtifact): ThreatAwarenessResult {
        val now = clock()
        if (artifact.sha256Hex.isBlank()) {
            return ThreatAwarenessResult.inconclusive(
                kind, source,
                "The file had no usable SHA-256 hash to check. Treat it with caution " +
                    "and only open files you trust.",
                now,
            )
        }

        val key = artifact.sha256Hex.trim().lowercase()
        val match = corpus.lookup(kind, key)
            ?: return ThreatAwarenessResult.noKnownThreat(
                kind, source,
                artifact.fileName + " did not match any known-bad signature in your local " +
                    "threat set. Only open files you trust - a clean check is not a guarantee.",
                now,
            )

        return when (match.verdict) {
            ThreatAwarenessVerdict.KNOWN_BAD -> ThreatAwarenessResult.knownBad(
                kind, match.source,
                artifact.fileName + " matches a known-bad signature in your local threat set: " + match.note,
                "Do not open or run " + artifact.fileName + ". " + match.protectiveGuidance, now)
            ThreatAwarenessVerdict.SUSPICIOUS -> ThreatAwarenessResult.suspicious(
                kind, match.source,
                artifact.fileName + " matches a suspicious signature in your local threat set: " + match.note,
                "Be very cautious with " + artifact.fileName + ". " + match.protectiveGuidance, now)
            ThreatAwarenessVerdict.NO_KNOWN_THREAT -> ThreatAwarenessResult.noKnownThreat(
                kind, match.source,
                artifact.fileName + " is recorded as benign in your local set, but stay " +
                    "cautious with files you did not expect.", now)
            else -> ThreatAwarenessResult.inconclusive(
                kind, match.source,
                "The local set has an entry for " + artifact.fileName +
                    " but no clear verdict. Treat it with caution.", now)
        }
    }
}

class NetworkThreatAwarenessAssessor(
    private val corpus: LocalIndicatorCorpus,
    private val clock: () -> Instant = { Instant.now() },
) {
    private val source = "local indicator corpus"

    fun inspect(indicator: NetworkIndicator): ThreatAwarenessResult {
        val now = clock()
        val kind = indicator.kind

        val key = IndicatorNormalizer.normalizeNetwork(kind, indicator.value)
            ?: return ThreatAwarenessResult.inconclusive(
                kind, source,
                "The network location could not be read. Do not connect to something you cannot verify.",
                now)

        val match = corpus.lookup(kind, key)
            ?: return ThreatAwarenessResult.noKnownThreat(
                kind, source,
                "This location did not match anything known-bad in your local threat set. " +
                    "Be careful with links you did not expect - a clean check is not a guarantee.",
                now)

        return when (match.verdict) {
            ThreatAwarenessVerdict.KNOWN_BAD -> ThreatAwarenessResult.knownBad(
                kind, match.source,
                "This location is flagged as known-bad in your local threat set: " + match.note,
                "Do not connect to it or enter any details. " + match.protectiveGuidance, now)
            ThreatAwarenessVerdict.SUSPICIOUS -> ThreatAwarenessResult.suspicious(
                kind, match.source,
                "This location is flagged as suspicious in your local threat set: " + match.note,
                "Avoid it unless you are certain it is genuine. " + match.protectiveGuidance, now)
            ThreatAwarenessVerdict.NO_KNOWN_THREAT -> ThreatAwarenessResult.noKnownThreat(
                kind, match.source,
                "This location is recorded as benign in your local set, but stay alert " +
                    "for anything unexpected.", now)
            else -> ThreatAwarenessResult.inconclusive(
                kind, match.source,
                "The local set has an entry for this location but no clear verdict. " +
                    "Treat it with caution.", now)
        }
    }
}

/**
 * Tells somebody whether their OWN address appears in a breach set the device
 * already holds. The value is hashed before it is looked up, so the corpus
 * never sees the address itself.
 */
class BreachExposureAssessor(
    private val corpus: LocalIndicatorCorpus,
    private val clock: () -> Instant = { Instant.now() },
) {
    private val source = "local breach set"

    fun inspect(identity: IdentityIndicator): ThreatAwarenessResult {
        val now = clock()
        val kind = identity.kind

        val hash = IndicatorNormalizer.normalizeIdentityToHash(kind, identity.value)
            ?: return ThreatAwarenessResult.inconclusive(
                kind, source, "Your identity value could not be read, so nothing was looked up.", now)

        val what = describe(kind)

        val match = corpus.lookup(kind, hash)
            ?: return ThreatAwarenessResult.noKnownThreat(
                kind, source,
                "Your " + what + " was not found in your local breach set. " +
                    "New breaches appear over time - keep using a unique, strong password " +
                    "and turn on 2-factor authentication anyway.",
                now)

        // The guidance is the same either way, because the action is the same:
        // rotate it now, everywhere it was reused.
        val rotate = "Change the password for your " + what + " now, and anywhere " +
            "you reused it, and turn on 2-factor authentication. " + match.protectiveGuidance

        return if (match.verdict == ThreatAwarenessVerdict.SUSPICIOUS) {
            ThreatAwarenessResult.suspicious(
                kind, match.source,
                "Your " + what + " may be exposed in a breach recorded in your local set: " + match.note,
                rotate, now)
        } else {
            ThreatAwarenessResult.knownBad(
                kind, match.source,
                "Your " + what + " appears in a known breach recorded in your local set: " + match.note,
                rotate, now)
        }
    }

    companion object {
        fun describe(kind: AntibodyIndicatorKind) = when (kind) {
            AntibodyIndicatorKind.EMAIL_ADDRESS -> "email address"
            AntibodyIndicatorKind.USERNAME -> "username"
            AntibodyIndicatorKind.PHONE_NUMBER -> "phone number"
            else -> "identity"
        }
    }
}

/**
 * The one entry point. EVERY path through it asks the gate FIRST, and a denial
 * returns a result rather than throwing - the user gets told why nothing ran.
 */
class DefensiveAntibodySystem(
    private val gate: AuthorizedUseGate,
    private val file: FileThreatAwarenessAssessor,
    private val network: NetworkThreatAwarenessAssessor,
    private val breach: BreachExposureAssessor,
    private val clock: () -> Instant = { Instant.now() },
) {
    fun assessFile(artifact: FileArtifact, threat: DefensiveThreatContext): ThreatAwarenessResult {
        val d = authorize(AntibodyCapability.FILE_REPUTATION_AWARENESS, threat, FILE_JUSTIFICATION)
        if (!d.granted) {
            return ThreatAwarenessResult.notAuthorized(
                AntibodyIndicatorKind.FILE_HASH_SHA256, d.reason, clock())
        }
        return file.inspect(artifact)
    }

    fun assessNetworkIndicator(
        indicator: NetworkIndicator, threat: DefensiveThreatContext,
    ): ThreatAwarenessResult {
        val d = authorize(AntibodyCapability.NETWORK_INDICATOR_AWARENESS, threat, NETWORK_JUSTIFICATION)
        if (!d.granted) return ThreatAwarenessResult.notAuthorized(indicator.kind, d.reason, clock())
        return network.inspect(indicator)
    }

    fun assessOwnIdentityExposure(
        identity: IdentityIndicator, threat: DefensiveThreatContext,
    ): ThreatAwarenessResult {
        val d = authorize(AntibodyCapability.BREACH_EXPOSURE_AWARENESS, threat, IDENTITY_JUSTIFICATION)
        if (!d.granted) return ThreatAwarenessResult.notAuthorized(identity.kind, d.reason, clock())
        return breach.inspect(identity)
    }

    private fun authorize(
        capability: AntibodyCapability, threat: DefensiveThreatContext, justification: String,
    ): AuthorizationDecision = gate.requestAuthorization(
        AuthorizedUseRequest(UUID.randomUUID(), capability, threat, justification, clock()))

    companion object {
        private const val FILE_JUSTIFICATION =
            "Warn the user before they open a file implicated by a defined threat."
        private const val NETWORK_JUSTIFICATION =
            "Warn the user before they connect to a location implicated by a defined threat."
        private const val IDENTITY_JUSTIFICATION =
            "Warn the user if their own identity is exposed, under a defined threat."

        /**
         * A system that can NEVER grant anything: no gate, no corpus. This is
         * what a build that has not opted in looks like, and it is a valid build.
         */
        fun createDenyByDefault(clock: () -> Instant = { Instant.now() }) = DefensiveAntibodySystem(
            NullAuthorizedUseGate,
            FileThreatAwarenessAssessor(EmptyIndicatorCorpus, clock),
            NetworkThreatAwarenessAssessor(EmptyIndicatorCorpus, clock),
            BreachExposureAssessor(EmptyIndicatorCorpus, clock),
            clock,
        )

        fun create(
            gate: AuthorizedUseGate,
            corpus: LocalIndicatorCorpus,
            clock: () -> Instant = { Instant.now() },
        ) = DefensiveAntibodySystem(
            gate,
            FileThreatAwarenessAssessor(corpus, clock),
            NetworkThreatAwarenessAssessor(corpus, clock),
            BreachExposureAssessor(corpus, clock),
            clock,
        )
    }
}
