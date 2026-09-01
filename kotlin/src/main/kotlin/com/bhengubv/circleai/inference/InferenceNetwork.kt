// InferenceNetwork.kt
//
// Why a download failed, in words a person can act on; whether it should have
// been attempted on this connection at all; and how to route around a dead
// system resolver rather than surrender to it.
//
// BEFORE THIS, A FAILED MODEL DOWNLOAD SURFACED AS A BARE TRANSPORT ERROR. On a
// Huawei P30 Lite with a dead resolver that read as "Unable to resolve host
// modelscope.cn", which is indistinguishable — to the caller and to the person
// holding the phone — from "the mirror is down", "you are offline", "the hotel
// wifi wants you to log in" and "the file 404'd". Those have completely
// different remedies and only some are the user's to fix, so shipping the raw
// error makes every one of them read as "CircleAI is broken".
//
// Ported from src/CircleAI.Inference/{NetworkDiagnosis, ModelDownloadException,
// ModelDownloadGate, NetworkPreflight, SideloadedBundleImporter}.cs.

package com.bhengubv.circleai.inference

import java.io.File
import java.net.URI
import java.security.MessageDigest
import java.util.Locale

enum class NetworkFault {
    None, NoLink, DnsFailure, CaptivePortal, HostUnreachable, TlsFailure, Timeout, HttpError, Unknown
}

/**
 * A fault, what happened, and what the person can do about it.
 *
 * [remedy] is EMPTY when there is nothing they can do — a dead mirror is not
 * theirs to fix, and inventing advice ("check your connection") for a problem on
 * our side sends somebody to reboot a router that was working.
 */
data class NetworkDiagnosis(
    val fault: NetworkFault,
    val detail: String,
    val remedy: String,
    /** Whether retrying could plausibly work. A 404 is not transient; a timeout
     *  is. Spinning on the first wastes battery and never succeeds. */
    val isTransient: Boolean
) {
    val shouldBlockDownload: Boolean get() = fault != NetworkFault.None

    override fun toString(): String = when {
        fault == NetworkFault.None -> "network: ok"
        remedy.isEmpty() -> "network: $fault — $detail"
        else -> "network: $fault — $detail. $remedy"
    }

    companion object {
        val Healthy = NetworkDiagnosis(NetworkFault.None, "reachable", "", false)

        /** Classifies an HTTP status that came back successfully but is a failure. */
        fun classify(httpStatus: Int): NetworkDiagnosis {
            if (httpStatus in 200..299) return Healthy
            return NetworkDiagnosis(
                NetworkFault.HttpError, "HTTP $httpStatus", "",
                // 5xx and 429 may pass on a retry; 4xx will not, so do not spin.
                isTransient = httpStatus >= 500 || httpStatus == 429
            )
        }

        /**
         * Classifies a raw failure.
         *
         * MATCHED ON TYPE NAME AND MESSAGE as well as on class, deliberately. On
         * Android the underlying failure is a Java type that a portable library
         * cannot name, and the same failure arrives under different classes on
         * different runtimes. Text matching is what makes one classifier serve
         * all of them.
         */
        fun classify(error: Throwable): NetworkDiagnosis {
            var e: Throwable? = error
            val seen = HashSet<Throwable>()
            while (e != null && seen.add(e)) {
                classifyOne(e)?.let { return it }
                e = e.cause
            }
            return NetworkDiagnosis(NetworkFault.Unknown, error.toString(), "", true)
        }

        private fun classifyOne(e: Throwable): NetworkDiagnosis? {
            val name = e.javaClass.name
            val message = e.message.orEmpty()
            val combined = "$name $message"
            val lower = combined.lowercase(Locale.ROOT)

            // DNS FIRST. A resolution failure is also a connection failure, and
            // checking the generic case first reports "no network" to somebody
            // whose network is fine and whose resolver is not — and then they
            // reboot a working router.
            if (matchesDnsFailure(combined)) {
                return NetworkDiagnosis(
                    NetworkFault.DnsFailure, message,
                    "Your device is connected but cannot look up addresses. " +
                        "Turning Wi-Fi off and on again usually fixes it.",
                    isTransient = true
                )
            }

            if (name.endsWith("SocketTimeoutException") || lower.contains("timed out") ||
                lower.contains("timeout")
            ) {
                return NetworkDiagnosis(
                    NetworkFault.Timeout, message,
                    "The connection is very slow or stalled. Try again on a better signal.",
                    isTransient = true
                )
            }

            if (name.contains("SSL") || name.contains("Certificate") ||
                lower.contains("certificate") || lower.contains("handshake")
            ) {
                return NetworkDiagnosis(
                    NetworkFault.TlsFailure, message,
                    "The secure connection could not be verified. If you are on public " +
                        "Wi-Fi, sign in to the network first.",
                    isTransient = true
                )
            }

            if (name.endsWith("NoRouteToHostException") ||
                lower.contains("network is unreachable") || lower.contains("network is down")
            ) {
                return NetworkDiagnosis(
                    NetworkFault.NoLink, message,
                    "There is no network connection. Connect to Wi-Fi or mobile data.",
                    isTransient = true
                )
            }

            if (name.endsWith("ConnectException") || lower.contains("connection refused")) {
                // A dead mirror is not the user's to fix, so no remedy.
                return NetworkDiagnosis(NetworkFault.HostUnreachable, message, "", true)
            }

            return null
        }

        /** Every spelling of "the name did not resolve" that has been seen. */
        internal fun matchesDnsFailure(combined: String): Boolean {
            if (combined.contains("UnknownHostException")) return true
            val lower = combined.lowercase(Locale.ROOT)
            return lower.contains("unable to resolve host") ||
                lower.contains("no address associated with hostname") ||
                combined.contains("EAI_NODATA") ||
                combined.contains("EAI_NONAME") ||
                lower.contains("name or service not known") ||
                lower.contains("nodename nor servname provided")
        }
    }
}

