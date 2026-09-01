package com.bhengubv.circleai.security.antibodies

import java.time.Instant
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

/** The three assessors. */
class AntibodiesAssessorTest {

    private val now: Instant = Instant.ofEpochSecond(1_782_896_400)
    private fun corpus() = InMemoryIndicatorCorpus()

    @Test fun `a known bad hash is reported with a clear instruction`() {
        val c = corpus()
        c.add(AntibodyIndicatorKind.FILE_HASH_SHA256, "abc123", ThreatAwarenessVerdict.KNOWN_BAD,
            "banking trojan dropper", "Delete it.", "test set")

        val r = FileThreatAwarenessAssessor(c) { now }
            .inspect(FileArtifact("invoice.pdf", "ABC123", 1))
        assertEquals(ThreatAwarenessVerdict.KNOWN_BAD, r.verdict)
        assertTrue(r.protectiveGuidance.contains("Do not open"))
        assertTrue(r.summary.contains("banking trojan dropper"))
    }

    // A clean result is NOT a clean bill of health, and must say so.
    @Test fun `an unknown hash is not called safe`() {
        val r = FileThreatAwarenessAssessor(corpus()) { now }
            .inspect(FileArtifact("cv.docx", "ffff", 1))
        assertEquals(ThreatAwarenessVerdict.NO_KNOWN_THREAT, r.verdict)
        assertTrue(r.summary.contains("not proof of safety"))
    }

    @Test fun `a file with no hash is inconclusive not clean`() {
        val r = FileThreatAwarenessAssessor(corpus()) { now }.inspect(FileArtifact("x", "  ", 0))
        assertEquals(ThreatAwarenessVerdict.INCONCLUSIVE, r.verdict)
    }

    @Test fun `a known bad domain is flagged through its www form`() {
        val c = corpus()
        c.add(AntibodyIndicatorKind.DOMAIN_NAME, "evil.com", ThreatAwarenessVerdict.KNOWN_BAD,
            "phishing", "Report it.", "test set")

        val r = NetworkThreatAwarenessAssessor(c) { now }
            .inspect(NetworkIndicator.forDomain("WWW.Evil.com")!!)
        assertEquals(ThreatAwarenessVerdict.KNOWN_BAD, r.verdict)
        assertTrue(r.protectiveGuidance.contains("Do not connect"))
    }

    @Test fun `a suspicious location is warned about more softly`() {
        val c = corpus()
        c.add(AntibodyIndicatorKind.IP_ADDRESS, "203.0.113.5", ThreatAwarenessVerdict.SUSPICIOUS,
            "seen in a scam campaign", "Verify first.", "test set")

        val r = NetworkThreatAwarenessAssessor(c) { now }
            .inspect(NetworkIndicator.forIp("203.0.113.5")!!)
        assertEquals(ThreatAwarenessVerdict.SUSPICIOUS, r.verdict)
        assertTrue(r.protectiveGuidance.contains("unless you are certain"))
    }

    @Test fun `an exposed address gets rotation guidance`() {
        val c = corpus()
        val hash = IndicatorNormalizer.normalizeIdentityToHash(
            AntibodyIndicatorKind.EMAIL_ADDRESS, "nandi@example.com")!!
        c.add(AntibodyIndicatorKind.EMAIL_ADDRESS, hash, ThreatAwarenessVerdict.KNOWN_BAD,
            "2024 forum breach", "Check your other accounts.", "test set")

        val r = BreachExposureAssessor(c) { now }
            .inspect(IdentityIndicator.email("Nandi@Example.com")!!)
        assertEquals(ThreatAwarenessVerdict.KNOWN_BAD, r.verdict)
        assertTrue(r.protectiveGuidance.contains("Change the password"))
        assertTrue(r.protectiveGuidance.contains("2-factor"))
        assertTrue(r.summary.contains("email address"))
    }

    // Absence of a match is not safety - breaches surface years later.
    @Test fun `an unfound address still gets advice`() {
        val r = BreachExposureAssessor(corpus()) { now }
            .inspect(IdentityIndicator.username("nandi")!!)
        assertEquals(ThreatAwarenessVerdict.NO_KNOWN_THREAT, r.verdict)
        assertTrue(r.protectiveGuidance.contains("New breaches appear over time"))
        assertTrue(r.protectiveGuidance.contains("username"))
    }

    @Test fun `an unreadable identity is inconclusive`() {
        val r = BreachExposureAssessor(corpus()) { now }
            .inspect(IdentityIndicator(AntibodyIndicatorKind.PHONE_NUMBER, "----"))
        assertEquals(ThreatAwarenessVerdict.INCONCLUSIVE, r.verdict)
    }

    // An entry without guidance would produce a warning nobody can act on.
    @Test fun `an entry without guidance is refused`() {
        val c = corpus()
        assertFalse(c.add(AntibodyIndicatorKind.DOMAIN_NAME, "x.com",
            ThreatAwarenessVerdict.KNOWN_BAD, "n", "  ", "s"))
        assertFalse(c.add(AntibodyIndicatorKind.DOMAIN_NAME, " ",
            ThreatAwarenessVerdict.KNOWN_BAD, "n", "g", "s"))
        assertEquals(0, c.count)
    }

    @Test fun `the empty corpus knows nothing`() {
        assertNull(EmptyIndicatorCorpus.lookup(AntibodyIndicatorKind.DOMAIN_NAME, "evil.com"))
    }
}
