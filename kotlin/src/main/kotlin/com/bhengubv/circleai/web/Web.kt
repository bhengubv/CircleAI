// Web.kt
//
// Kotlin port of CircleAI.Web — the C# reference is the EXACT spec. The Web
// vertical's portable core: HTTP route/page-metadata/cache domain types with an
// in-memory board, plus a per-circuit Companion session lifecycle wrapper.
//
// Covers (C# file -> Kotlin type):
//   WebPrimitives.cs         -> RouteDescriptor, PageMetadata, CachedResponse,
//                               IWebBoard, InMemoryWebBoard
//   WebCompanionService.cs   -> WebCompanionService
//
// Portability note:
//   The C# `ServiceCollectionExtensions.AddCircleWebCompanion` (Blazor / ASP.NET
//   `IServiceCollection` DI glue) is host-framework-specific and has no analogue
//   in the portable Kotlin core — the Kotlin port constructs sessions directly
//   via `ICompanionSessionFactory` (mirroring `companion.CompanionSessionFactory`).
//   It is intentionally NOT ported. The `WebCompanionService` lifecycle logic
//   itself IS portable and is ported 1:1.
//
// Fidelity notes:
//   * C# `record` -> `data class`; `byte[] Body` -> `ByteArray`;
//     `DateTimeOffset` -> `Instant`.
//   * `ConcurrentDictionary` (Ordinal / OrdinalIgnoreCase) -> `ConcurrentHashMap`;
//     case-insensitive metadata keys are lower-cased (Locale.ROOT).
//   * Route key = `"${METHOD} ${path}"` with upper-cased method; `RoutesByMethod`
//     filters case-insensitively and orders by path ASC.
//   * `Cache` skips already-expired entries; `Lookup` evicts on read when expired.
//   * `WebCompanionService.Session` throws before init (C# InvalidOperationException
//     -> Kotlin IllegalStateException); `InitialiseAsync` is idempotent.

package com.bhengubv.circleai.web

import com.bhengubv.circleai.companion.ICompanionSession
import com.bhengubv.circleai.companion.ICompanionSessionFactory
import com.bhengubv.circleai.companion.InterfaceKind
import java.time.Instant
import java.util.Locale
import java.util.concurrent.ConcurrentHashMap

// =====================================================================
// WebPrimitives (WebPrimitives.cs)
// =====================================================================

/** A registered HTTP route. */
data class RouteDescriptor(
    val path: String,
    val method: String,
    val handlerName: String,
    val tags: List<String>,
)

/** SEO / page metadata for a path. */
data class PageMetadata(
    val path: String,
    val title: String,
    val description: String?,
    val keywords: List<String>,
)

/** A cached HTTP response body with an expiry. */
data class CachedResponse(
    val key: String,
    val body: ByteArray,
    val mime: String,
    val expiresUtc: Instant,
) {
    // ByteArray is reference-equal by default; override for value semantics.
    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is CachedResponse) return false
        return key == other.key &&
            body.contentEquals(other.body) &&
            mime == other.mime &&
            expiresUtc == other.expiresUtc
    }

    override fun hashCode(): Int {
        var result = key.hashCode()
        result = 31 * result + body.contentHashCode()
        result = 31 * result + mime.hashCode()
        result = 31 * result + expiresUtc.hashCode()
        return result
    }
}

/** Deterministic in-memory board for routes, page metadata, and response cache. */
interface IWebBoard {
    fun register(r: RouteDescriptor)
    fun routesByMethod(method: String): List<RouteDescriptor>
    fun setMetadata(m: PageMetadata)
    fun getMetadata(path: String): PageMetadata?
    fun cache(c: CachedResponse)
    fun lookup(key: String): CachedResponse?
}

/** In-memory [IWebBoard]. */
class InMemoryWebBoard : IWebBoard {
    private val routes = ConcurrentHashMap<String, RouteDescriptor>()
    // Case-insensitive metadata keys: store under lower-cased path.
    private val meta = ConcurrentHashMap<String, PageMetadata>()
    private val cacheMap = ConcurrentHashMap<String, CachedResponse>()

    override fun register(r: RouteDescriptor) {
        routes["${r.method.uppercase(Locale.ROOT)} ${r.path}"] = r
    }

    override fun routesByMethod(method: String): List<RouteDescriptor> {
        require(method.isNotBlank()) { "method required" }
        return routes.values
            .filter { it.method.equals(method, ignoreCase = true) }
            .sortedBy { it.path }
    }

    override fun setMetadata(m: PageMetadata) {
        meta[m.path.lowercase(Locale.ROOT)] = m
    }

    override fun getMetadata(path: String): PageMetadata? = meta[path.lowercase(Locale.ROOT)]

    override fun cache(c: CachedResponse) {
        if (!c.expiresUtc.isAfter(Instant.now())) return // already expired; skip
        cacheMap[c.key] = c
    }

    override fun lookup(key: String): CachedResponse? {
        require(key.isNotBlank()) { "key required" }
        val c = cacheMap[key] ?: return null
        if (!c.expiresUtc.isAfter(Instant.now())) {
            cacheMap.remove(key)
            return null
        }
        return c
    }
}

// =====================================================================
// WebCompanionService (WebCompanionService.cs)
// =====================================================================

/**
 * Manages the lifecycle of a single [ICompanionSession] per browser tab /
 * circuit. In the C# reference this is a scoped Blazor service; here it is a
 * plain [AutoCloseable] lifecycle wrapper the host owns per circuit.
 */
class WebCompanionService(private val factory: ICompanionSessionFactory) : AutoCloseable {
    private var session: ICompanionSession? = null

    /** The active session, once [initialise] has been called. */
    val activeSession: ICompanionSession
        get() = session ?: throw IllegalStateException("Call initialise first.")

    /**
     * Initialises the Companion session for the given identity. Safe to call
     * multiple times — only creates a session once.
     */
    suspend fun initialise(identityId: String) {
        if (session != null) return
        session = factory.createAsync(identityId, InterfaceKind.Web)
    }

    override fun close() {
        session?.close()
    }
}