/**
 * A download failure that carries its own DIAGNOSIS, so callers and UI layers
 * stop pattern-matching on error text to work out whether the person is offline,
 * the mirror is dead, or the file is corrupt.
 */
class ModelDownloadException(
    message: String,
    val diagnosis: NetworkDiagnosis,
    cause: Throwable? = null
) : Exception(message, cause) {

    /** What to show a PERSON, as opposed to what to put in a log. "Unable to
     *  resolve host modelscope.cn" tells somebody holding a phone nothing. */
    val userMessage: String
        get() = diagnosis.remedy.ifEmpty {
            "The model could not be downloaded right now. Please try again later."
        }
}

class ModelDownloadBlockedException(message: String) : Exception(message)

// ─────────────────────────────────────────────────────────────────────────────
// Should this download run at all

interface IModelDownloadGate {
    /** Why this download must not start, or null to allow it. */
    fun blockReason(estimatedBytes: Long): String?

    /** Whether the guarantee actually HOLDS on this host. */
    val isEnforceable: Boolean
}

/**
 * Enforces "Wi-Fi only", which was INERT for months.
 *
 * The option existed, defaulted to on, was documented as protecting mobile data,
 * and nothing read it. The smallest catalogued bundle is 433 MB — real money on
 * a South African prepaid bundle.
 *
 * THE HONEST DIFFICULTY. A default device context can only say "online", so the
 * guarantee is genuinely unenforceable there. Failing CLOSED would stop every
 * desktop host downloading anything; failing OPEN silently recreates the
 * original bug on exactly the devices it was meant to protect. So it fails open
 * and SAYS SO: [isEnforceable] reports whether the check actually held, and a
 * host can surface "we cannot tell if you are on mobile data" rather than the
 * SDK pretending it looked.
 */
