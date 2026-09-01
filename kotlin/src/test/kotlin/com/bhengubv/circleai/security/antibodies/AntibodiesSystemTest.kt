package com.bhengubv.circleai.security.antibodies

import java.time.Instant
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

/** The facade: every path asks the gate first. */
class AntibodiesSystemTest {

    private val now: Instant = Instant.ofEpochSecond(1_782_896_400)

    private fun threat() = DefensiveThreatContext.raise(
        "User was sent an unexpected invoice", DefensiveThreatSeverity.ELEVATED, "user", now)!!

    private fun armedCorpus(): InMemoryIndicatorCorpus {
        val c = InMemoryIndicatorCorpus()
        c.add(AntibodyIndicatorKind.FILE_HASH_SHA256, "abc123",
            ThreatAwarenessVerdict.KNOWN_BAD, "dropper", "Delete it.", "test set")
        c.add(AntibodyIndicatorKind.DOMAIN_NAME, "evil.com",
            ThreatAwarenessVerdict.KNOWN_BAD, "phishing", "Report it.", "test set")
        val hash = IndicatorNormalizer.normalizeIdentityToHash(
            AntibodyIndicatorKind.EMAIL_ADDRESS, "nandi@example.com")!!
        c.add(AntibodyIndicatorKind.EMAIL_ADDRESS, hash,
            ThreatAwarenessVerdict.KNOWN_BAD, "2024 breach", "Rotate it.", "test set")
        return c
    }

    private fun consented(): DefensiveAntibodySystem {
        val store = InMemoryAuthorizedUseConsentStore()
        for (cap in AntibodyCapability.entries) {
            store.record(AuthorizedUseConsent.grant(cap, "Nandi", "this incident", 3600.0, now)!!)
        }
        return DefensiveAntibodySystem.create(
            ExplicitConsentAuthorizedUseGate(store) { now }, armedCorpus()) { now }
    }

    // A build that has not opted in is a VALID build, and it assesses nothing.
    @Test fun `the deny by default system assesses nothing at all`() {
        val s = DefensiveAntibodySystem.createDenyByDefault { now }
        val t = threat()

        val results = listOf(
            s.assessFile(FileArtifact("x.pdf", "abc123", 1), t),
            s.assessNetworkIndicator(NetworkIndicator.forDomain("evil.com")!!, t),
            s.assessOwnIdentityExposure(IdentityIndicator.email("nandi@example.com")!!, t),
        )
        for (r in results) {
            assertEquals(ThreatAwarenessVerdict.NOT_ASSESSED, r.verdict)
            assertFalse(r.wasAuthorized)
            assertEquals("authorized-use gate", r.source)
        }
    }

    // A denial EXPLAINS itself rather than throwing or returning nothing.
    @Test fun `a denial says why and offers a way forward`() {
        val r = DefensiveAntibodySystem.createDenyByDefault { now }
            .assessFile(FileArtifact("x", "abc123", 1), threat())
        assertTrue(r.summary.contains("gate denied it"))
        assertTrue(r.protectiveGuidance.contains("explicitly authorized"))
    }

    // The corpus is armed and the indicator IS known-bad - the only thing
    // stopping the answer is the missing consent. That is the design.
    @Test fun `an armed corpus is still silent without consent`() {
        val s = DefensiveAntibodySystem.create(NullAuthorizedUseGate, armedCorpus()) { now }
        val r = s.assessNetworkIndicator(NetworkIndicator.forDomain("evil.com")!!, threat())
        assertEquals(ThreatAwarenessVerdict.NOT_ASSESSED, r.verdict)
    }

    @Test fun `with consent all three assessments run`() {
        val s = consented()
        val t = threat()
        val results = listOf(
            s.assessFile(FileArtifact("invoice.pdf", "abc123", 9), t),
            s.assessNetworkIndicator(NetworkIndicator.forDomain("www.evil.com")!!, t),
            s.assessOwnIdentityExposure(IdentityIndicator.email("Nandi@Example.com")!!, t),
        )
        for (r in results) {
            assertTrue(r.wasAuthorized)
            assertEquals(ThreatAwarenessVerdict.KNOWN_BAD, r.verdict)
        }
    }

    @Test fun `a clean indicator comes back as no known threat not as safe`() {
        val r = consented().assessNetworkIndicator(
            NetworkIndicator.forDomain("example.org")!!, threat())
        assertTrue(r.wasAuthorized)
        assertEquals(ThreatAwarenessVerdict.NO_KNOWN_THREAT, r.verdict)
        assertTrue(r.summary.contains("not proof of safety"))
    }

    // Consent for the file capability must not let the identity check run.
    @Test fun `each capability is gated separately end to end`() {
        val store = InMemoryAuthorizedUseConsentStore()
        store.record(AuthorizedUseConsent.grant(
            AntibodyCapability.FILE_REPUTATION_AWARENESS, "u", "s", 3600.0, now)!!)
        val s = DefensiveAntibodySystem.create(
            ExplicitConsentAuthorizedUseGate(store) { now }, armedCorpus()) { now }
        val t = threat()

        assertTrue(s.assessFile(FileArtifact("x", "abc123", 1), t).wasAuthorized)
        assertFalse(s.assessOwnIdentityExposure(
            IdentityIndicator.email("nandi@example.com")!!, t).wasAuthorized)
    }

    @Test fun `every result carries guidance whatever the verdict`() {
        val s = consented()
        val t = threat()
        val results = listOf(
            s.assessFile(FileArtifact("a", "abc123", 1), t),
            s.assessFile(FileArtifact("b", "ffff", 1), t),
            s.assessNetworkIndicator(NetworkIndicator.forUrl("https://example.org")!!, t),
            DefensiveAntibodySystem.createDenyByDefault { now }
                .assessFile(FileArtifact("c", "abc123", 1), t),
        )
        for (r in results) {
            assertTrue(r.protectiveGuidance.isNotEmpty())
            assertTrue(r.summary.isNotEmpty())
            assertEquals(now, r.assessedAtUtc)
        }
    }
}
