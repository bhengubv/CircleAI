// Extractor.kt
//
// Knowledge-graph extraction: turn → (subject, predicate, object) triples.
// Kotlin port of Circle.AI.Companion (IKnowledgeGraphExtractor,
// HeuristicKnowledgeGraphExtractor) — the C# reference — mirroring the
// TypeScript pilot (memory/extractor.ts) and Go port (memory_extractor.go) 1:1.
//
// The heuristic extractor is model-free: it links the content words a turn
// mentions to the memory they came from, two-way, so a later question can reach
// an older memory across turns. It is the offline counterpart to the LLM-based
// extractor (same interface, no network) — the graph still fills, just coarsely.

package com.bhengubv.circleai.memory.brain

import java.time.Instant

/** Turns a conversation turn into knowledge-graph triples. */
interface IKnowledgeGraphExtractor {
    suspend fun extractFromTurnAsync(
        userText: String,
        assistantText: String,
        sourceEpisodeId: String?,
    ): List<KnowledgeTriple>
}

/** Model-free extractor: links a turn's content words to their memory, two-way. */
class HeuristicKnowledgeGraphExtractor : IKnowledgeGraphExtractor {

    override suspend fun extractFromTurnAsync(
        userText: String,
        assistantText: String,
        sourceEpisodeId: String?,
    ): List<KnowledgeTriple> {
        // The memory node is identified by the source id when given, else the user's
        // words — so recall can hand back the memory it came from.
        val memory = if (!sourceEpisodeId.isNullOrBlank()) sourceEpisodeId else userText
        if (memory.isBlank()) return emptyList()

        val words = contentWords(userText + " " + assistantText)
        val now = Instant.now()
        val triples = ArrayList<KnowledgeTriple>(words.size * 2)
        for (w in words) {
            // Two-way so a walk can go word → memory → word → memory across turns.
            triples.add(KnowledgeTriple(memory, "mentions", w, sourceEpisodeId, DEFAULT_CONFIDENCE, now))
            triples.add(KnowledgeTriple(w, "seenin", memory, sourceEpisodeId, DEFAULT_CONFIDENCE, now))
        }
        return triples
    }

    private companion object {
        const val DEFAULT_CONFIDENCE = 0.6f

        // Common function words carry no association — drop them so links form on
        // meaningful words (names, places, symptoms, things), not "the" and "my".
        val STOP: Set<String> = hashSetOf(
            "the", "a", "an", "and", "or", "but", "if", "is", "are", "was", "were", "be", "been", "being",
            "to", "of", "in", "on", "at", "for", "with", "from", "by", "as", "into", "about", "over", "under",
            "my", "your", "our", "their", "his", "her", "its", "this", "that", "these", "those",
            "i", "you", "he", "she", "it", "we", "they", "me", "him", "them", "us",
            "do", "does", "did", "done", "have", "has", "had", "will", "would", "can", "could", "should",
            "shall", "may", "might", "must", "not", "no", "yes", "so", "than", "then", "there", "here",
            "how", "why", "what", "when", "where", "who", "which", "whom",
            "am", "get", "got", "really", "just", "very", "much", "many", "some", "any", "all",
        )

        // Split set includes apostrophe, hyphen, slash — matches the C# reference.
        val SEPARATORS = charArrayOf(
            ' ', '\t', '\n', '\r', '.', ',', '?', '!', ';', ':', '\'', '"', '(', ')', '-', '/',
        )

        /** Lowercase, split on separators, drop short/stop words, dedupe preserving order. */
        fun contentWords(text: String): List<String> {
            val seen = HashSet<String>()
            val result = ArrayList<String>()
            for (raw in text.lowercase().split(*SEPARATORS)) {
                if (raw.length < 3 || STOP.contains(raw)) continue
                if (seen.add(raw)) result.add(raw)
            }
            return result
        }
    }
}