class MeteredNetworkDownloadGate(
    private val networkType: () -> String?,
    private val wifiOnly: Boolean = true
) : IModelDownloadGate {

    override val isEnforceable: Boolean
        get() {
            if (!wifiOnly) return true                 // nothing to enforce
            val net = normalise(networkType()) ?: return false
            return net in UNMETERED || net in METERED || net == "none"
        }

    override fun blockReason(estimatedBytes: Long): String? {
        if (!wifiOnly) return null
        val net = normalise(networkType()) ?: return null

        if (net in METERED) {
            val size = if (estimatedBytes > 0)
                "%.0f MB".format(estimatedBytes / 1024.0 / 1024) else "a large"
            return "This download is $size and you appear to be on mobile data. " +
                "Connect to Wi-Fi, or allow mobile downloads in settings."
        }
        if (net == "none") return "No network connection is available for the model download."

        // Unmetered is allowed. "online", "mesh" and anything unrecognised are
        // also allowed — but see isEnforceable: we could not actually verify it.
        return null
    }

    companion object {
        internal val UNMETERED = setOf("wifi", "ethernet", "unmetered")
        internal val METERED = setOf("cellular", "mobile", "metered")

        internal fun normalise(value: String?): String? =
            value?.trim()?.lowercase(Locale.ROOT)?.takeIf { it.isNotEmpty() }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Preflight, and the DNS bypass

interface INetworkPreflight {
    suspend fun check(target: URI): NetworkDiagnosis
    suspend fun resolve(host: String): List<String>
}

/**
 * Checks the network BEFORE a 433 MB download, and routes AROUND a dead system
 * resolver.
 *
 * WHY NOT "RESTART DNS": an app cannot. There is no public API to flush or
 * restart the platform resolver, and toggling Wi-Fi has been a no-op for
 * non-system apps since API 29. Toggling it over adb fixes the phone; the app
 * has no such power. So the recovery is to BYPASS it — ask the system resolver,
 * and on failure resolve over DNS-over-HTTPS addressed by IP LITERAL. That is
 * the whole trick: https://1.1.1.1/dns-query is reachable with a broken resolver
 * precisely because there is no name to look up.
 *
 * RESOLVER CHOICE — de-Googled by policy. Cloudflare and Quad9 only; 8.8.8.8 is
 * Google and is deliberately absent. Quad9 is second because a Swiss non-profit
 * is a different failure domain, not a second helping of the same one.
 */
class NetworkPreflight(
    private val transport: suspend (URI, Map<String, String>) -> HttpReply,
    private val systemResolve: suspend (String) -> List<String> = { emptyList() },
    private val linkIsUp: () -> Boolean = { true }
) : INetworkPreflight {

    data class HttpReply(val body: ByteArray, val status: Int, val location: String?)

    override suspend fun check(target: URI): NetworkDiagnosis {
        // LINK LAYER FIRST — cheapest, and it distinguishes "no network at all"
        // from "network but broken", which have different remedies.
        if (!linkIsUp()) {
            return NetworkDiagnosis(
                NetworkFault.NoLink, "no network interface is up",
                "Connect to Wi-Fi or mobile data.", true
            )
        }

        return try {
            // HEAD, not GET: this wants reachability, not 433 MB of payload.
            val reply = transport(target, mapOf("X-Method" to "HEAD"))

            // A REDIRECT TO AN UNRELATED HOST ON A PLAIN HEAD is the classic
            // captive-portal signature: the network answered for somebody else.
            val loc = reply.location
            if (isRedirect(reply.status) && loc != null) {
                val host = runCatching { URI(loc).host }.getOrNull()
                if (host != null && !host.equals(target.host, ignoreCase = true)) {
                    return NetworkDiagnosis(
                        NetworkFault.CaptivePortal, "redirected to $host",
                        "This Wi-Fi needs you to sign in first. Open a browser and " +
                            "complete sign-in.",
                        // NOT transient: retrying redirects again until somebody
                        // signs in, and spinning on it drains a battery.
                        isTransient = false
                    )
                }
            }

            if (reply.status !in 200..399) NetworkDiagnosis.classify(reply.status)
            else NetworkDiagnosis.Healthy
        } catch (t: Throwable) {
            val diagnosis = NetworkDiagnosis.classify(t)

            // A DNS FAILURE IS NOT NECESSARILY FATAL — the bypass may still
            // resolve it. Only report it when that ALSO fails, otherwise this
            // blocks a download that would have worked.
            if (diagnosis.fault == NetworkFault.DnsFailure) {
                val viaDoh = resolveViaDoh(target.host.orEmpty())
                if (viaDoh.isNotEmpty()) {
                    return NetworkDiagnosis(
                        NetworkFault.DnsFailure,
                        "system resolver failed for '${target.host}'; " +
                            "resolved ${viaDoh.first()} over DoH instead",
                        "",                       // nothing to do — we routed around it
                        isTransient = true
                    )
                }
            }
            diagnosis
        }
    }

    override suspend fun resolve(host: String): List<String> {
        val trimmed = host.trim()
        if (trimmed.isEmpty()) return emptyList()
        // Already an address: asking a broken resolver about an IP literal is
        // how a working connection gets blocked by a broken one.
        if (isIpLiteral(trimmed)) return listOf(trimmed)

        val system = systemResolve(trimmed)
        if (system.isNotEmpty()) return system
        return resolveViaDoh(trimmed)
    }

    internal suspend fun resolveViaDoh(host: String): List<String> {
        if (host.isEmpty()) return emptyList()
        for (endpoint in DOH_ENDPOINTS) {
            val uri = runCatching {
                URI("$endpoint?name=${java.net.URLEncoder.encode(host, "UTF-8")}&type=A")
            }.getOrNull() ?: continue

            val reply = runCatching {
                transport(uri, mapOf("Accept" to "application/dns-json"))
            }.getOrNull() ?: continue          // try the next resolver

            if (reply.status !in 200..299) continue
            val addresses = parseDohAnswer(String(reply.body, Charsets.UTF_8))
            if (addresses.isNotEmpty()) return addresses
        }
        return emptyList()
    }

    companion object {
        /** Cloudflare first, then Quad9. NOT 8.8.8.8 — that is Google, and its
         *  absence is a policy decision, not an oversight. */
        val DOH_ENDPOINTS = listOf(
            "https://1.1.1.1/dns-query",          // Cloudflare
            "https://9.9.9.9:5053/dns-query"      // Quad9, a Swiss non-profit
        )

        internal fun isRedirect(status: Int) = status in setOf(301, 302, 303, 307, 308)

        /**
         * Only A records (type 1). A CNAME is a NAME, and connecting to it would
         * need the resolver that just failed; an A record carrying a hostname is
         * rejected for the same reason.
         */
        internal fun parseDohAnswer(json: String): List<String> =
            Regex("\\{[^{}]*\"type\"\\s*:\\s*1\\b[^{}]*}")
                .findAll(json)
                .mapNotNull { Regex("\"data\"\\s*:\\s*\"([^\"]+)\"").find(it.value)?.groupValues?.get(1) }
                .filter { isIpLiteral(it) }
                .toList()

        /** Dotted quad only, matching the A records this asks for. Deliberately
         *  not permissive: a hostname where an address belongs would go straight
         *  back to the resolver that is broken. */
        internal fun isIpLiteral(s: String): Boolean {
            val parts = s.split('.')
            if (parts.size != 4) return false
            return parts.all { p ->
                p.isNotEmpty() && p.length <= 3 && p.all(Char::isDigit) &&
                    (p.toIntOrNull() ?: -1) in 0..255
            }
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Side-loaded bundles

enum class SideloadOutcome { Imported, AlreadyInstalled, NotFound, Corrupt, Unknown, CopyFailed }

data class SideloadResult(
    val outcome: SideloadOutcome,
    /** Written for a PERSON, not a log. */
    val detail: String,
    val files: Int = 0
) {
    /** Both mean the model is there and checked. A caller that treats only
     *  Imported as success re-imports on every launch. */
    val usable: Boolean
        get() = outcome == SideloadOutcome.Imported || outcome == SideloadOutcome.AlreadyInstalled
}

/**
 * Makes a hand-copied model a first-class installed one, or refuses it.
 *
 * WHY THIS IS A FEATURE AND NOT A DEVELOPER HOOK. A 7 MB wake word or a 900 MB
 * generalist is real money on a prepaid bundle, and the people this is built for
 * are exactly the ones handed a model over Bluetooth or on a memory card.
 * Reading a side-loaded folder already worked; what was missing is everything
 * that makes it TRUSTWORTHY.
 *
 * VERIFY, THEN IMPORT, IN THAT ORDER. The registry pins a SHA-256 per file, so a
 * copy arriving by an untrusted route is held to the standard a download is, and
 * one that does not match never reaches the store. Without it, "copy this folder
 * onto your phone" is an invitation to run somebody else's weights.
 */
class SideloadedBundleImporter(
    private val storageRoot: String,
    private val lookup: (String) -> List<BundleFileSpec>?
) {
    data class BundleFileSpec(val name: String, val sha256: String, val sizeBytes: Long)

    fun import(modelName: String, folder: String): SideloadResult {
        val wanted = lookup(modelName)
        if (wanted.isNullOrEmpty()) {
            return SideloadResult(
                SideloadOutcome.Unknown,
                "\u201C$modelName\u201D is not in the catalogue, so there is nothing to " +
                    "check this against."
            )
        }

        val src = File(folder)
        if (!src.isDirectory) {
            return SideloadResult(SideloadOutcome.NotFound, "That folder is not there.")
        }

        // The published names are repo-relative, but somebody copying a folder
        // keeps the LEAF names and rarely the path. Both are accepted.
        val present = src.walkTopDown().filter { it.isFile }
            .associateBy { it.name.lowercase(Locale.ROOT) }

        val verified = ArrayList<Pair<String, File>>()
        for (want in wanted) {
            val leaf = want.name.substringAfterLast('/')
            val source = present[leaf.lowercase(Locale.ROOT)]
                ?: return SideloadResult(SideloadOutcome.NotFound, "This copy is missing $leaf.")

            // SIZE FIRST, because it is free and catches the overwhelmingly
            // common failure — a copy that stopped part-way — without reading
            // 400 MB to find out.
            if (want.sizeBytes > 0 && source.length() != want.sizeBytes) {
                return SideloadResult(
                    SideloadOutcome.Corrupt,
                    "$leaf is the wrong size — ${source.length()} bytes instead of " +
                        "${want.sizeBytes}. The copy is probably incomplete."
                )
            }

            if (want.sha256.isNotBlank()) {
                val actual = sha256Hex(source)
                if (!actual.equals(want.sha256, ignoreCase = true)) {
                    return SideloadResult(
                        SideloadOutcome.Corrupt,
                        "$leaf does not match the published version. It may have been " +
                            "damaged in transit, or it may not be ours."
                    )
                }
            }
            verified.add(want.name to source)
        }

        val target = File(storageRoot, modelName)
        if (target.isDirectory && verified.all { File(target, it.first).isFile }) {
            return SideloadResult(
                SideloadOutcome.AlreadyInstalled, "This is already installed.", verified.size
            )
        }

        for ((relative, source) in verified) {
            val dest = File(target, relative)
            try {
                dest.parentFile?.mkdirs()
                // COPY, NEVER MOVE. The folder may be shared storage somebody
                // wants to pass to the next phone, and consuming it would make
                // installing on one device destroy the copy for everyone else.
                source.copyTo(dest, overwrite = true)
            } catch (t: Throwable) {
                return SideloadResult(
                    SideloadOutcome.CopyFailed, "Could not save it: ${t.message}", verified.size
                )
            }
        }
        return SideloadResult(SideloadOutcome.Imported, "Installed and checked.", verified.size)
    }

    companion object {
        /** Streamed in 1 MB chunks: reading a 900 MB model into memory to hash
         *  it is the allocation a low-end phone cannot make. */
        fun sha256Hex(file: File): String {
            val digest = MessageDigest.getInstance("SHA-256")
            file.inputStream().use { stream ->
                val buffer = ByteArray(1 shl 20)
                while (true) {
                    val read = stream.read(buffer)
                    if (read <= 0) break
                    digest.update(buffer, 0, read)
                }
            }
            return digest.digest().joinToString("") { "%02x".format(it) }
        }
    }
}
