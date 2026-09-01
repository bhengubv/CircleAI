package com.bhengubv.circleai.security.antibodies

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNotEquals
import kotlin.test.assertNull
import kotlin.test.assertTrue

/** Canonical forms - what makes two spellings the same question. */
class AntibodiesNormalizerTest {

    @Test fun `domains lose their www and their case`() {
        assertEquals("evil.com",
            IndicatorNormalizer.normalizeNetwork(AntibodyIndicatorKind.DOMAIN_NAME, "WWW.Evil.COM"))
        assertEquals("evil.com",
            IndicatorNormalizer.normalizeNetwork(AntibodyIndicatorKind.DOMAIN_NAME, "evil.com"))
    }

    // Only DOMAINS lose the prefix - a URL that starts with www is a different
    // string and must stay one.
    @Test fun `a url keeps its www`() {
        assertEquals("https://www.evil.com/a",
            IndicatorNormalizer.normalizeNetwork(AntibodyIndicatorKind.URL, "HTTPS://WWW.Evil.com/a"))
    }

    @Test fun `an empty network value normalises to nothing`() {
        assertNull(IndicatorNormalizer.normalizeNetwork(AntibodyIndicatorKind.DOMAIN_NAME, "   "))
    }

    // An identity is HASHED before it is looked up, so the corpus never holds
    // the address itself.
    @Test fun `an identity is hashed not stored in the clear`() {
        val h = IndicatorNormalizer.normalizeIdentityToHash(
            AntibodyIndicatorKind.EMAIL_ADDRESS, "Nandi@Example.com")!!
        assertEquals(64, h.length)
        assertFalse(h.contains("nandi"))
        assertEquals(h, IndicatorNormalizer.normalizeIdentityToHash(
            AntibodyIndicatorKind.EMAIL_ADDRESS, "nandi@example.com"))
    }

    // Spaces and dashes are how people write numbers and must not change the
    // answer - but the country code must.
    @Test fun `a phone number keeps its leading plus and digits only`() {
        val k = AntibodyIndicatorKind.PHONE_NUMBER
        val a = IndicatorNormalizer.normalizeIdentityToHash(k, "+27 82 555 0142")
        assertEquals(a, IndicatorNormalizer.normalizeIdentityToHash(k, "+27825550142"))
        assertEquals(a, IndicatorNormalizer.normalizeIdentityToHash(k, "+27-82-555-0142"))
        assertNotEquals(a, IndicatorNormalizer.normalizeIdentityToHash(k, "27825550142"))
    }

    @Test fun `a phone number with no digits normalises to nothing`() {
        assertNull(IndicatorNormalizer.normalizeIdentityToHash(
            AntibodyIndicatorKind.PHONE_NUMBER, "----"))
        assertNull(IndicatorNormalizer.normalizeIdentityToHash(
            AntibodyIndicatorKind.EMAIL_ADDRESS, "  "))
    }

    @Test fun `a file artifact hashes its content`() {
        val a = FileArtifact.fromContent("invoice.pdf", "hello".toByteArray())!!
        assertEquals(5L, a.sizeBytes)
        // The published SHA-256 of hello.
        assertEquals("2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824", a.sha256Hex)
    }

    @Test fun `a file artifact needs a name`() {
        assertNull(FileArtifact.fromContent("  ", ByteArray(0)))
    }

    @Test fun `an empty indicator is refused at construction`() {
        assertNull(NetworkIndicator.forUrl("  "))
        assertNull(NetworkIndicator.forDomain(""))
        assertNull(IdentityIndicator.email("  "))
        assertTrue(NetworkIndicator.forIp("203.0.113.5") != null)
    }
}
