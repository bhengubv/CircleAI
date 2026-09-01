// ToolsCatalog.kt
//
// Port of CircleAI.Tools.Catalog: the provider directory, the encrypted
// credential store, the OAuth2 flow driver, the quota guard and the namespace
// partition - plus a fail-closed null for each.
//
// C# -> Kotlin notes:
//   * ArgumentException / InvalidOperationException become ToolsCatalogError,
//     one sealed type with the two cases the C# raises.
//   * AesGcm becomes javax.crypto AES/GCM/NoPadding. JCE emits
//     ciphertext || tag; the blob is reordered to nonce || tag || ciphertext so
//     it stays byte-compatible with the C# store and the Swift port.
//   * DateTime? becomes Instant?, serialised as epoch millis.

package com.bhengubv.circleai.tools.catalog

import java.security.SecureRandom
import java.time.Instant
import javax.crypto.Cipher
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.SecretKeySpec
import kotlinx.serialization.KSerializer
import kotlinx.serialization.Serializable
import kotlinx.serialization.descriptors.PrimitiveKind
import kotlinx.serialization.descriptors.PrimitiveSerialDescriptor
import kotlinx.serialization.descriptors.SerialDescriptor
import kotlinx.serialization.encoding.Decoder
import kotlinx.serialization.encoding.Encoder
import kotlinx.serialization.json.Json

// ------------------------------------------------------------- Errors

/** Errors raised by the tools-catalog primitives. */
sealed class ToolsCatalogError(message: String) : Exception(message) {
    /** The caller passed something the method cannot work with. C# ArgumentException. */
    class Argument(message: String) : ToolsCatalogError(message)

    /** The call is well formed but the state does not allow it. C# InvalidOperationException. */
    class InvalidOperation(message: String) : ToolsCatalogError(message)
}

private fun required(value: String?, name: String): String {
    if (value == null || value.isBlank()) throw ToolsCatalogError.Argument(name + " required")
    return value
}

// ------------------------------------------------------------ Records

/** How the provider authenticates. */
enum class AuthKind { NONE, API_KEY, BEARER_TOKEN, OAUTH2, BASIC, CUSTOM }

/** Epoch-millis wire form, so no serialization module registration is needed. */
object InstantEpochMillisSerializer : KSerializer<Instant> {
    override val descriptor: SerialDescriptor =
        PrimitiveSerialDescriptor("java.time.Instant", PrimitiveKind.LONG)

    override fun serialize(encoder: Encoder, value: Instant) =
        encoder.encodeLong(value.toEpochMilli())

    override fun deserialize(decoder: Decoder): Instant =
        Instant.ofEpochMilli(decoder.decodeLong())
}

/** OAuth2 configuration, present when a provider auth is OAUTH2. */
@Serializable
data class OAuth2Descriptor(
    val authorizeUrl: String,
    val tokenUrl: String,
    val scopes: List<String>,
    val userInfoUrl: String? = null,
)

/** One provider in the catalog - Gmail, Slack, Linear and the rest. */
@Serializable
data class ProviderDescriptor(
    val providerId: String,
    val displayName: String,
    val description: String,
    val homepage: String? = null,
    val auth: AuthKind,
    val tags: List<String> = emptyList(),
    val capabilities: List<String> = emptyList(),
    val oauth2: OAuth2Descriptor? = null,
)

/** One stored credential for one user and one provider. */
@Serializable
data class CredentialBundle(
    val providerId: String,
    val userId: String,
    val fields: Map<String, String>,
    @Serializable(with = InstantEpochMillisSerializer::class)
    val expiresAtUtc: Instant? = null,
)

/** A quota policy on one provider-and-user pair. */
@Serializable
data class QuotaPolicy(
    val providerId: String,
    val userId: String,
    val dailyCallBudget: Int,
    val maxConcurrent: Int,
    val perMinuteCap: Int,
)

/** Namespace partition - keeps one user tool list separate from the next. */
@Serializable
data class ToolNamespace(
    val namespaceId: String,
    val ownerUserId: String,
    val providerIds: List<String>,
)

// ---------------------------------------------------------- Contracts

/** The provider directory. */
interface ProviderCatalog {
    val backendId: String
    suspend fun listProviders(): List<ProviderDescriptor>
    suspend fun getProvider(providerId: String): ProviderDescriptor?

