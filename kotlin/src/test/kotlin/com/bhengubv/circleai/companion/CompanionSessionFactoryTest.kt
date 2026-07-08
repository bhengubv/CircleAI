// CompanionSessionFactoryTest.kt
//
// Verifies CompanionSessionFactory against the C# reference: it produces an
// ICompanionSession stamped with the requested identity + surface, enriches the
// display name / preferred language from an identity provider when present,
// falls back to the identityId when absent, rejects a blank id, and the produced
// session is a working brain.CompanionSession (a turn round-trips through it).

package com.bhengubv.circleai.companion

import com.bhengubv.circleai.identity.CircleIdentity
import com.bhengubv.circleai.identity.IIdentityProvider
import com.bhengubv.circleai.identity.IdentityTier
import com.bhengubv.circleai.inference.GenerationOptions
import com.bhengubv.circleai.inference.IChatGenerator
import com.bhengubv.circleai.memory.brain.FusedRecall
import com.bhengubv.circleai.memory.brain.InMemoryEpisodicStore
import com.bhengubv.circleai.models.ChatMessage
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.flow
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.time.Instant
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertNotNull
import kotlin.test.assertTrue

class CompanionSessionFactoryTest {

    private class StubGenerator(private val reply: String) : IChatGenerator {
        override suspend fun generateAsync(messages: List<ChatMessage>, opts: GenerationOptions): String = reply
        override fun streamAsync(messages: List<ChatMessage>, opts: GenerationOptions): Flow<String> =
            flow { emit(reply) }
        override fun close() {}
    }

    private fun factory(identity: IIdentityProvider? = null): CompanionSessionFactory {
        val episodic = InMemoryEpisodicStore()
        return CompanionSessionFactory(
            generator = StubGenerator("hello there"),
            episodic = episodic,
            recall = FusedRecall(episodic),
            identity = identity,
        )
    }

    @Test
    fun `creates a session stamped with identity and surface`() = runTest {
        val session = factory().createAsync("id-123", InterfaceKind.Mobile)
        assertEquals("id-123", session.identityId)
        assertEquals(InterfaceKind.Mobile, session.interfaceKind)
        assertTrue(session.sessionId.isNotBlank())
    }

    @Test
    fun `falls back to identityId as display name when no provider`() = runTest {
        val session = factory().createAsync("id-xyz", InterfaceKind.Web)
        assertEquals("id-xyz", session.getContext().displayName)
    }

    @Test
    fun `enriches display name and language from the identity provider`() = runTest {
        val ident = CircleIdentity(
            identityId = "id-1",
            displayName = "Thabang",
            preferredLanguage = "zu",
            tier = IdentityTier.Verified,
            deviceIds = emptyList(),
            createdAt = Instant.now(),
            lastSeenAt = Instant.now(),
        )
        val provider = object : IIdentityProvider {
            override suspend fun getCurrentIdentityAsync(): CircleIdentity = ident
            override suspend fun isAuthenticatedAsync(): Boolean = true
            override suspend fun createIdentityAsync(displayName: String, preferredLanguage: String?): CircleIdentity = ident
        }
        val session = factory(provider).createAsync("id-1", InterfaceKind.Desktop)
        val ctx = session.getContext()
        assertEquals("Thabang", ctx.displayName)
        assertEquals("zu", ctx.preferredLanguage)
    }

    @Test
    fun `rejects a blank identity id`() = runTest {
        assertFailsWith<IllegalArgumentException> { factory().createAsync("  ", InterfaceKind.Headless) }
    }

    @Test
    fun `produced session is a working companion session`() = runTest {
        val session = factory().createAsync("id-42", InterfaceKind.Ambient)
        val reply = session.sendAsync("hi")
        assertEquals("hello there", reply)
        assertEquals(2, session.history.size) // user + assistant
        assertNotNull(session.history.first { it.role == "assistant" })
    }
}
