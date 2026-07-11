// Domain.kt
//
// Kotlin port of CircleAI.Domain (Contracts.cs + InMemoryDomain.cs +
// NullImplementations.cs) — the C# reference is the EXACT spec. The
// domain-specialist plug points: food embeddings, finance retrieval + agent,
// presentation generation, job-search drafting, MemPalace / HippoRAG memory,
// swarm coordination, and personal-LoRA adapter management, each with a
// deterministic in-memory backing and a fail-safe null default.
//
// Fidelity notes:
//   * C# `record` -> Kotlin `data class`; C# `DateTimeOffset` -> `java.time.Instant`.
//   * C# `ValueTask` async members -> `suspend fun`.
//   * `ConcurrentDictionary` -> `ConcurrentHashMap`.
//   * InMemoryFoodEmbeddings' fallback vector: the C# uses
//     String.GetHashCode(OrdinalIgnoreCase), which is deterministic-per-process
//     but NOT portable. The Kotlin port uses a stable FNV-1a hash of the
//     lowercased name so the 8-dim vector is deterministic across runs and hosts
//     (the observable contract — a name-derived vector — is preserved).

package com.bhengubv.circleai.domain

import java.time.Instant
import java.util.concurrent.ConcurrentHashMap
import kotlin.math.ln

// =====================================================================
// Contracts (Contracts.cs)
// =====================================================================

// ─── Food (EPICure) ─────────────────────────────────────────────────

/** One ingredient with optional canonical form + quantity. Mirrors C# `Ingredient`. */
data class Ingredient(val name: String, val canonical: String? = null, val quantity: String? = null)

/** Food / ingredient embedding store (EPICure-backed). Mirrors C# `IFoodEmbeddings`. */
interface IFoodEmbeddings {
    val backendId: String
    suspend fun embedAsync(ingredient: Ingredient): FloatArray
    suspend fun substitutesAsync(ingredient: Ingredient, topK: Int = 5): List<Ingredient>
}

// ─── Finance (quant-mind + dexter) ──────────────────────────────────

/** One retrieved finance snippet. Mirrors C# `FinanceSnippet`. */
data class FinanceSnippet(val text: String, val source: String, val score: Float)

/** Quant-finance RAG retrieval. Mirrors C# `IFinanceRetrieval`. */
interface IFinanceRetrieval {
    val backendId: String
    suspend fun retrieveAsync(query: String, topK: Int = 5): List<FinanceSnippet>
}

/** One agent finding. Mirrors C# `FinanceFinding`. */
data class FinanceFinding(val subject: String, val summary: String, val citations: List<String>)

/** Autonomous financial-research agent (dexter pattern). Mirrors C# `IFinancialAgent`. */
interface IFinancialAgent {
    val backendId: String
    suspend fun researchAsync(question: String): List<FinanceFinding>
}

// ─── Presentations (presenton) ──────────────────────────────────────

/** One slide outline. Mirrors C# `SlideOutline`. */
data class SlideOutline(val title: String, val body: String, val bullets: List<String>? = null)

/** A generated presentation. Mirrors C# `GeneratedPresentation`. */
data class GeneratedPresentation(val slides: List<SlideOutline>, val theme: String, val format: String)

/** AI presentation generator (presenton pattern). Mirrors C# `IPresentationGenerator`. */
interface IPresentationGenerator {
    val backendId: String
    suspend fun generateAsync(topic: String, targetSlideCount: Int = 10, theme: String? = null): GeneratedPresentation
}

// ─── Job search (career-ops) ────────────────────────────────────────

/** A drafted job application. Mirrors C# `JobApplicationDraft`. */
data class JobApplicationDraft(val resumeText: String, val coverLetterText: String, val keyMatches: List<String>)

/** Job-search pipeline (career-ops). Mirrors C# `IJobSearchPipeline`. */
interface IJobSearchPipeline {
    val backendId: String
    suspend fun draftApplicationAsync(roleDescription: String, candidateProfileText: String): JobApplicationDraft
}

// ─── Memory upgrades (mempalace + HippoRAG) ─────────────────────────

/** One long-term memory item. Mirrors C# `MemoryItem`. */
data class MemoryItem(val id: String, val text: String, val metadata: Map<String, String>? = null)

/** One recalled memory hit. Mirrors C# `MemoryHit`. */
data class MemoryHit(val item: MemoryItem, val score: Float)