    /** Substring-and-tag search over registered providers. */
    suspend fun searchProviders(query: String, topK: Int = 8): List<ProviderDescriptor>
}

/** Credential storage. Implementations must encrypt at rest. */
interface CredentialStore {
    val backendId: String
    suspend fun upsert(bundle: CredentialBundle)
    suspend fun get(providerId: String, userId: String): CredentialBundle?
    suspend fun delete(providerId: String, userId: String)
}

/** OAuth2 flow driver - initiates and completes a three-legged flow. */
interface OAuth2FlowDriverContract {
    val backendId: String

    /** Build the redirect URL for the user browser. */
    suspend fun start(providerId: String, userId: String, redirectUri: String): String

    /** Exchange the authorisation code for a credential bundle. */
    suspend fun complete(
        providerId: String,
        userId: String,
        authorizationCode: String,
        redirectUri: String,
    ): CredentialBundle
}

/** Per-provider-and-user quota enforcement. */
interface QuotaGuard {
    val backendId: String
    suspend fun tryAcquire(providerId: String, userId: String): Boolean
    suspend fun setPolicy(policy: QuotaPolicy)
    suspend fun getPolicy(providerId: String, userId: String): QuotaPolicy?
}

/** Namespace store. */
interface ToolNamespaceStore {
    val backendId: String
    suspend fun upsert(ns: ToolNamespace)
    suspend fun get(namespaceId: String): ToolNamespace?
    suspend fun listForUser(userId: String): List<ToolNamespace>
}

// -------------------------------------------------------- Crypto seam

/**
 * Symmetric AEAD seam so the credential store never depends on a concrete
 * crypto library. seal returns the combined nonce, tag and ciphertext blob;
 * open reverses it and returns null on any authentication failure.
 */
interface CredentialCipher {
    fun seal(plaintext: ByteArray): ByteArray
    fun open(combined: ByteArray): ByteArray?
}

/**
 * AES-256-GCM. Layout: nonce(12) then tag(16) then ciphertext, matching the C#
 * AesGcm store byte for byte.
 *
 * JCE puts the tag at the END of its output, so seal splits it back off and
 * open splices it back on. Getting that wrong does not fail loudly - it fails
 * as an authentication error on a blob that was written correctly by the other
 * language, which is exactly the bug the layout comment exists to prevent.
 */
class AesGcmCredentialCipher(key32: ByteArray) : CredentialCipher {

    private val key: SecretKeySpec

    init {
        if (key32.size != 32) {
            throw ToolsCatalogError.Argument("key must be 32 bytes (AES-256-GCM)")
        }
        key = SecretKeySpec(key32, "AES")
    }

    override fun seal(plaintext: ByteArray): ByteArray {
        val nonce = ByteArray(NONCE_BYTES)
        random.nextBytes(nonce)
        val cipher = Cipher.getInstance(TRANSFORM)
        cipher.init(Cipher.ENCRYPT_MODE, key, GCMParameterSpec(TAG_BYTES * 8, nonce))
        val ctThenTag = cipher.doFinal(plaintext)
        val ctLength = ctThenTag.size - TAG_BYTES

        val out = ByteArray(NONCE_BYTES + TAG_BYTES + ctLength)
        System.arraycopy(nonce, 0, out, 0, NONCE_BYTES)
        System.arraycopy(ctThenTag, ctLength, out, NONCE_BYTES, TAG_BYTES)
        System.arraycopy(ctThenTag, 0, out, NONCE_BYTES + TAG_BYTES, ctLength)
        return out
    }

    override fun open(combined: ByteArray): ByteArray? {
        if (combined.size < NONCE_BYTES + TAG_BYTES) return null
        val ctLength = combined.size - NONCE_BYTES - TAG_BYTES

        val nonce = combined.copyOfRange(0, NONCE_BYTES)
        val ctThenTag = ByteArray(ctLength + TAG_BYTES)
        System.arraycopy(combined, NONCE_BYTES + TAG_BYTES, ctThenTag, 0, ctLength)
        System.arraycopy(combined, NONCE_BYTES, ctThenTag, ctLength, TAG_BYTES)

        return try {
            val cipher = Cipher.getInstance(TRANSFORM)
            cipher.init(Cipher.DECRYPT_MODE, key, GCMParameterSpec(TAG_BYTES * 8, nonce))
            cipher.doFinal(ctThenTag)
        } catch (e: Exception) {
            // Any authentication or padding failure is an absent credential, not
            // a crash. A tampered blob must not be distinguishable from a missing
            // one by the shape of what comes back.
            null
        }
    }

