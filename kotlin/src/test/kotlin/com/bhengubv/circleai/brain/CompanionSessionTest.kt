// CompanionSessionTest.kt
//
// Verifies the concrete CompanionSession end-to-end: a turn recalls fused memory
// + the user's own facts into the system prompt, calls the generator, persists
// the exchange, hands it to the background encoder, recalls it on a later turn,
// and streams. Mirrors the TS pilot (tests/companion_session.test.ts) and Go port.

package com.bhengubv.circleai.brain

import com.bhengubv.circleai.companion.InterfaceKind
import com.bhengubv.circleai.companion.brain.CompanionMemoryEncoder
import com.bhengubv.circleai.companion.brain.CompanionSession
import com.bhengubv.circleai.companion.brain.CompanionSessionOptions
import com.bhengubv.circleai.companion.brain.HeuristicBeliefExtractor
import com.bhengubv.circleai.companion.brain.SelfBeliefStore
import com.bhengubv.circleai.inference.GenerationOptions
import com.bhengubv.circleai.inference.IChatGenerator
import com.bhengubv.circleai.memory.brain.EpisodicEntry
import com.bhengubv.circleai.memory.brain.FusedRecall
import com.bhengubv.circleai.memory.brain.HeuristicKnowledgeGraphExtractor
import com.bhengubv.circleai.memory.brain.InMemoryEpisodicStore
import com.bhengubv.circleai.memory.brain.InMemoryKnowledgeGraph
import com.bhengubv.circleai.models.ChatMessage
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.flow
import kotlinx.coroutines.flow.toList
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.time.Instant
import kotlin.test.assertEquals
import kotlin.test.assertTrue

class CompanionSessionTest {

    /** Records the prompt it was handed and returns a canned reply / chunks. */
    private class CapturingGenerator(
        private val reply: String,
        private val chunks: List<String>? = null,
    ) : IChatGenerator {
        @Volatile
        var lastMessages: List<ChatMessage> = emptyList()

        override suspend fun generateAsync(messages: List<ChatMessage>, opts: GenerationOptions): String {
            lastMessages = messages
            return reply
        }

        override fun streamAsync(messages: List<ChatMessage>, opts: GenerationOptions): Flow<String> = flow {
            lastMessages = messages
            for (c in (chunks ?: listOf(reply))) emit(c)
        }

        override fun close() {}
    }

    private suspend fun recordSelfFact(beliefs: SelfBeliefStore, text: String) {
        val bx = HeuristicBeliefExtractor()
        for (b in bx.extractAsync(text, "t0")) beliefs.record(b)
    }

    private fun makeSession(
        generator: IChatGenerator,
        episodic: InMemoryEpisodicStore,
        beliefs: SelfBeliefStore? = null,
        encoder: CompanionMemoryEncoder? = null,
    ): CompanionSession {
        val recall = FusedRecall(episodic, null)
        return CompanionSession(
            generator,
            episodic,
            recall,
            CompanionSessionOptions(
                sessionId = "s1",
                identityId = "u1",
                interfaceKind = InterfaceKind.Mobile,
                beliefs = beliefs,
                encoder = encoder,
            ),
        )
    }

    // ── send path ───────────────────────────────────────────────────────────────

    @Test
    fun `injects recalled memories AND user facts into the system prompt`() = runTest {
        val episodic = InMemoryEpisodicStore()
        episodic.addAsync(
            EpisodicEntry(
                id = "seed1",
                userText = "I have a peanut allergy",
                assistantText = "Noted",
                recordedAtUtc = Instant.parse("2026-01-01T00:00:00Z"),
            ),
        )
        val beliefs = SelfBeliefStore()
        recordSelfFact(beliefs, "i am vegetarian")

        val gen = CapturingGenerator("Here are some options")
        val session = makeSession(gen, episodic, beliefs = beliefs)

        val reply = session.sendAsync("what can I eat?")
        assertEquals("Here are some options", reply)

        val system = gen.lastMessages[0]
        assertEquals("system", system.role)
        assertTrue(system.content.contains("peanut allergy"), "recalled memory should be in the prompt")
        assertTrue(system.content.contains("vegetarian"), "user fact should be in the prompt")

        // The user's actual message is the last turn handed to the generator.
        assertEquals("what can I eat?", gen.lastMessages.last().content)
    }

    @Test
    fun `persists the turn and grows the history`() = runTest {
        val episodic = InMemoryEpisodicStore()
        val session = makeSession(CapturingGenerator("ok"), episodic)

        session.sendAsync("hello")
        assertEquals(1, episodic.countAsync())
        assertEquals(2, session.history.size) // user + assistant
        assertEquals("user", session.history[0].role)
        assertEquals("assistant", session.history[1].role)
    }

    @Test
    fun `recalls a prior turn on a later turn (memory persists across the session)`() = runTest {
        val episodic = InMemoryEpisodicStore()
        val gen = CapturingGenerator("noted")
        val session = makeSession(gen, episodic)

        session.sendAsync("my favourite colour is blue")
        session.sendAsync("what's my favourite colour?")

        val system = gen.lastMessages[0]
        assertTrue(
            system.content.contains("favourite colour is blue"),
            "the earlier turn should be recalled",
        )
    }

    @Test
    fun `hands the turn to the background encoder, filling the graph`() = runTest {
        val episodic = InMemoryEpisodicStore()
        val graph = InMemoryKnowledgeGraph()
        val encoder = CompanionMemoryEncoder(HeuristicKnowledgeGraphExtractor(), graph)
        val session = makeSession(CapturingGenerator("ok"), episodic, encoder = encoder)

        session.sendAsync("remember my dentist appointment")
        encoder.closeAsync()

        assertTrue(
            graph.allTriples().any { it.obj == "dentist" },
            "the encoder should have extracted the turn into the graph",
        )
    }

    // ── stream + context ─────────────────────────────────────────────────────────

    @Test
    fun `streams chunks and still persists the full reply`() = runTest {
        val episodic = InMemoryEpisodicStore()
        val gen = CapturingGenerator("unused", listOf("Hel", "lo"))
        val session = makeSession(gen, episodic)

        val chunks = session.streamAsync("hi").toList()

        assertEquals(listOf("Hel", "lo"), chunks)
        assertEquals(1, episodic.countAsync())
        assertEquals("Hello", session.history[1].content) // accumulated reply persisted
    }

    @Test
    fun `getContext reflects the memories recalled on the last turn`() = runTest {
        val episodic = InMemoryEpisodicStore()
        episodic.addAsync(
            EpisodicEntry(
                id = "seed1",
                userText = "I live in Durban",
                assistantText = "Nice",
                recordedAtUtc = Instant.parse("2026-01-01T00:00:00Z"),
            ),
        )
        val session = makeSession(CapturingGenerator("ok"), episodic)

        session.sendAsync("where do I live?")
        assertTrue(session.getContext().recentMemorySnippets.contains("I live in Durban"))
    }

    @Test
    fun `agentAsync returns a reply and persists (no tool loop in the pilot)`() = runTest {
        val episodic = InMemoryEpisodicStore()
        val session = makeSession(CapturingGenerator("done"), episodic)
        val reply = session.agentAsync("do the thing")
        assertEquals("done", reply)
        assertEquals(1, episodic.countAsync())
    }
}
