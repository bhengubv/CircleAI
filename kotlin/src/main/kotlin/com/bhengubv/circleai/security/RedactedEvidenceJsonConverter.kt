// RedactedEvidenceJsonConverter.kt
//
// Kotlin port of src/CircleAI.Security/RedactedEvidenceJsonConverter.cs.
//
// Custom kotlinx.serialization converter for AnomalySignal.evidence. Serialises
// every value as the SHA-256 hex of its UTF-8 bytes instead of the raw content.
// The keys (evidence labels) are preserved so structured log sinks (Seq, Loki,
// OpenSearch) can still join entries by evidence shape, but the raw values —
// which may carry session tokens, payload fragments, or PII — never leave the
// process in clear text.
//
// Read side intentionally reverses to an empty map: incoming JSON cannot be
// trusted to carry the original cleartext, and round-tripping hashes back into
// the map would mask whether the source-of-record is the in-process signal or a
// serialised copy.

package com.bhengubv.circleai.security

import kotlinx.serialization.KSerializer
import kotlinx.serialization.descriptors.SerialDescriptor
import kotlinx.serialization.descriptors.buildClassSerialDescriptor
import kotlinx.serialization.encoding.Decoder
import kotlinx.serialization.encoding.Encoder
import kotlinx.serialization.json.JsonDecoder
import kotlinx.serialization.json.JsonEncoder
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import java.security.MessageDigest
import java.nio.charset.StandardCharsets

/**
 * Serialises an evidence map with every value replaced by
 * `"sha256:" + lowercase-hex(SHA-256(utf8(value)))`.
 *
 * Apply via `@Serializable(with = RedactedEvidenceJsonConverter::class)` on the
 * property, or invoke [RedactedEvidence.hashRedacted] directly.
 *
 * The read side intentionally returns an empty map — see the file header.
 */
object RedactedEvidenceJsonConverter : KSerializer<Map<String, String>> {

    override val descriptor: SerialDescriptor =
        buildClassSerialDescriptor("CircleAI.Security.RedactedEvidence")

    override fun serialize(encoder: Encoder, value: Map<String, String>) {
        require(encoder is JsonEncoder) {
            "RedactedEvidenceJsonConverter only supports JSON encoding."
        }
        val redacted = JsonObject(
            value.entries.associate { (k, v) ->
                k to JsonPrimitive(RedactedEvidence.hashRedacted(v))
            },
        )
        encoder.encodeJsonElement(redacted)
    }

    override fun deserialize(decoder: Decoder): Map<String, String> {
        // Tolerate inbound JSON but never trust the values — return empty.
        require(decoder is JsonDecoder) {
            "RedactedEvidenceJsonConverter only supports JSON decoding."
        }
        decoder.decodeJsonElement() // consume + discard
        return emptyMap()
    }
}

/**
 * Deterministic redaction helper, exposed separately so the hashing contract
 * can be unit-tested without a full serialization round-trip.
 */
object RedactedEvidence {
    /**
     * Returns `"sha256:"` for null/empty input, otherwise
     * `"sha256:" + lowercase-hex(SHA-256(utf8(raw)))`.
     */
    fun hashRedacted(raw: String?): String {
        if (raw.isNullOrEmpty()) return "sha256:"
        val hash = MessageDigest.getInstance("SHA-256")
            .digest(raw.toByteArray(StandardCharsets.UTF_8))
        return "sha256:" + toLowerHex(hash)
    }

    private val HEX = "0123456789abcdef".toCharArray()

    private fun toLowerHex(bytes: ByteArray): String {
        val sb = StringBuilder(bytes.size * 2)
        for (byte in bytes) {
            val v = byte.toInt() and 0xFF
            sb.append(HEX[v ushr 4])
            sb.append(HEX[v and 0x0F])
        }
        return sb.toString()
    }
}