    companion object {
        private const val TRANSFORM = "AES/GCM/NoPadding"
        const val NONCE_BYTES = 12
        const val TAG_BYTES = 16
        private val random = SecureRandom()
    }
}

// ------------------------------------------- In-memory implementations

private fun pairKey(providerId: String, userId: String): String = providerId + "/" + userId

/** In-memory provider catalog with substring-and-tag search. */
class InMemoryProviderCatalog : ProviderCatalog {

    private val lock = Any()
    private val items = LinkedHashMap<String, ProviderDescriptor>() // keyed case-insensitively

    override val backendId: String get() = "in-memory"

    /** Registers, or replaces, a provider descriptor. */
    fun register(p: ProviderDescriptor) {
        synchronized(lock) { items[p.providerId.lowercase()] = p }
    }

    override suspend fun listProviders(): List<ProviderDescriptor> =
        synchronized(lock) { items.values.toList() }.sortedBy { it.providerId }

    override suspend fun getProvider(providerId: String): ProviderDescriptor? {
        required(providerId, "providerId")
        return synchronized(lock) { items[providerId.lowercase()] }
    }

    override suspend fun searchProviders(query: String, topK: Int): List<ProviderDescriptor> {
        if (topK <= 0) throw ToolsCatalogError.Argument("topK must be positive")
        val all = synchronized(lock) { items.values.toList() }
        return all
            .map { it to score(it, query) }
            .filter { it.second > 0 }
            .sortedByDescending { it.second }
            .take(topK)
            .map { it.first }
    }

    /**
     * The name is worth three, a tag or a capability two, the prose one. A
     * provider whose NAME is the query outranks one that merely mentions it.
     */
    private fun score(p: ProviderDescriptor, q: String): Int {
        var s = 0
        if (p.displayName.contains(q, ignoreCase = true)) s += 3
        if (p.description.contains(q, ignoreCase = true)) s += 1
        if (p.tags.any { it.contains(q, ignoreCase = true) }) s += 2
        if (p.capabilities.any { it.contains(q, ignoreCase = true) }) s += 2
        return s
    }
}

/**
 * AES-GCM-encrypted credential store. Encryption is delegated to an injected
 * cipher so the store itself never holds a key.
 */
class AesGcmCredentialStore(private val cipher: CredentialCipher) : CredentialStore {

    /** Convenience: a 32-byte key and the default AES-256-GCM cipher. */
    constructor(key32: ByteArray) : this(AesGcmCredentialCipher(key32))

    private val lock = Any()
    private val enc = HashMap<String, ByteArray>()

    override val backendId: String get() = "aes-gcm"

    override suspend fun upsert(bundle: CredentialBundle) {
        val json = Json.encodeToString(CredentialBundle.serializer(), bundle)
        val combined = cipher.seal(json.toByteArray(Charsets.UTF_8))
        synchronized(lock) { enc[pairKey(bundle.providerId, bundle.userId)] = combined }
    }

    override suspend fun get(providerId: String, userId: String): CredentialBundle? {
        required(providerId, "providerId")
        required(userId, "userId")
        val combined = synchronized(lock) { enc[pairKey(providerId, userId)] } ?: return null
        val pt = cipher.open(combined) ?: return null
        // A decode failure on authenticated plaintext reads as absent, mirroring
        // the C# catch-to-null path.
        return try {
            Json.decodeFromString(CredentialBundle.serializer(), pt.toString(Charsets.UTF_8))
        } catch (e: Exception) {
            null
        }
    }

    override suspend fun delete(providerId: String, userId: String) {
        required(providerId, "providerId")
        required(userId, "userId")
        synchronized(lock) { enc.remove(pairKey(providerId, userId)) }
    }
}

/**
 * OAuth2 flow driver. It builds the authorise URL; the token exchange is a host
 * closure, because that leg needs a network stack and this layer must not.
 */