/** MemPalace-pattern long-term memory. Mirrors C# `IMemPalaceStore`. */
interface IMemPalaceStore {
    val backendId: String
    suspend fun upsertAsync(item: MemoryItem)
    suspend fun recallAsync(query: String, topK: Int = 5): List<MemoryHit>
}

/** HippoRAG-pattern multi-hop memory. Mirrors C# `IHippoRagStore`. */
interface IHippoRagStore {
    val backendId: String
    suspend fun indexAsync(item: MemoryItem)
    suspend fun multiHopRecallAsync(query: String, topK: Int = 5): List<MemoryHit>
}

// ─── Swarm (MiroFish) ───────────────────────────────────────────────

/** One swarm peer. Mirrors C# `SwarmPeer`. */
data class SwarmPeer(val peerId: String, val capability: String, val health: Float)

/** Multi-device coordination over AetherNet (MiroFish-pattern). Mirrors C# `ISwarmCoordinator`. */
interface ISwarmCoordinator {
    val backendId: String
    suspend fun listPeersAsync(): List<SwarmPeer>
    suspend fun chooseDelegateAsync(capability: String): String?
}

// ─── Personal LoRA (RT-10) ──────────────────────────────────────────

/** A LoRA training summary. Mirrors C# `LoRATrainingSummary`. */
data class LoRATrainingSummary(val adapterId: String, val stepsTrained: Int, val finalLoss: Float)

/** On-device personalisation via LoRA fine-tuning (RT-10). Mirrors C# `IPersonalLoRA`. */
interface IPersonalLoRA {
    val backendId: String
    suspend fun trainAsync(adapterId: String, conversationSamples: List<String>): LoRATrainingSummary
    suspend fun loadAdapterAsync(adapterId: String)
    suspend fun unloadAdapterAsync(adapterId: String)
}

// =====================================================================
// In-memory implementations (InMemoryDomain.cs)
// =====================================================================

// ─── Food ───────────────────────────────────────────────────────────
/** Substitute-by-registered-name food embeddings. Mirrors C# `InMemoryFoodEmbeddings`. */
class InMemoryFoodEmbeddings : IFoodEmbeddings {
    private val embeds = ConcurrentHashMap<String, FloatArray>()
    private val subs = ConcurrentHashMap<String, MutableList<Ingredient>>()

    override val backendId: String get() = "in-memory"

    fun registerEmbedding(name: String, v: FloatArray) {
        embeds[name.lowercase()] = v
    }

    fun registerSubstitute(name: String, alt: Ingredient) {
        subs.getOrPut(name.lowercase()) { mutableListOf() }.add(alt)
    }

    override suspend fun embedAsync(i: Ingredient): FloatArray {
        embeds[i.name.lowercase()]?.let { return it }
        // Deterministic hash-based 8-dim vector if none registered.
        val v = FloatArray(8)
        val h = stableHash(i.name)
        for (k in 0 until 8) v[k] = ((h shr (k * 4)) and 0xF) / 15f
        return v
    }

    override suspend fun substitutesAsync(i: Ingredient, topK: Int): List<Ingredient> {
        if (topK <= 0) throw IndexOutOfBoundsException("topK")
        return subs[i.name.lowercase()]?.take(topK) ?: emptyList()
    }

    private companion object {
        /** Stable FNV-1a hash over the lowercased string (portable + deterministic). */
        fun stableHash(s: String): Int {
            var h = -0x7ee3623b // 2166136261 as Int
            for (c in s.lowercase()) {
                h = h xor c.code
                h *= 0x01000193
            }
            return h
        }
    }
}

// ─── Finance ─────────────────────────────────────────────────────────
/** Substring-scored finance retrieval. Mirrors C# `InMemoryFinanceRetrieval`. */
class InMemoryFinanceRetrieval : IFinanceRetrieval {
    private val corpus = ArrayList<FinanceSnippet>()
    private val lock = Any()

    override val backendId: String get() = "in-memory"

    fun add(s: FinanceSnippet) {
        synchronized(lock) { corpus.add(s) }
    }

    override suspend fun retrieveAsync(query: String, topK: Int): List<FinanceSnippet> {
        if (topK <= 0) throw IndexOutOfBoundsException("topK")
        synchronized(lock) {
            return corpus.filter { it.text.contains(query, ignoreCase = true) }
                .sortedByDescending { it.score }
                .take(topK)
        }
    }
}

