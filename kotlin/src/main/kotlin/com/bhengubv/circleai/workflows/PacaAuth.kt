// PacaAuth.kt
//
// Kotlin port of CircleAI.Workflows/PacaAuth.cs.
//
// (3.3.0) Auth primitives ported from paca: JWT (access + refresh) + API-key
// validation. Issuance and verification use HMAC-SHA256. API keys live in an
// in-memory store keyed by hashed prefix.
//
// Self-contained crypto: javax.crypto.Mac (HmacSHA256), MessageDigest (SHA-256),
// SecureRandom, java.util.Base64 URL codec. JSON via kotlinx.serialization.

package com.bhengubv.circleai.workflows

import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.longOrNull
import java.security.MessageDigest
import java.security.SecureRandom
import java.time.Duration
import java.time.Instant
import java.util.Base64
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap
import javax.crypto.Mac
import javax.crypto.spec.SecretKeySpec

/** (3.3.0) Token-shaped JWT result. */
data class JwtPair(
    val accessToken: String,
    val refreshToken: String,
    val accessExpiresAtUtc: Instant,
    val refreshExpiresAtUtc: Instant,
)

/** (3.3.0) Verified JWT payload. */
data class JwtPayload(
    val subject: String,
    val claims: Map<String, String>,
    val expiresAtUtc: Instant,
)

/** (3.3.0) HMAC-SHA256 JWT issuer + verifier. */
class HmacJwtAuthenticator(
    signingSecret: String,
    accessLifetime: Duration? = null,
    refreshLifetime: Duration? = null,
    private val clock: () -> Instant = { Instant.now() },
) {
    private val secret: ByteArray
    private val accessLifetime: Duration
    private val refreshLifetime: Duration

    init {
        require(signingSecret.isNotBlank() && signingSecret.length >= 16) {
            "Signing secret must be at least 16 characters."
        }
        secret = signingSecret.toByteArray(Charsets.UTF_8)
        this.accessLifetime = accessLifetime ?: Duration.ofMinutes(15)
        this.refreshLifetime = refreshLifetime ?: Duration.ofDays(7)
    }

    /** (3.3.0) Issue access + refresh tokens for [subject]. */
    fun issue(subject: String, claims: Map<String, String>? = null): JwtPair {
        require(subject.isNotBlank()) { "subject required" }
        val now = clock()
        val accessExp = now.plus(accessLifetime)
        val refreshExp = now.plus(refreshLifetime)
        val access = encodeToken(subject, "access", accessExp, claims)
        val refresh = encodeToken(subject, "refresh", refreshExp, null)
        return JwtPair(access, refresh, accessExp, refreshExp)
    }

    /** (3.3.0) Verify a token; returns the payload or null if invalid/expired. */
    fun verify(token: String, expectedType: String = "access"): JwtPayload? {
        if (token.isBlank()) return null
        val parts = token.split(".")
        if (parts.size != 3) return null

        val header = parts[0]
        val payload = parts[1]
        val sig = parts[2]
        val signing = "$header.$payload"
        val expected = signBase64Url(signing)
        if (!fixedTimeEquals(expected, sig)) return null

        val json: JsonObject = try {
            JSON.parseToJsonElement(String(base64UrlDecode(payload), Charsets.UTF_8)) as JsonObject
        } catch (_: Exception) {
            return null
        }

        if ((json["typ"] as? JsonPrimitive)?.contentOrNull != expectedType) return null
        val subject = (json["sub"] as? JsonPrimitive)?.contentOrNull ?: return null
        val expSeconds = (json["exp"] as? JsonPrimitive)?.longOrNull ?: return null
        val exp = Instant.ofEpochSecond(expSeconds)
        if (!exp.isAfter(clock())) return null

        val extraClaims = LinkedHashMap<String, String>()
        for ((k, v) in json) {
            if (k == "typ" || k == "sub" || k == "exp") continue
            extraClaims[k] = (v as? JsonPrimitive)?.contentOrNull ?: v.toString()
        }
        return JwtPayload(subject, extraClaims, exp)
    }

    private fun encodeToken(subject: String, type: String, expires: Instant, claims: Map<String, String>?): String {
        val header = """{"alg":"HS256","typ":"JWT"}"""
        val payloadMap = LinkedHashMap<String, Any>()
        payloadMap["sub"] = subject
        payloadMap["typ"] = type
        payloadMap["exp"] = expires.epochSecond
        claims?.forEach { (k, v) -> payloadMap[k] = v }

        val payloadJson = buildJson(payloadMap)
        val headerB = base64UrlEncode(header.toByteArray(Charsets.UTF_8))
        val payloadB = base64UrlEncode(payloadJson.toByteArray(Charsets.UTF_8))
        val signing = "$headerB.$payloadB"
        val sig = signBase64Url(signing)
        return "$signing.$sig"
    }

    private fun signBase64Url(signing: String): String {
        val mac = Mac.getInstance("HmacSHA256")
        mac.init(SecretKeySpec(secret, "HmacSHA256"))
        val sig = mac.doFinal(signing.toByteArray(Charsets.UTF_8))
        return base64UrlEncode(sig)
    }

    companion object {
        private val JSON = Json { ignoreUnknownKeys = true }

        private fun buildJson(map: Map<String, Any>): String {
            val obj = JsonObject(
                map.mapValues { (_, v) ->
                    when (v) {
                        is Number -> JsonPrimitive(v)
                        is Boolean -> JsonPrimitive(v)
                        else -> JsonPrimitive(v.toString())
                    }
                },
            )
            return JSON.encodeToString(JsonObject.serializer(), obj)
        }

        private fun base64UrlEncode(bytes: ByteArray): String =
            Base64.getEncoder().encodeToString(bytes).trimEnd('=').replace('+', '-').replace('/', '_')

        private fun base64UrlDecode(input: String): ByteArray {
            var s = input.replace('-', '+').replace('_', '/')
            when (s.length % 4) {
                2 -> s += "=="
                3 -> s += "="
            }
            return Base64.getDecoder().decode(s)
        }

        private fun fixedTimeEquals(a: String, b: String): Boolean {
            val ba = a.toByteArray(Charsets.UTF_8)
            val bb = b.toByteArray(Charsets.UTF_8)
            if (ba.size != bb.size) return false
            var diff = 0
            for (i in ba.indices) diff = diff or (ba[i].toInt() xor bb[i].toInt())
            return diff == 0
        }
    }
}

