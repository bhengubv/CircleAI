// RedactedEvidenceTest.kt
//
// Verifies the deterministic evidence redaction contract: values become
// "sha256:<lowercase-hex>", keys are preserved, empty/null -> "sha256:", and
// the JSON serializer emits redacted values while the deserializer returns an
// empty map.

package com.bhengubv.circleai.security

import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json
import org.junit.jupiter.api.Test
import java.security.MessageDigest
import java.nio.charset.StandardCharsets
import kotlin.test.assertEquals
import kotlin.test.assertTrue

class RedactedEvidenceTest {

    @Serializable
    private data class Holder(
        @Serializable(with = RedactedEvidenceJsonConverter::class)
        val evidence: Map<String, String>,
    )

    private fun expectedHash(raw: String): String {
        val h = MessageDigest.getInstance("SHA-256")
            .digest(raw.toByteArray(StandardCharsets.UTF_8))
        return "sha256:" + h.joinToString("") { "%02x".format(it) }
    }

    @Test
    fun `hashRedacted matches raw SHA-256 hex with sha256 prefix`() {
        val raw = "session-token-abc"
        assertEquals(expectedHash(raw), RedactedEvidence.hashRedacted(raw))
    }

    @Test
    fun `hashRedacted of null and empty is bare sha256 prefix`() {
        assertEquals("sha256:", RedactedEvidence.hashRedacted(null))
        assertEquals("sha256:", RedactedEvidence.hashRedacted(""))
    }

    @Test
    fun `hashRedacted output is lowercase hex`() {
        val out = RedactedEvidence.hashRedacted("Value")
        val hexPart = out.removePrefix("sha256:")
        assertEquals(hexPart.lowercase(), hexPart)
        assertEquals(64, hexPart.length, "SHA-256 hex is 64 chars")
    }

    @Test
    fun `hashRedacted is deterministic`() {
        assertEquals(
            RedactedEvidence.hashRedacted("same"),
            RedactedEvidence.hashRedacted("same"),
        )
    }

    @Test
    fun `serialize redacts values but preserves keys`() {
        val holder = Holder(mapOf("ip" to "10.0.0.1", "token" to "secret"))
        val json = Json.encodeToString(Holder.serializer(), holder)

        // Keys present.
        assertTrue(json.contains("\"ip\""))
        assertTrue(json.contains("\"token\""))
        // Raw values absent.
        assertTrue(!json.contains("10.0.0.1"))
        assertTrue(!json.contains("secret"))
        // Redacted values present.
        assertTrue(json.contains(expectedHash("10.0.0.1")))
        assertTrue(json.contains(expectedHash("secret")))
    }

    @Test
    fun `deserialize returns an empty map regardless of input`() {
        val json = """{"evidence":{"ip":"sha256:deadbeef","token":"sha256:cafe"}}"""
        val holder = Json.decodeFromString(Holder.serializer(), json)
        assertTrue(holder.evidence.isEmpty(), "read side must not trust inbound values")
    }
}
