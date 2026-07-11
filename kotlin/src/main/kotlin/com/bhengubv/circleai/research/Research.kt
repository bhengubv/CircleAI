// Research.kt
//
// Kotlin port of CircleAI.Research (Contracts.cs + InMemoryResearch.cs +
// NullImplementations.cs) — the C# reference is the EXACT spec. Research
// corpora: paper store with substring scoring on title/abstract/authors, a
// full-text retrieval seam, and a citation adjacency graph.
//
// Fidelity notes:
//   * C# `record` -> Kotlin `data class`.
//   * C# `DateTimeOffset` -> `java.time.Instant`.
//   * C# `ReadOnlyMemory<byte>` -> `ByteArray`.
//   * C# `ValueTask`/`ValueTask<T>` async members -> `suspend fun`.
//   * `ConcurrentDictionary`(StringComparer.Ordinal) -> `ConcurrentHashMap`.
//   * SearchAsync scores Title(+3)/Abstract(+1)/Authors(+1) OrdinalIgnoreCase,
//     keeps score > 0, orders by score desc, takes topK.
//   * Null implementations are fail-open no-op singletons.

package com.bhengubv.circleai.research

import java.time.Instant
import java.util.concurrent.ConcurrentHashMap

// =====================================================================
// Contracts (Contracts.cs)
// =====================================================================

/** A research paper. Mirrors C# `ResearchPaper`. */
data class ResearchPaper(
    val paperId: String,
    val title: String,
    val authors: List<String>,
    val abstractText: String,
    val publishedAtUtc: Instant,
    val doi: String?,
)

/** One citation edge between two papers. Mirrors C# `Citation`. */
data class Citation(val fromPaperId: String, val toPaperId: String, val context: String)

/** Research corpus — paper lookup + search. Mirrors C# `IResearchCorpus`. */
interface IResearchCorpus {
    val backendId: String
    suspend fun getAsync(paperId: String): ResearchPaper?
    suspend fun searchAsync(query: String, topK: Int = 10): List<ResearchPaper>
}

/** Full-text retrieval seam. Mirrors C# `IPaperRetrieval`. */
interface IPaperRetrieval {
    val backendId: String
    suspend fun fetchFullTextAsync(paperId: String): ByteArray?
}

/** Citation graph — forward + backward adjacency. Mirrors C# `ICitationGraph`. */
interface ICitationGraph {
    val backendId: String
    suspend fun forwardCitationsAsync(paperId: String): List<Citation>
    suspend fun backwardCitationsAsync(paperId: String): List<Citation>
}

// =====================================================================
// In-memory implementations (InMemoryResearch.cs)
// =====================================================================

/** In-memory [IResearchCorpus] with substring relevance. Mirrors C# `InMemoryResearchCorpus`. */
class InMemoryResearchCorpus : IResearchCorpus {
    private val papers = ConcurrentHashMap<String, ResearchPaper>()

    override val backendId: String get() = "in-memory"

    fun add(paper: ResearchPaper) {
        papers[paper.paperId] = paper
    }

    override suspend fun getAsync(paperId: String): ResearchPaper? {
        require(paperId.isNotBlank()) { "paperId required" }
        return papers[paperId]
    }

    override suspend fun searchAsync(query: String, topK: Int): List<ResearchPaper> {
        if (topK <= 0) throw IndexOutOfBoundsException("topK")
        return papers.values
            .map { it to score(it, query) }
            .filter { it.second > 0 }
            .sortedByDescending { it.second }
            .take(topK)
            .map { it.first }
    }

    private companion object {
        fun score(p: ResearchPaper, q: String): Int {
            var s = 0
            if (p.title.contains(q, ignoreCase = true)) s += 3
            if (p.abstractText.contains(q, ignoreCase = true)) s += 1
            if (p.authors.any { it.contains(q, ignoreCase = true) }) s += 1
            return s
        }
    }
}

/** In-memory [IPaperRetrieval]. Mirrors C# `InMemoryPaperRetrieval`. */
class InMemoryPaperRetrieval : IPaperRetrieval {
    private val texts = ConcurrentHashMap<String, ByteArray>()

    override val backendId: String get() = "in-memory"

    fun add(paperId: String, fullText: ByteArray) {
        require(paperId.isNotBlank()) { "paperId required" }
        texts[paperId] = fullText
    }

    override suspend fun fetchFullTextAsync(paperId: String): ByteArray? {
        require(paperId.isNotBlank()) { "paperId required" }
        return texts[paperId]
    }
}

/** In-memory [ICitationGraph] adjacency list. Mirrors C# `InMemoryCitationGraph`. */
class InMemoryCitationGraph : ICitationGraph {
    private val forward = ConcurrentHashMap<String, MutableList<Citation>>()
    private val backward = ConcurrentHashMap<String, MutableList<Citation>>()
    private val lock = Any()

    override val backendId: String get() = "in-memory"

    fun link(c: Citation) {
        synchronized(lock) {
            forward.getOrPut(c.fromPaperId) { mutableListOf() }.add(c)
            backward.getOrPut(c.toPaperId) { mutableListOf() }.add(c)
        }
    }

    override suspend fun forwardCitationsAsync(paperId: String): List<Citation> {
        require(paperId.isNotBlank()) { "paperId required" }
        synchronized(lock) {
            return forward[paperId]?.toList() ?: emptyList()
        }
    }

    override suspend fun backwardCitationsAsync(paperId: String): List<Citation> {
        require(paperId.isNotBlank()) { "paperId required" }
        synchronized(lock) {
            return backward[paperId]?.toList() ?: emptyList()
        }
    }
}

// =====================================================================
// Null implementations (NullImplementations.cs)
// =====================================================================

/** Fail-open [IResearchCorpus]. Mirrors C# `NullResearchCorpus`. */
class NullResearchCorpus private constructor() : IResearchCorpus {
    override val backendId: String get() = "null"
    override suspend fun getAsync(paperId: String): ResearchPaper? = null
    override suspend fun searchAsync(query: String, topK: Int): List<ResearchPaper> = emptyList()

    companion object {
        val Instance = NullResearchCorpus()
    }
}

/** Fail-open [IPaperRetrieval]. Mirrors C# `NullPaperRetrieval`. */
class NullPaperRetrieval private constructor() : IPaperRetrieval {
    override val backendId: String get() = "null"
    override suspend fun fetchFullTextAsync(paperId: String): ByteArray? = null

    companion object {
        val Instance = NullPaperRetrieval()
    }
}

/** Fail-open [ICitationGraph]. Mirrors C# `NullCitationGraph`. */
class NullCitationGraph private constructor() : ICitationGraph {
    override val backendId: String get() = "null"
    override suspend fun forwardCitationsAsync(paperId: String): List<Citation> = emptyList()
    override suspend fun backwardCitationsAsync(paperId: String): List<Citation> = emptyList()

    companion object {
        val Instance = NullCitationGraph()
    }
}