/** Multi-pass financial agent over an [IFinanceRetrieval]. Mirrors C# `MultiPassFinancialAgent`. */
class MultiPassFinancialAgent(private val retr: IFinanceRetrieval) : IFinancialAgent {
    override val backendId: String get() = "multi-pass"

    override suspend fun researchAsync(question: String): List<FinanceFinding> {
        val subQuestions = decompose(question)
        val findings = ArrayList<FinanceFinding>()
        for (sub in subQuestions) {
            val snippets = retr.retrieveAsync(sub, 5)
            if (snippets.isEmpty()) continue
            val bySource = snippets.groupBy { it.source }
            for ((source, grp) in bySource) {
                val summary = grp.sortedByDescending { it.score }.take(3).joinToString(" | ") { it.text }
                findings.add(FinanceFinding(subject = sub, summary = summary, citations = listOf(source)))
            }
        }
        return findings
    }

    private companion object {
        fun decompose(question: String): List<String> {
            val subs = ArrayList<String>()
            subs.add(question)
            if (question.contains(" and ", ignoreCase = true)) {
                for (part in question.split(" and ").filter { it.isNotEmpty() }) {
                    if (part.trim().length > 6) subs.add(part.trim())
                }
            }
            if (question.length > 60) {
                subs.add(question.split(",").first().trim())
            }
            return subs.distinct()
        }
    }
}

// ─── Presentations ───────────────────────────────────────────────────
/** Template presentation generator. Mirrors C# `TemplatePresentationGenerator`. */
class TemplatePresentationGenerator : IPresentationGenerator {
    override val backendId: String get() = "template"

    override suspend fun generateAsync(topic: String, targetSlideCount: Int, theme: String?): GeneratedPresentation {
        require(topic.isNotBlank()) { "topic required" }
        if (targetSlideCount <= 0) throw IndexOutOfBoundsException("targetSlideCount")
        val slides = ArrayList<SlideOutline>(targetSlideCount)
        slides.add(SlideOutline(topic, "Overview", listOf("What is $topic", "Why it matters", "What we'll cover")))
        for (i in 2 until targetSlideCount) {
            slides.add(SlideOutline("$topic — Part ${i - 1}", "Detail for part ${i - 1}", listOf("Point A", "Point B", "Point C")))
        }
        slides.add(SlideOutline("Conclusion", "Summary of $topic", listOf("Recap", "Next steps", "Questions")))
        return GeneratedPresentation(slides, theme ?: "default", "markdown")
    }
}

// ─── Job search ──────────────────────────────────────────────────────
/** Keyword-match job-search pipeline. Mirrors C# `TemplateJobSearchPipeline`. */
class TemplateJobSearchPipeline : IJobSearchPipeline {
    override val backendId: String get() = "template"

    override suspend fun draftApplicationAsync(roleDescription: String, candidateProfileText: String): JobApplicationDraft {
        val roleWords = extractKeyWords(roleDescription)
        val candWords = extractKeyWords(candidateProfileText).toHashSet()
        val matches = roleWords.filter { it in candWords }.take(10)
        val resume = "${candidateProfileText.trim()}\n\nMatched skills: ${matches.joinToString(", ")}"
        val cover = "Dear Hiring Team,\n\nI am applying because my background (${matches.take(3).joinToString(", ")}) " +
            "fits the role.\n\nRegards."
        return JobApplicationDraft(resume, cover, matches)
    }

    private companion object {
        val SPLIT = charArrayOf(' ', '\n', '\r', '\t', ',', '.', ';', ':', '(', ')')

        fun extractKeyWords(text: String): List<String> =
            text.split(*SPLIT).filter { it.length > 3 }.map { it.trim().lowercase() }.distinct()
    }
}

// ─── Memory upgrades ─────────────────────────────────────────────────
/** In-memory MemPalace store with recency-decayed substring scoring. Mirrors C# `InMemoryMemPalaceStore`. */
class InMemoryMemPalaceStore : IMemPalaceStore {
    private val items = ConcurrentHashMap<String, MemoryItem>()

    override val backendId: String get() = "in-memory"

    override suspend fun upsertAsync(item: MemoryItem) {
        require(item.id.isNotBlank()) { "Id required" }
        items[item.id] = item
    }

