// SocialTest.kt — verifies the CircleAI.Social port against the C# reference.

package com.bhengubv.circleai.social

import com.bhengubv.circleai.companion.support.FakeCompanionSession
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.time.Instant
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertTrue

class SocialTest {

    private val t0 = Instant.parse("2026-01-01T00:00:00Z")

    @Test
    fun `posts reactions follows and feed`() {
        val b = InMemorySocialBoard()
        b.post(SocialPost("p1", "author", "hello", t0, listOf("intro")))
        b.post(SocialPost("p2", "author", "again", t0.plusSeconds(60), emptyList()))
        b.post(SocialPost("p3", "other", "noise", t0, emptyList()))

        b.react(Reaction("p1", "u1", "like", t0))
        b.react(Reaction("p1", "u2", "LIKE", t0)) // case-insensitive count
        assertEquals(2, b.reactionCount("p1", "like"))

        b.follow(Follow("u1", "author", t0))
        assertFailsWith<IllegalStateException> { b.follow(Follow("u1", "u1", t0)) } // self-follow
        assertEquals(listOf("p2", "p1"), b.feedFor("u1").map { it.postId }) // followees only, newest-first
        assertEquals(listOf("u1"), b.followers("author"))
        assertFailsWith<IllegalArgumentException> { b.feedFor("u1", 0) }

        b.unfollow("u1", "author")
        assertTrue(b.feedFor("u1").isEmpty())
    }

    @Test
    fun `domain context and adapter`() = runTest {
        assertTrue(SocialDomainContext.SYSTEM_PROMPT_SNIPPET.startsWith("[DOMAIN: Social]"))
        assertTrue("ASA_Advertising_Code" in SocialDomainContext.complianceFlags)

        val fake = FakeCompanionSession()
        val a = SocialCompanionAdapter(fake)
        a.sendAsync("hi")
        assertTrue(fake.lastMessage!!.startsWith("[DOMAIN: Social]"))
        a.draftPostAsync("launch", "LinkedIn", "professional")
        assertTrue(fake.lastMessage!!.contains("Draft a LinkedIn post on 'launch' in professional voice"))
    }
}
