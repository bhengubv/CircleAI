// SecurityCheckpointTest.kt
//
// Verifies self-verifying checkpoint semantics: create computes the hash,
// verify() detects tampering, and toString never leaks the payload.

package com.bhengubv.circleai.security

import org.junit.jupiter.api.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue
import kotlin.test.assertFailsWith

class SecurityCheckpointTest {

    @Test
    fun `create computes a 32-byte SHA-256 hash and verify passes`() {
        val cp = SecurityCheckpoint.create("uhid-1", "CircleAI.Memory", "state".toByteArray())
        assertEquals(32, cp.payloadHash.size)
        assertTrue(cp.verify())
    }

    @Test
    fun `verify fails after payload mutation`() {
        val payload = byteArrayOf(1, 2, 3, 4)
        val cp = SecurityCheckpoint.create("uhid-1", "mod", payload)
        assertTrue(cp.verify())
        // Tamper with the underlying payload array.
        payload[0] = 99
        assertFalse(cp.verify(), "mutated payload must fail verification")
    }

    @Test
    fun `identical payloads produce identical hashes`() {
        val a = SecurityCheckpoint.create("uhid-1", "mod", "same".toByteArray())
        val b = SecurityCheckpoint.create("uhid-2", "mod", "same".toByteArray())
        assertTrue(a.payloadHash.contentEquals(b.payloadHash))
    }

    @Test
    fun `each checkpoint gets a fresh id`() {
        val a = SecurityCheckpoint.create("uhid-1", "mod", "x".toByteArray())
        val b = SecurityCheckpoint.create("uhid-1", "mod", "x".toByteArray())
        assertTrue(a.id != b.id)
    }

    @Test
    fun `toString never contains raw payload text`() {
        val secret = "TOP-SECRET-PAYLOAD"
        val cp = SecurityCheckpoint.create("uhid-1", "mod", secret.toByteArray())
        val s = cp.toString()
        assertFalse(s.contains(secret), "toString must not leak the payload")
        assertTrue(s.contains("PayloadBytes=${secret.toByteArray().size}"))
        assertTrue(s.contains("PayloadSha256="))
    }

    @Test
    fun `empty payload verifies and reports empty hash prefix`() {
        val cp = SecurityCheckpoint.create("uhid-1", "mod", ByteArray(0))
        assertTrue(cp.verify())
        // SHA-256 of empty input is 32 bytes, so prefix is present (>= 8 bytes).
        assertTrue(cp.toString().contains("PayloadBytes=0"))
    }

    @Test
    fun `blank identity or module is rejected`() {
        assertFailsWith<IllegalArgumentException> {
            SecurityCheckpoint.create("  ", "mod", ByteArray(0))
        }
        assertFailsWith<IllegalArgumentException> {
            SecurityCheckpoint.create("uhid", "   ", ByteArray(0))
        }
    }
}
