// ServerAuth.kt
//
// Kotlin port of CircleAI.Inference.Server.Auth (ApiKeyAuthHandler.cs,
// AuthSchemes.cs) + the Options tree (InferenceServerOptions.cs). C# is the
// EXACT spec.
//
// The C# handler is an ASP.NET AuthenticationHandler. Per the port rules we do
// not stand up a real HTTP server: the handler is exposed behind an interface
// that takes the request headers and returns an AuthResult. The security-
// critical logic — constant-time key comparison, anonymous-when-disabled, and
// the NoResult (missing header) vs Fail (wrong key) distinction — ports 1:1.

package com.bhengubv.circleai.server

// ── AuthSchemes ──────────────────────────────────────────────────────────────

/** Identifiers for the auth schemes the server registers (ports AuthSchemes). */
object AuthSchemes {
    /** API-key auth scheme name. */
    const val API_KEY = "ApiKey"

    /** JWT Bearer auth scheme name. */
    const val JWT = "Bearer"

    /** Policy name for endpoints requiring an authenticated caller. */
    const val AUTHENTICATED_POLICY = "Authenticated"
}

// ── Options tree ─────────────────────────────────────────────────────────────

/** API-key auth configuration (ports ApiKeyOptions). */
data class ApiKeyOptions(
    /** When `true`, requests without a valid API key are rejected. */
    val enabled: Boolean = true,
    /** HTTP header carrying the API key. */
    val headerName: String = "X-CircleAI-Api-Key",
    /** Allow-listed keys. */
    val keys: List<String> = emptyList(),
)

/** JWT-bearer auth configuration (ports JwtOptions). */
data class JwtOptions(
    val enabled: Boolean = false,
    val issuer: String = "",
    val audience: String = "",
    val signingKey: String = "",
)

/** Auth subtree (ports AuthOptions). */
data class AuthOptions(
    val apiKey: ApiKeyOptions = ApiKeyOptions(),
    val jwt: JwtOptions = JwtOptions(),
)

/** Root configuration for the inference server (ports InferenceServerOptions). */
data class InferenceServerOptions(
    val runtimeCacheRoot: String = "%LOCALAPPDATA%/CircleAI/runtime",
    val modelStorageRoot: String = "%LOCALAPPDATA%/CircleAI/models",
    val maxConcurrentRequests: Int = 16,
    val requestTimeoutSeconds: Int = 120,
    val auth: AuthOptions = AuthOptions(),
) {
    companion object {
        /** Top-level config section name. */
        const val SECTION_NAME = "CircleAIServer"
    }
}

// ── Authentication result ────────────────────────────────────────────────────

/**
 * Outcome of an authentication attempt, mirroring ASP.NET's
 * `AuthenticateResult` trichotomy:
 *   • [Success] — a principal was established.
 *   • [NoResult] — this scheme had nothing to say (no header present).
 *   • [Fail] — the credential was present but invalid.
 */
sealed interface AuthResult {
    /** Authenticated caller with a set of claims. */
    data class Success(val claims: Map<String, String>) : AuthResult

    /** No credential offered for this scheme. */
    data object NoResult : AuthResult

    /** Credential offered but rejected. */
    data class Fail(val reason: String) : AuthResult

    val isAuthenticated: Boolean get() = this is Success
}

// ── ApiKeyAuthHandler ────────────────────────────────────────────────────────

/**
 * API-key authentication handler. Reads the header named
 * [ApiKeyOptions.headerName] and matches against [ApiKeyOptions.keys]. When the
 * option block has `enabled = false` the handler succeeds with a synthetic
 * "anonymous" principal so dev environments don't need keys. Ports
 * ApiKeyAuthHandler.
 *
 * Headers are looked up case-insensitively (HTTP header names are
 * case-insensitive).
 */
class ApiKeyAuthHandler(private val serverOptions: () -> InferenceServerOptions) {

    /**
     * Authenticate a request given its headers. [headers] keys are matched
     * case-insensitively.
     */
    fun authenticate(headers: Map<String, String>): AuthResult {
        val cfg = serverOptions().auth.apiKey

        if (!cfg.enabled) {
            // Auth disabled — succeed with a marker identity.
            return AuthResult.Success(
                mapOf(
                    "name" to "anonymous",
                    "scheme" to AuthSchemes.API_KEY,
                    "auth_disabled" to "true",
                ),
            )
        }

        val raw = headers.entries.firstOrNull { it.key.equals(cfg.headerName, ignoreCase = true) }?.value
        if (raw.isNullOrBlank()) {
            return AuthResult.NoResult
        }

        if (!tryMatchKey(raw, cfg.keys)) {
            return AuthResult.Fail("Invalid API key.")
        }

        return AuthResult.Success(
            mapOf(
                "name" to "api-key-caller",
                "scheme" to AuthSchemes.API_KEY,
            ),
        )
    }

    private companion object {
        /** Constant-time match against any configured key (ports TryMatchKey). */
        fun tryMatchKey(presented: String, allowed: List<String>): Boolean {
            if (allowed.isEmpty()) return false
            val presentedBytes = presented.toByteArray(Charsets.UTF_8)
            var matched = false
            for (k in allowed) {
                if (k.isEmpty()) continue
                val bytes = k.toByteArray(Charsets.UTF_8)
                if (bytes.size != presentedBytes.size) continue
                // Constant-time compare — do not early-out on first mismatch.
                if (fixedTimeEquals(bytes, presentedBytes)) matched = true
            }
            return matched
        }

        /** Length-equal constant-time byte comparison (ports FixedTimeEquals). */
        fun fixedTimeEquals(a: ByteArray, b: ByteArray): Boolean {
            if (a.size != b.size) return false
            var diff = 0
            for (i in a.indices) diff = diff or (a[i].toInt() xor b[i].toInt())
            return diff == 0
        }
    }
}