/** (3.3.0) Issued API key — store hashes only. */
data class PacaApiKeyRecord(
    val keyId: String,
    val label: String,
    val hashedSecret: String,
    val createdAtUtc: Instant,
    val revokedAtUtc: Instant?,
)

/** (3.3.0) API-key registry separate from JWT user auth. */
class PacaApiKeyAuthenticator(private val clock: () -> Instant = { Instant.now() }) {

    private val keys = ConcurrentHashMap<String, PacaApiKeyRecord>()
    private val random = SecureRandom()

    /**
     * (3.3.0) Generate a fresh key; the raw secret is returned ONCE for the
     * caller to store. Returns (record, rawSecret).
     */
    fun issue(label: String): Pair<PacaApiKeyRecord, String> {
        require(label.isNotBlank()) { "label required" }
        val keyId = UUID.randomUUID().toString().replace("-", "")
        val secretBytes = ByteArray(32).also { random.nextBytes(it) }
        val secret = Base64.getEncoder().encodeToString(secretBytes).trimEnd('=')
        val hashed = hash(secret)
        val record = PacaApiKeyRecord(keyId, label, hashed, clock(), null)
        keys[keyId] = record
        return record to secret
    }

    /** (3.3.0) Verify an incoming key. Returns the record if valid and live. */
    fun verify(keyId: String, presentedSecret: String): PacaApiKeyRecord? {
        val record = keys[keyId] ?: return null
        if (record.revokedAtUtc != null) return null
        val hashed = hash(presentedSecret)
        return if (slowEquals(hashed, record.hashedSecret)) record else null
    }

    /** (3.3.0) Revoke a key. Idempotent. */
    fun revoke(keyId: String) {
        val existing = keys[keyId] ?: return
        if (existing.revokedAtUtc != null) return
        keys[keyId] = existing.copy(revokedAtUtc = clock())
    }

    companion object {
        private fun hash(secret: String): String {
            val digest = MessageDigest.getInstance("SHA-256").digest(secret.toByteArray(Charsets.UTF_8))
            return Base64.getEncoder().encodeToString(digest).trimEnd('=')
        }

        private fun slowEquals(a: String, b: String): Boolean {
            val ba = a.toByteArray(Charsets.UTF_8)
            val bb = b.toByteArray(Charsets.UTF_8)
            if (ba.size != bb.size) return false
            var diff = 0
            for (i in ba.indices) diff = diff or (ba[i].toInt() xor bb[i].toInt())
            return diff == 0
        }
    }
}