    override suspend fun recallAsync(query: String, topK: Int): List<MemoryHit> {
        if (topK <= 0) throw IndexOutOfBoundsException("topK")
        return items.values
            .map { MemoryHit(it, score(it.text, query)) }
            .filter { it.score > 0 }
            .sortedByDescending { it.score }
            .take(topK)
    }

    internal companion object {
        fun score(body: String, query: String): Float {
            if (body.isEmpty() || query.isEmpty()) return 0f
            val q = query.trim()
            val idx = body.indexOf(q, ignoreCase = true)
            return if (idx < 0) 0f else 1f / (1f + idx)
        }
    }
}

/** In-memory HippoRAG multi-hop store over a MemPalace base. Mirrors C# `InMemoryHippoRagStore`. */
class InMemoryHippoRagStore : IHippoRagStore {
    private val base = InMemoryMemPalaceStore()

    override val backendId: String get() = "in-memory"

    override suspend fun indexAsync(item: MemoryItem) = base.upsertAsync(item)

    override suspend fun multiHopRecallAsync(query: String, topK: Int): List<MemoryHit> {
        val first = base.recallAsync(query, topK)
        if (first.isEmpty()) return first
        val seed = first[0].item.text
        val second = base.recallAsync(seed, topK)
        return (first + second)
            .groupBy { it.item.id }
            .map { it.value.first() }
            .sortedByDescending { it.score }
            .take(topK)
    }
}

// ─── Swarm ───────────────────────────────────────────────────────────
/** In-memory swarm coordinator. Mirrors C# `InMemorySwarmCoordinator`. */
class InMemorySwarmCoordinator : ISwarmCoordinator {
    private val peers = ConcurrentHashMap<String, SwarmPeer>()

    override val backendId: String get() = "in-memory"

    fun register(p: SwarmPeer) {
        peers[p.peerId] = p
    }

    override suspend fun listPeersAsync(): List<SwarmPeer> = peers.values.toList()

    override suspend fun chooseDelegateAsync(capability: String): String? {
        require(capability.isNotBlank()) { "capability required" }
        return peers.values
            .filter { it.capability.equals(capability, ignoreCase = true) }
            .maxByOrNull { it.health }
            ?.peerId
    }
}

// ─── Personal LoRA ───────────────────────────────────────────────────
/** Adapter state snapshot. Mirrors C# `LoRAAdapterState`. */
data class LoRAAdapterState(val adapterId: String, val steps: Int, val finalLoss: Float, val trainedAtUtc: Instant)

/** In-memory adapter manager with a simulated training loop. Mirrors C# `InMemoryPersonalLoRA`. */
class InMemoryPersonalLoRA : IPersonalLoRA {
    private val adapters = ConcurrentHashMap<String, LoRAAdapterState>()
    private val loaded = ConcurrentHashMap<String, Byte>()

    override val backendId: String get() = "in-memory"

    override suspend fun trainAsync(adapterId: String, conversationSamples: List<String>): LoRATrainingSummary {
        require(adapterId.isNotBlank()) { "adapterId required" }
        require(conversationSamples.isNotEmpty()) { "at least one sample required" }

        val steps = conversationSamples.size
        val totalChars = conversationSamples.sumOf { it.length }
        val finalLoss = (1.0 / (1.0 + ln(1.0 + steps)) + 1.0 / (1.0 + totalChars / 1000.0)).toFloat()
        val state = LoRAAdapterState(adapterId, steps, finalLoss, Instant.now())
        adapters[adapterId] = state
        return LoRATrainingSummary(adapterId, steps, finalLoss)
    }

    override suspend fun loadAdapterAsync(adapterId: String) {
        require(adapterId.isNotBlank()) { "adapterId required" }
        if (!adapters.containsKey(adapterId)) {
            throw IllegalStateException("Adapter '$adapterId' not trained.")
        }
        loaded[adapterId] = 1
    }

    override suspend fun unloadAdapterAsync(adapterId: String) {
        require(adapterId.isNotBlank()) { "adapterId required" }
        loaded.remove(adapterId)
    }

    fun isLoaded(adapterId: String): Boolean = loaded.containsKey(adapterId)
    fun stateOf(adapterId: String): LoRAAdapterState? = adapters[adapterId]
}