class OAuth2FlowDriver(
    private val catalog: ProviderCatalog,
    private val clientIdFor: (String) -> String,
    private val exchange: suspend (
        providerId: String,
        userId: String,
        code: String,
        redirectUri: String,
    ) -> CredentialBundle,
) : OAuth2FlowDriverContract {

    override val backendId: String get() = "oauth2"

    override suspend fun start(providerId: String, userId: String, redirectUri: String): String {
        required(providerId, "providerId")
        required(userId, "userId")
        required(redirectUri, "redirectUri")

        val provider = catalog.getProvider(providerId)
            ?: throw ToolsCatalogError.InvalidOperation("Unknown provider " + providerId + ".")
        val oauth = provider.oauth2
            ?: throw ToolsCatalogError.InvalidOperation("Provider " + providerId + " is not OAuth2.")

        val state = urlSafeBase64(randomBytes(16))
        val scopes = oauth.scopes.joinToString(" ")
        val clientId = clientIdFor(providerId)
        return oauth.authorizeUrl + "?response_type=code" +
            "&client_id=" + urlEncode(clientId) +
            "&redirect_uri=" + urlEncode(redirectUri) +
            "&scope=" + urlEncode(scopes) +
            "&state=" + urlEncode(state)
    }

    override suspend fun complete(
        providerId: String,
        userId: String,
        authorizationCode: String,
        redirectUri: String,
    ): CredentialBundle {
        required(providerId, "providerId")
        required(userId, "userId")
        required(authorizationCode, "authorizationCode")
        required(redirectUri, "redirectUri")
        return exchange(providerId, userId, authorizationCode, redirectUri)
    }

    companion object {
        private val random = SecureRandom()

        private fun randomBytes(n: Int): ByteArray {
            val b = ByteArray(n)
            random.nextBytes(b)
            return b
        }

        /**
         * Base64 with the two URL-hostile characters swapped and the padding
         * dropped, because state rides in a query string.
         */
        fun urlSafeBase64(data: ByteArray): String =
            java.util.Base64.getEncoder().encodeToString(data)
                .replace("=", "")
                .replace("+", "-")
                .replace("/", "_")

        /**
         * Percent-encoding written out rather than delegated to URLEncoder,
         * which spells a space as a plus and percent-encodes a tilde. The other
         * ports keep exactly the unreserved set below, and an authorize URL that
         * differs between ports is a redirect_uri mismatch at the provider.
         */
        fun urlEncode(s: String): String {
            val unreserved =
                "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_.~"
            val out = StringBuilder(s.length)
            for (b in s.toByteArray(Charsets.UTF_8)) {
                val c = (b.toInt() and 0xFF).toChar()
                if (unreserved.indexOf(c) >= 0) {
                    out.append(c)
                } else {
                    out.append("%")
                    out.append(HEX[(b.toInt() and 0xF0) shr 4])
                    out.append(HEX[b.toInt() and 0x0F])
                }
            }
            return out.toString()
        }

        private val HEX = charArrayOf(
            Char(48), Char(49), Char(50), Char(51), Char(52), Char(53), Char(54), Char(55),
            Char(56), Char(57), Char(65), Char(66), Char(67), Char(68), Char(69), Char(70),
        )
    }
}

/**
 * Sliding-window per-minute cap, plus a daily budget and a max-concurrent
 * count.
 *
 * nowMillis is a test seam only: left alone it is the wall clock, which is what
 * the C# and the Swift use. A window measured in minutes cannot be tested
 * against a real clock without either sleeping or flaking.
 */
