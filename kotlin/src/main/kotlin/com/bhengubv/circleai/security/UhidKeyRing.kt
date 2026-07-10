// UhidKeyRing.kt
//
// Kotlin port of src/CircleAI.Security/UhidKeyRing.cs.
//
// Ephemeral session key management bound to a UHID identity.
//
// Each UHID session gets a fresh P-256 (NIST secp256r1) key pair for ECDSA
// signing. When an anomaly is confirmed the watchdog calls generateFresh() —
// the old key is revoked and a new key ring is issued. All in-flight requests
// signed with the revoked key are rejected.
//
// Uses java.security only — no external dependencies. P-256 (secp256r1 /
// prime256v1) matches the C# ECCurve.NamedCurves.nistP256. PublicKey.encoded is
// X.509 SubjectPublicKeyInfo DER, byte-identical to the C#
// ECDsa.ExportSubjectPublicKeyInfo output for the same key.

package com.bhengubv.circleai.security

import java.security.KeyPair
import java.security.KeyPairGenerator
import java.security.Signature
import java.security.spec.ECGenParameterSpec
import java.time.Instant
import java.util.UUID

/**
 * Ephemeral ECDSA (P-256) session key ring bound to a UHID identity.
 * Generate a fresh ring at session start or on anomaly confirmation.
 * Once revoked, the ring cannot sign; generate a new one.
 *
 * All state transitions are guarded by an internal monitor so concurrent
 * sign / verify / revoke calls are safe.
 */
class UhidKeyRing private constructor(
    /** The UHID identity this ring is bound to. */
    val uhidIdentityId: String,
) : AutoCloseable {

    private val lock = Any()
    private var keyPair: KeyPair? = null
    private var revoked: Boolean = false
    private var disposed: Boolean = false

    /** Unique ring identifier. Changes on every [generateFresh]/[Rotate] call. */
    var ringId: UUID = UUID.randomUUID()
        private set

    /** UTC timestamp when this ring was generated. */
    var generatedAt: Instant = Instant.now()
        private set

    /** UTC timestamp when this ring was revoked, or `null` if still active. */
    var revokedAt: Instant? = null
        private set

    /**
     * The DER-encoded (X.509 SubjectPublicKeyInfo) public key for this ring.
     * Safe to share; corresponds to the private signing key.
     */
    var publicKeyDer: ByteArray = ByteArray(0)
        private set

    /** `true` if this ring has been explicitly revoked. */
    val isRevoked: Boolean
        get() = synchronized(lock) { revoked }

    init {
        require(uhidIdentityId.isNotBlank()) { "uhidIdentityId must not be blank" }
        regenerateKey()
    }

    /**
     * Rotates the ring: revokes the current key and generates a replacement.
     * Returns a NEW [UhidKeyRing] — this instance remains revoked.
     *
     * Prefer this pattern over mutating in place so call sites holding a
     * reference to the old ring cannot accidentally sign with a rotated key.
     */
    @Suppress("FunctionName")
    fun Rotate(): UhidKeyRing {
        revoke()
        return generateFresh(uhidIdentityId)
    }

    /**
     * Signs [data] with the current private key using ECDSA-SHA256.
     *
     * @throws IllegalStateException if disposed or revoked.
     */
    fun sign(data: ByteArray): ByteArray = synchronized(lock) {
        val kp = keyPair
        check(kp != null && !disposed) { "UhidKeyRing $ringId has been disposed." }
        check(!revoked) {
            "UhidKeyRing $ringId has been revoked — call Rotate() to get a fresh ring."
        }
        val signer = Signature.getInstance("SHA256withECDSA")
        signer.initSign(kp.private)
        signer.update(data)
        signer.sign()
    }

    /**
     * Verifies an ECDSA-SHA256 [signature] against [data] using this ring's
     * public key. Works even after revocation (so prior signatures can still be
     * validated).
     */
    fun verify(data: ByteArray, signature: ByteArray): Boolean = synchronized(lock) {
        val kp = keyPair ?: return false
        return try {
            val verifier = Signature.getInstance("SHA256withECDSA")
            verifier.initVerify(kp.public)
            verifier.update(data)
            verifier.verify(signature)
        } catch (_: java.security.SignatureException) {
            // Malformed signature bytes -> not a valid signature.
            false
        }
    }

    /**
     * Revokes this ring. After revocation [sign] throws; [verify] continues to
     * work for historical validation.
     */
    fun revoke() = synchronized(lock) {
        if (revoked) return@synchronized
        revoked = true
        revokedAt = Instant.now()
    }

    private fun regenerateKey() = synchronized(lock) {
        val generator = KeyPairGenerator.getInstance("EC")
        generator.initialize(ECGenParameterSpec("secp256r1"))
        keyPair = generator.generateKeyPair()
        ringId = UUID.randomUUID()
        generatedAt = Instant.now()
        revokedAt = null
        revoked = false
        publicKeyDer = keyPair!!.public.encoded
    }

    override fun close() = synchronized(lock) {
        disposed = true
        keyPair = null
    }

    companion object {
        /**
         * Creates a new [UhidKeyRing] for [uhidIdentityId] with a freshly
         * generated P-256 key pair.
         */
        fun generateFresh(uhidIdentityId: String): UhidKeyRing =
            UhidKeyRing(uhidIdentityId)
    }
}