// =====================================================================
// Null implementations (NullImplementations.cs)
// =====================================================================

/** Fail-safe [IFoodEmbeddings]. Mirrors C# `NullFoodEmbeddings`. */
class NullFoodEmbeddings private constructor() : IFoodEmbeddings {
    override val backendId: String get() = "null"
    override suspend fun embedAsync(ingredient: Ingredient): FloatArray = FloatArray(300)
    override suspend fun substitutesAsync(ingredient: Ingredient, topK: Int): List<Ingredient> = emptyList()

    companion object {
        val Instance = NullFoodEmbeddings()
    }
}

/** Fail-safe [IFinanceRetrieval]. Mirrors C# `NullFinanceRetrieval`. */
class NullFinanceRetrieval private constructor() : IFinanceRetrieval {
    override val backendId: String get() = "null"
    override suspend fun retrieveAsync(query: String, topK: Int): List<FinanceSnippet> = emptyList()

    companion object {
        val Instance = NullFinanceRetrieval()
    }
}

/** Fail-safe [IFinancialAgent]. Mirrors C# `NullFinancialAgent`. */
class NullFinancialAgent private constructor() : IFinancialAgent {
    override val backendId: String get() = "null"
    override suspend fun researchAsync(question: String): List<FinanceFinding> = emptyList()

    companion object {
        val Instance = NullFinancialAgent()
    }
}

/** Fail-safe [IPresentationGenerator]. Mirrors C# `NullPresentationGenerator`. */
class NullPresentationGenerator private constructor() : IPresentationGenerator {
    override val backendId: String get() = "null"
    override suspend fun generateAsync(topic: String, targetSlideCount: Int, theme: String?): GeneratedPresentation =
        GeneratedPresentation(slides = emptyList(), theme = theme ?: "default", format = "json")

    companion object {
        val Instance = NullPresentationGenerator()
    }
}

/** Fail-safe [IJobSearchPipeline]. Mirrors C# `NullJobSearchPipeline`. */
class NullJobSearchPipeline private constructor() : IJobSearchPipeline {
    override val backendId: String get() = "null"
    override suspend fun draftApplicationAsync(roleDescription: String, candidateProfileText: String): JobApplicationDraft =
        JobApplicationDraft(resumeText = "", coverLetterText = "", keyMatches = emptyList())

    companion object {
        val Instance = NullJobSearchPipeline()
    }
}

/** Fail-safe [IMemPalaceStore]. Mirrors C# `NullMemPalaceStore`. */
class NullMemPalaceStore private constructor() : IMemPalaceStore {
    override val backendId: String get() = "null"
    override suspend fun upsertAsync(item: MemoryItem) {}
    override suspend fun recallAsync(query: String, topK: Int): List<MemoryHit> = emptyList()

    companion object {
        val Instance = NullMemPalaceStore()
    }
}

/** Fail-safe [IHippoRagStore]. Mirrors C# `NullHippoRagStore`. */
class NullHippoRagStore private constructor() : IHippoRagStore {
    override val backendId: String get() = "null"
    override suspend fun indexAsync(item: MemoryItem) {}
    override suspend fun multiHopRecallAsync(query: String, topK: Int): List<MemoryHit> = emptyList()

    companion object {
        val Instance = NullHippoRagStore()
    }
}

/** Fail-safe [ISwarmCoordinator]. Mirrors C# `NullSwarmCoordinator`. */
class NullSwarmCoordinator private constructor() : ISwarmCoordinator {
    override val backendId: String get() = "null"
    override suspend fun listPeersAsync(): List<SwarmPeer> = emptyList()
    override suspend fun chooseDelegateAsync(capability: String): String? = null

    companion object {
        val Instance = NullSwarmCoordinator()
    }
}

/** Fail-safe [IPersonalLoRA]. Mirrors C# `NullPersonalLoRA`. */
class NullPersonalLoRA private constructor() : IPersonalLoRA {
    override val backendId: String get() = "null"
    override suspend fun trainAsync(adapterId: String, conversationSamples: List<String>): LoRATrainingSummary =
        LoRATrainingSummary(adapterId, 0, 0f)
    override suspend fun loadAdapterAsync(adapterId: String) {}
    override suspend fun unloadAdapterAsync(adapterId: String) {}

    companion object {
        val Instance = NullPersonalLoRA()
    }
}
