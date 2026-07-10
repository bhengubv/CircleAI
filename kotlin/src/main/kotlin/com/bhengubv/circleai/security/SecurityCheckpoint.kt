// SecurityCheckpoint.kt
//
// Kotlin port of src/CircleAI.Security/SecurityCheckpoint.cs.
//
// A cryptographically-bound snapshot of trusted local state.
//
// When CircleAI detects an anomaly, the watchdog may roll back to the last
// verified checkpoint. A checkpoint is:
//   - IMMUTABLE once created (data class with private constructor)
//   - SELF-VERIFYING (SHA-256 of payload, verified on restore)
//   - TAGGED with the UHID that created it (identity binding)
//
// The payload is deliberately opaque (ByteArray) so any module can checkpoint
// its own serialised state without this package taking a dependency on it.

package com.bhengubv.circleai.security

import java.security.MessageDigest
import java.time.Instant
import java.util.UUID

/**
 * An immutable, self-verifying snapshot of trusted local state.
 * Created before a risky operation; used for rollback if an [AnomalySignal] is
 * confirmed.
 *
 * @property id Unique checkpoint identifier.
 * @property uhidIdentityId The UHID of the local user whose state is captured.
 *   Binds the checkpoint to a specific identity.
 * @property moduleLabel Label for the module or subsystem that created this
 *   checkpoint (e.g. `"CircleAI.Companion"`, `"CircleAI.Memory"`).
 * @property payload Opaque serialised state payload.
 * @property payloadHash SHA-256 hash of [payload], computed at creation time.
 *   Verified by [verify] before restoring.
 * @property createdAt UTC timestamp of checkpoint creation.
 */
class SecurityCheckpoint private constructor(
    val id: UUID,
    val uhidIdentityId: String,
    val moduleLabel: String,
    val payload: ByteArray,
    val payloadHash: ByteArray,
    val createdAt: Instant,
) {
    /**
     * Verifies that [payload] has not been tampered with since the checkpoint
     * was created.
     *
     * @return `true` if the current SHA-256 of [payload] matches [payloadHash];
     *   `false` if the payload was modified.
     */
    fun verify(): Boolean {
        val current = sha256(payload)
        return fixedTimeEquals(current, payloadHash)
    }

    /**
     * Returns a non-sensitive textual representation of this checkpoint — the
     * payload bytes are NEVER included in clear. Only the first 16 hex chars of
     * [payloadHash] are emitted, sufficient for correlation across logs without
     * leaking content. Overrides the default so structured loggers can't
     * accidentally serialise [payload] through reflection.
     */
    override fun toString(): String {
        val hashPrefix =
            if (payloadHash.size >= 8) toHex(payloadHash.copyOfRange(0, 8)) else "(empty)"
        return "SecurityCheckpoint(Id=$id, Module=$moduleLabel, " +
            "Uhid=$uhidIdentityId, PayloadSha256=$hashPrefix…, " +
            "PayloadBytes=${payload.size}, CreatedAt=$createdAt)"
    }

    companion object {
        /**
         * Creates a new checkpoint, computing [payloadHash] automatically.
         *
         * @throws IllegalArgumentException if [uhidIdentityId] or [moduleLabel]
         *   is blank.
         */
        fun create(
            uhidIdentityId: String,
            moduleLabel: String,
            payload: ByteArray,
        ): SecurityCheckpoint {
            require(uhidIdentityId.isNotBlank()) { "uhidIdentityId must not be blank" }
            require(moduleLabel.isNotBlank()) { "moduleLabel must not be blank" }

            val hash = sha256(payload)
            return SecurityCheckpoint(
                UUID.randomUUID(),
                uhidIdentityId,
                moduleLabel,
                payload,
                hash,
                Instant.now(),
            )
        }

        private fun sha256(data: ByteArray): ByteArray =
            MessageDigest.getInstance("SHA-256").digest(data)

        /**
         * Constant-time comparison of two byte arrays. Mirrors
         * `CryptographicOperations.FixedTimeEquals` — always reads both arrays
         * fully so timing does not leak where the first mismatch occurred.
         */
        private fun fixedTimeEquals(a: ByteArray, b: ByteArray): Boolean =
            MessageDigest.isEqual(a, b)

        private val HEX = "0123456789ABCDEF".toCharArray()

        private fun toHex(bytes: ByteArray): String {
            val sb = StringBuilder(bytes.size * 2)
            for (byte in bytes) {
                val v = byte.toInt() and 0xFF
                sb.append(HEX[v ushr 4])
                sb.append(HEX[v and 0x0F])
            }
            return sb.toString()
        }
    }
}
