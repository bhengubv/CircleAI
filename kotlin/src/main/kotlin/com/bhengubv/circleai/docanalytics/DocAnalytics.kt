// DocAnalytics.kt
//
// Kotlin port of CircleAI.DocAnalytics (Contracts.cs + InMemoryDocumentTracker.cs
// + NullImplementations.cs) — the C# reference is the EXACT spec. Records
// document views and computes per-document insights.
//
// Fidelity notes:
//   * C# `record` -> Kotlin `data class`.
//   * C# `DateTimeOffset` -> `java.time.Instant`; C# `TimeSpan` -> `java.time.Duration`.
//   * C# `ValueTask` async members -> `suspend fun`.
//   * The single in-memory class implements BOTH IDocumentTracker and
//     IDocumentInsights, matching C# `InMemoryDocumentTracker`.

package com.bhengubv.circleai.docanalytics

import java.time.Duration
import java.time.Instant
import java.util.concurrent.ConcurrentHashMap

// =====================================================================
// Contracts (Contracts.cs)
// =====================================================================

/** One recorded view of a document. Mirrors C# `DocumentView`. */
data class DocumentView(
    val documentId: String,
    val viewerId: String,
    val atUtc: Instant,
    val duration: Duration,
    val pagesViewed: Int,
)

/** Aggregated insight for one document. Mirrors C# `DocumentInsight`. */
data class DocumentInsight(
    val documentId: String,
    val totalViews: Int,
    val uniqueViewers: Int,
    val avgDurationSeconds: Double,
)

/** Document view tracker. Mirrors C# `IDocumentTracker`. */
interface IDocumentTracker {
    val backendId: String
    suspend fun recordViewAsync(view: DocumentView)
    suspend fun listViewsAsync(documentId: String): List<DocumentView>
}

/** Document insight computation. Mirrors C# `IDocumentInsights`. */
interface IDocumentInsights {
    val backendId: String
    suspend fun computeAsync(documentId: String): DocumentInsight?
}

// =====================================================================
// In-memory implementation (InMemoryDocumentTracker.cs)
// =====================================================================

/**
 * Thread-safe in-memory tracker + insights. Records every view in a per-document
 * list and computes insights on demand. Mirrors C# `InMemoryDocumentTracker`.
 */
class InMemoryDocumentTracker : IDocumentTracker, IDocumentInsights {
    private val byDoc = ConcurrentHashMap<String, MutableList<DocumentView>>()
    private val writeLock = Any()

    override val backendId: String get() = "in-memory"

    override suspend fun recordViewAsync(view: DocumentView) {
        require(view.documentId.isNotBlank()) { "DocumentId required" }
        synchronized(writeLock) {
            byDoc.getOrPut(view.documentId) { mutableListOf() }.add(view)
        }
    }

    override suspend fun listViewsAsync(documentId: String): List<DocumentView> {
        require(documentId.isNotBlank()) { "documentId required" }
        synchronized(writeLock) {
            return byDoc[documentId]?.toList() ?: emptyList()
        }
    }

    override suspend fun computeAsync(documentId: String): DocumentInsight? {
        require(documentId.isNotBlank()) { "documentId required" }
        synchronized(writeLock) {
            val views = byDoc[documentId]
            if (views == null || views.isEmpty()) return null

            val total = views.size
            val unique = views.map { it.viewerId }.distinct().count()
            val avgSeconds = views.map { it.duration.toNanos() / 1_000_000_000.0 }.average()

            return DocumentInsight(
                documentId = documentId,
                totalViews = total,
                uniqueViewers = unique,
                avgDurationSeconds = avgSeconds,
            )
        }
    }
}

// =====================================================================
// Null implementations (NullImplementations.cs)
// =====================================================================

/** No-op [IDocumentTracker]. Mirrors C# `NullDocumentTracker`. */
class NullDocumentTracker private constructor() : IDocumentTracker {
    override val backendId: String get() = "null"
    override suspend fun recordViewAsync(view: DocumentView) {}
    override suspend fun listViewsAsync(documentId: String): List<DocumentView> = emptyList()

    companion object {
        val Instance = NullDocumentTracker()
    }
}

/** No-op [IDocumentInsights]. Mirrors C# `NullDocumentInsights`. */
class NullDocumentInsights private constructor() : IDocumentInsights {
    override val backendId: String get() = "null"
    override suspend fun computeAsync(documentId: String): DocumentInsight? = null

    companion object {
        val Instance = NullDocumentInsights()
    }
}
