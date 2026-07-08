// InnerMonologue.kt
//
// Kotlin port of the CircleAI.Companion inner-monologue contract and its two
// concrete implementations — the C# reference is the exact spec:
//   IInnerMonologue            (HerJarvisContracts.cs, contract 13)
//   SelfReflection             (record)
//   TemplateInnerMonologue     (HerJarvisRealImplementations.cs, impl 13)
//   ReasoningLoopInnerMonologue(ReasoningLoopInnerMonologue.cs, Phase E1)
//
// Inner monologue = the companion's self-reflection over its current context.
// TemplateInnerMonologue is a deterministic narrative-template reflection with
// no model dependency; ReasoningLoopInnerMonologue drives a reasoning-capable
// LLM and captures its <think> trace as the thought.

package com.bhengubv.circleai.companion.reasoning

import com.bhengubv.circleai.inference.GenerationOptions
import com.bhengubv.circleai.inference.IChatGenerator
import com.bhengubv.circleai.models.ChatFragmentKind
import com.bhengubv.circleai.models.ChatMessage
import java.time.Instant
import java.util.UUID

// ---------------------------------------------------------------------------
// SelfReflection
// ---------------------------------------------------------------------------

/** A single reflective [thought] captured at [at]. Mirrors C# `SelfReflection`. */
data class SelfReflection(
    val thought: String,
    val at: Instant,
)

// ---------------------------------------------------------------------------
// IInnerMonologue
// ---------------------------------------------------------------------------

/** Self-reflection / inner monologue over a JSON context. */
interface IInnerMonologue {
    suspend fun reflectAsync(contextJson: String): SelfReflection
}

// ---------------------------------------------------------------------------
// TemplateInnerMonologue
// ---------------------------------------------------------------------------

/**
 * Narrative-template reflection over the context JSON. Picks one of three
 * frames deterministically from the context's hash, fills `{summary}` with the
 * first dozen words of the de-punctuated JSON and `{direction}` with a
 * keyword-inferred next step.
 *
 * Note on parity: the C# reference selects the frame from
 * `contextJson.GetHashCode()`, which is randomised per .NET process and so is
 * not reproducible across runs by design. We use Kotlin's stable
 * `String.hashCode()` (masked non-negative) so the selection is deterministic
 * and testable; the frame text, summary extraction, and direction inference
 * match the C# exactly.
 */
class TemplateInnerMonologue : IInnerMonologue {

    override suspend fun reflectAsync(contextJson: String): SelfReflection {
        val summary = summarise(contextJson)
        val direction = inferDirection(contextJson)
        val seed = contextJson.hashCode() and Int.MAX_VALUE
        val frame = FRAMES[seed % FRAMES.size]
        val thought = frame.replace("{summary}", summary).replace("{direction}", direction)
        return SelfReflection(thought, Instant.now())
    }

    private companion object {
        val FRAMES = arrayOf(
            "Observation: {summary}. Implication: this likely means {direction}.",
            "Looking at {summary}, the salient pattern is {direction}.",
            "Given {summary}, my next step is to {direction}.",
        )

        // Matches the C# regex character class [\{\}\[\]\"].
        val PUNCT = Regex("[\\{\\}\\[\\]\"]")

        fun summarise(json: String): String {
            val clean = PUNCT.replace(json, " ")
            val words = clean.split(' ').filter { it.isNotEmpty() }.take(12)
            return words.joinToString(" ")
        }

        fun inferDirection(json: String): String = when {
            json.contains("error", ignoreCase = true) -> "diagnose the failure first"
            json.contains("goal", ignoreCase = true) -> "advance toward the stated goal"
            json.contains("user", ignoreCase = true) -> "respond to the user"
            else -> "gather more context"
        }
    }
}

// ---------------------------------------------------------------------------
// ReasoningLoopInnerMonologue
// ---------------------------------------------------------------------------

/**
 * Inner monologue powered by a reasoning-capable LLM. Streams fragments from
 * [llm], routing REASONING fragments into the "thought" and CONTENT fragments
 * into the visible conclusion. Prefers the reasoning trace; falls back to the
 * visible content, then to "(no inner state)". Stream failures are swallowed so
 * a reflection is always produced.
 */
class ReasoningLoopInnerMonologue(private val llm: IChatGenerator) : IInnerMonologue {

    override suspend fun reflectAsync(contextJson: String): SelfReflection {
        val messages = listOf(
            ChatMessage(id = UUID.randomUUID().toString(), role = "system", content = REASONING_SYSTEM_PROMPT),
            ChatMessage(
                id = UUID.randomUUID().toString(),
                role = "user",
                content = "Context (raw JSON):\n$contextJson\n\nReflect on this in 2-3 sentences.",
            ),
        )
        val options = GenerationOptions(maxTokens = 256, temperature = 0.5f, includeReasoning = true)

        val reasoning = StringBuilder()
        val content = StringBuilder()
        try {
            llm.streamFragmentsAsync(messages, options).collect { frag ->
                if (frag.kind == ChatFragmentKind.REASONING) reasoning.append(frag.text)
                else content.append(frag.text)
            }
        } catch (ex: Exception) {
            // Match the C# reference: log and continue with whatever accumulated.
            System.err.println("[ReasoningLoopInnerMonologue] LLM stream failed: ${ex.message}")
        }

        // Prefer the reasoning trace as the "thought"; fall back to visible content.
        var thought = if (reasoning.isNotEmpty()) reasoning.toString().trim() else content.toString().trim()
        if (thought.isEmpty()) thought = "(no inner state)"
        return SelfReflection(thought, Instant.now())
    }

    private companion object {
        const val REASONING_SYSTEM_PROMPT =
            "You are this user's inner monologue. Reason carefully before responding. " +
                "Use <think>...</think> blocks for chain-of-thought. The visible answer " +
                "afterwards should be short and reflective — not a solution, an observation."
    }
}