class SlidingWindowQuotaGuard(
    private val nowMillis: () -> Long = System::currentTimeMillis,
) : QuotaGuard {

    private val lock = Any()
    private val policies = HashMap<String, QuotaPolicy>()
    private val calls = HashMap<String, MutableList<Long>>()
    private val inflight = HashMap<String, Int>()

    override val backendId: String get() = "sliding-window"

    override suspend fun tryAcquire(providerId: String, userId: String): Boolean {
        val key = pairKey(providerId, userId)
        synchronized(lock) {
            // NO POLICY MEANS UNLIMITED, not denied. A provider nobody has
            // budgeted for still works; NullQuotaGuard is where fail-closed lives.
            val policy = policies[key] ?: return true

            val now = nowMillis()
            val list = calls.getOrPut(key) { mutableListOf() }

            // Per-minute cap: drop entries older than sixty seconds first.
            val minuteAgo = now - 60_000L
            list.removeAll { it < minuteAgo }
            if (list.size >= policy.perMinuteCap) return false

            // Daily budget, counted over the entries that survived the trim.
            val dayAgo = now - 86_400_000L
            if (list.count { it >= dayAgo } >= policy.dailyCallBudget) return false

            val current = inflight[key] ?: 0
            if (current >= policy.maxConcurrent) return false

            list.add(now)
            inflight[key] = current + 1
            return true
        }
    }

    /** Releases one in-flight slot. */
    fun release(providerId: String, userId: String) {
        val key = pairKey(providerId, userId)
        synchronized(lock) {
            val n = inflight[key] ?: 0
            if (n > 0) inflight[key] = n - 1
        }
    }

    override suspend fun setPolicy(policy: QuotaPolicy) {
        synchronized(lock) { policies[pairKey(policy.providerId, policy.userId)] = policy }
    }

    override suspend fun getPolicy(providerId: String, userId: String): QuotaPolicy? =
        synchronized(lock) { policies[pairKey(providerId, userId)] }
}

/** In-memory namespace store. */
class InMemoryToolNamespaceStore : ToolNamespaceStore {

    private val lock = Any()
    private val items = LinkedHashMap<String, ToolNamespace>()

    override val backendId: String get() = "in-memory"

    override suspend fun upsert(ns: ToolNamespace) {
        required(ns.namespaceId, "NamespaceId")
        synchronized(lock) { items[ns.namespaceId] = ns }
    }

    override suspend fun get(namespaceId: String): ToolNamespace? {
        required(namespaceId, "namespaceId")
        return synchronized(lock) { items[namespaceId] }
    }

    override suspend fun listForUser(userId: String): List<ToolNamespace> {
        required(userId, "userId")
        return synchronized(lock) { items.values.toList() }.filter { it.ownerUserId == userId }
    }
}

// ------------------------------------------- Fail-closed null objects

/** Fail-closed provider catalog. */
class NullProviderCatalog : ProviderCatalog {
    override val backendId: String get() = "null"
    override suspend fun listProviders(): List<ProviderDescriptor> = emptyList()
    override suspend fun getProvider(providerId: String): ProviderDescriptor? = null
    override suspend fun searchProviders(query: String, topK: Int): List<ProviderDescriptor> = emptyList()

    companion object { val instance = NullProviderCatalog() }
}

/** Fail-closed credential store. */
class NullCredentialStore : CredentialStore {
    override val backendId: String get() = "null"
    override suspend fun upsert(bundle: CredentialBundle) {}
    override suspend fun get(providerId: String, userId: String): CredentialBundle? = null
    override suspend fun delete(providerId: String, userId: String) {}

    companion object { val instance = NullCredentialStore() }
}

/** Fail-closed OAuth2 driver - start hands back about:blank, complete refuses. */
class NullOAuth2FlowDriver : OAuth2FlowDriverContract {
    override val backendId: String get() = "null"

    override suspend fun start(providerId: String, userId: String, redirectUri: String): String =
        "about:blank"

    override suspend fun complete(
        providerId: String,
        userId: String,
        authorizationCode: String,
        redirectUri: String,
    ): CredentialBundle =
        throw ToolsCatalogError.InvalidOperation("NullOAuth2FlowDriver: no real provider wired.")

    companion object { val instance = NullOAuth2FlowDriver() }
}

/** Fail-closed quota guard - every acquire is denied. */
class NullQuotaGuard : QuotaGuard {
    override val backendId: String get() = "null"
    override suspend fun tryAcquire(providerId: String, userId: String): Boolean = false
    override suspend fun setPolicy(policy: QuotaPolicy) {}
    override suspend fun getPolicy(providerId: String, userId: String): QuotaPolicy? = null

    companion object { val instance = NullQuotaGuard() }
}

/** Fail-closed namespace store. */
class NullToolNamespaceStore : ToolNamespaceStore {
    override val backendId: String get() = "null"
    override suspend fun upsert(ns: ToolNamespace) {}
    override suspend fun get(namespaceId: String): ToolNamespace? = null
    override suspend fun listForUser(userId: String): List<ToolNamespace> = emptyList()

    companion object { val instance = NullToolNamespaceStore() }
}
