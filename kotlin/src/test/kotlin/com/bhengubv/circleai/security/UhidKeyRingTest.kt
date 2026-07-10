// UhidKeyRingTest.kt
//
// Verifies ephemeral P-256 key-ring semantics: sign/verify round-trip,
// revocation blocks signing but not verification, rotation issues a fresh
// distinct ring, and the exported public key is DER SubjectPublicKeyInfo.

package com.bhengubv.circleai.security

import org.junit.jupiter.api.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNotEquals
import kotlin.test.assertNull
import kotlin.test.assertTrue
import kotlin.test.assertFailsWith
import kotlin.test.assertNotNull

class UhidKeyRingTest {

    @Test
    fun `sign then verify round-trips`() {
        UhidKeyRing.generateFresh("uhid-1").use { ring ->
            val data = "payload".toByteArray()
            val sig = ring.sign(data)
            assertTrue(ring.verify(data, sig))
        }
    }

    @Test
    fun `verify rejects a signature over different data`() {
        UhidKeyRing.generateFresh("uhid-1").use { ring ->
            val sig = ring.sign("original".toByteArray())
            assertFalse(ring.verify("tampered".toByteArray(), sig))
        }
    }

    @Test
    fun `verify returns false for garbage signature bytes`() {
        UhidKeyRing.generateFresh("uhid-1").use { ring ->
            assertFalse(ring.verify("data".toByteArray(), byteArrayOf(0, 1, 2, 3)))
        }
    }

    @Test
    fun `new ring is not revoked and exposes DER public key`() {
        UhidKeyRing.generateFresh("uhid-1").use { ring ->
            assertFalse(ring.isRevoked)
            assertNull(ring.revokedAt)
            assertTrue(ring.publicKeyDer.isNotEmpty())
            // X.509 SubjectPublicKeyInfo DER starts with a SEQUENCE tag (0x30).
            assertEquals(0x30.toByte(), ring.publicKeyDer[0])
        }
    }

    @Test
    fun `revoke blocks signing but still verifies old signatures`() {
        UhidKeyRing.generateFresh("uhid-1").use { ring ->
            val data = "x".toByteArray()
            val sig = ring.sign(data)
            ring.revoke()

            assertTrue(ring.isRevoked)
            assertNotNull(ring.revokedAt)
            assertFailsWith<IllegalStateException> { ring.sign(data) }
            // Historical validation still works post-revocation.
            assertTrue(ring.verify(data, sig))
        }
    }

    @Test
    fun `revoke is idempotent`() {
        UhidKeyRing.generateFresh("uhid-1").use { ring ->
            ring.revoke()
            val first = ring.revokedAt
            ring.revoke()
            assertEquals(first, ring.revokedAt)
        }
    }

    @Test
    fun `Rotate revokes this ring and returns a fresh distinct ring`() {
        val original = UhidKeyRing.generateFresh("uhid-1")
        val rotated = original.Rotate()
        try {
            assertTrue(original.isRevoked)
            assertFalse(rotated.isRevoked)
            assertNotEquals(original.ringId, rotated.ringId)
            assertEquals("uhid-1", rotated.uhidIdentityId)
            // Rotated ring signs; original cannot.
            val sig = rotated.sign("y".toByteArray())
            assertTrue(rotated.verify("y".toByteArray(), sig))
        } finally {
            original.close()
            rotated.close()
        }
    }

    @Test
    fun `cross-ring signatures do not verify`() {
        UhidKeyRing.generateFresh("uhid-1").use { a ->
            UhidKeyRing.generateFresh("uhid-1").use { b ->
                val sig = a.sign("shared".toByteArray())
                assertFalse(b.verify("shared".toByteArray(), sig))
            }
        }
    }

    @Test
    fun `blank identity is rejected`() {
        assertFailsWith<IllegalArgumentException> { UhidKeyRing.generateFresh("   ") }
    }

    @Test
    fun `signing after dispose throws`() {
        val ring = UhidKeyRing.generateFresh("uhid-1")
        ring.close()
        assertFailsWith<IllegalStateException> { ring.sign("z".toByteArray()) }
    }
}
