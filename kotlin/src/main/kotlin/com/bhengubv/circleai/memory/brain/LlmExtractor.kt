// LlmExtractor.kt
//
// LLM-backed knowledge-graph extraction: turn → (subject, predicate, object)
// triples. Kotlin port of CircleAI.Companion (LlmKnowledgeGraphExtractor) — the
// C# reference — mirroring the just-verified TypeScript reference
// (memory/llm_extractor.ts) 1:1.
//
// Uses an on-device IChatGenerator to ask an LLM to extract triples from a
// single conversation turn. The extraction prompt asks for strict-JSON output;
// the parser is defensive against the model emitting extra prose or fences.

package com.bhengubv.circleai.memory.brain

import com.bhengubv.circleai.inference.IChatGenerator
import com.bhengubv.circleai.models.ChatMessage
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.doubleOrNull
import java.time.Instant
import java.util.UUID

/** Confidence used when the model omits (or malforms) the "c" field. */
private const val DEFAULT_CONFIDENCE = 0.75f

private const val SYSTEM_PROMPT =
    "You are a knowledge-graph extractor. Read the conversation turn between USER and ASSISTANT. " +
        "Identify entities (people, places, things, concepts) and facts. " +
        "Output a single JSON array of triples like [{\"s\":\"Subject\",\"p\":\"predicate\",\"o\":\"object\",\"c\":0.0-1.0}, ...]. " +
        "Only output the JSON — no prose, no markdown fences."

private val PARSER_JSON: Json = Json {
    ignoreUnknownKeys = true
    isLenient = true
}

/** Model-backed extractor: asks an LLM for triples and parses its JSON reply. */
class LlmKnowledgeGraphExtractor(private val ai: IChatGenerator) : IKnowledgeGraphExtractor {

    override suspend fun extractFromTurnAsync(
        userText: String,
        assistantText: String,
        sourceEpisodeId: String?,
    ): List<KnowledgeTriple> {
        if (userText.isBlank() && assistantText.isBlank()) return emptyList()

        // Mirrors the C# StringBuilder.AppendLine chain (USER:\n…\nASSISTANT:\n…\n).
        val userMsg = "USER:\n$userText\nASSISTANT:\n$assistantText\n"

        val reply: String = try {
            ai.generateAsync(
                listOf(
                    ChatMessage(id = UUID.randomUUID().toString(), role = "system", content = SYSTEM_PROMPT),
                    ChatMessage(id = UUID.randomUUID().toString(), role = "user", content = userMsg),
                ),
            )
        } catch (ex: Exception) {
            // LLM call failed — degrade gracefully, no triples this turn.
            return emptyList()
        }

        return parseTriples(reply, sourceEpisodeId)
    }

    companion object {
        /**
         * Parses the model's reply into triples. Finds the first `[` and last `]`,
         * JSON-parses the slice, and reads s/p/o/c from each object. Any structural
         * problem yields an empty list rather than throwing.
         */
        fun parseTriples(raw: String, sourceEpisodeId: String?): List<KnowledgeTriple> {
            if (raw.isBlank()) return emptyList()
            val firstBracket = raw.indexOf('[')
            val lastBracket = raw.lastIndexOf(']')
            if (firstBracket < 0 || lastBracket <= firstBracket) return emptyList()
            val jsonSlice = raw.substring(firstBracket, lastBracket + 1)

            return try {
                val parsed = PARSER_JSON.parseToJsonElement(jsonSlice)
                if (parsed !is JsonArray) return emptyList()

                val now = Instant.now()
                val hits = ArrayList<KnowledgeTriple>(parsed.size)
                for (entry in parsed) {
                    if (entry !is JsonObject) continue
                    val s = stringField(entry, "s")
                    val p = stringField(entry, "p")
                    val o = stringField(entry, "o")
                    val cField = entry["c"]
                    val cNum = if (cField is JsonPrimitive && !cField.isString) cField.doubleOrNull else null
                    val c = if (cNum != null && cNum.isFinite()) clamp(cNum.toFloat(), 0f, 1f) else DEFAULT_CONFIDENCE
                    if (s.isNullOrBlank() || p.isNullOrBlank() || o.isNullOrBlank()) continue
                    hits.add(KnowledgeTriple(s, p, o, sourceEpisodeId, c, now))
                }
                hits
            } catch (ex: Exception) {
                // Malformed JSON — return nothing.
                emptyList()
            }
        }

        /** Reads a string-typed field; null when absent or not a JSON string. */
        private fun stringField(obj: JsonObject, key: String): String? {
            val el = obj[key] ?: return null
            if (el !is JsonPrimitive || !el.isString) return null
            return el.content
        }

        private fun clamp(x: Float, lo: Float, hi: Float): Float = maxOf(lo, minOf(hi, x))
    }
}
