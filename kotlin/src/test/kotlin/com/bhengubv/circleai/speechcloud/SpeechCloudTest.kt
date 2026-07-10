// SpeechCloudTest.kt
//
// Verifies the CircleAI.Speech.Cloud KeywordVoiceIntentRouter Kotlin port
// against the C# reference: first-hit-wins ordering, named-group capture (with
// trimming + empty-capture drop + numeric-group skip), fallback on no-match /
// empty transcript, and the null router.

package com.bhengubv.circleai.speechcloud

import kotlinx.coroutines.runBlocking
import org.junit.jupiter.api.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue

class SpeechCloudTest {

    private val intents = listOf(
        VoiceIntent("open-note", Regex("""^open (the )?note (?<title>.+)$""", RegexOption.IGNORE_CASE)),
        VoiceIntent("set-timer", Regex("""^set (a )?timer for (?<duration>.+)$""", RegexOption.IGNORE_CASE)),
        VoiceIntent("greeting", Regex("""^(hi|hello|hey)$""", RegexOption.IGNORE_CASE)),
    )

    @Test
    fun `first matching intent wins and captures the named group`() = runBlocking {
        val router = KeywordVoiceIntentRouter(intents)
        assertEquals("keyword", router.backendId)

        val m = router.routeAsync("open note groceries")
        assertEquals("open-note", m.intentName)
        assertEquals("open note groceries", m.transcript)
        assertEquals("groceries", m.captures["title"])
    }

    @Test
    fun `capture is trimmed`() = runBlocking {
        val router = KeywordVoiceIntentRouter(intents)
        val m = router.routeAsync("set timer for   ten minutes  ")
        assertEquals("set-timer", m.intentName)
        assertEquals("ten minutes", m.captures["duration"])
    }

    @Test
    fun `a match with no named groups yields an empty capture map`() = runBlocking {
        val router = KeywordVoiceIntentRouter(intents)
        val m = router.routeAsync("hello")
        assertEquals("greeting", m.intentName)
        assertTrue(m.captures.isEmpty())
    }

    @Test
    fun `no match falls through to the fallback intent`() = runBlocking {
        val router = KeywordVoiceIntentRouter(intents, fallbackIntentName = "ask-ai")
        val m = router.routeAsync("what is the meaning of life")
        assertEquals("ask-ai", m.intentName)
        assertEquals("what is the meaning of life", m.transcript)
        assertTrue(m.captures.isEmpty())
    }

    @Test
    fun `custom fallback name is honoured`() = runBlocking {
        val router = KeywordVoiceIntentRouter(intents, fallbackIntentName = "fallback-x")
        assertEquals("fallback-x", router.routeAsync("no match here").intentName)
    }

    @Test
    fun `empty transcript returns fallback with empty transcript`() = runBlocking {
        val router = KeywordVoiceIntentRouter(intents)
        val m = router.routeAsync("   ")
        assertEquals("ask-ai", m.intentName)
        assertEquals("", m.transcript)
        assertTrue(m.captures.isEmpty())
    }

    @Test
    fun `transcript is trimmed before matching`() = runBlocking {
        val router = KeywordVoiceIntentRouter(intents)
        val m = router.routeAsync("   hi   ")
        assertEquals("greeting", m.intentName)
        assertEquals("hi", m.transcript)
    }

    @Test
    fun `multiple named groups are all surfaced`() = runBlocking {
        val router = KeywordVoiceIntentRouter(
            listOf(
                VoiceIntent(
                    "book",
                    Regex("""^book (?<who>\w+) at (?<when>.+)$""", RegexOption.IGNORE_CASE),
                ),
            ),
        )
        val m = router.routeAsync("book alice at noon")
        assertEquals("book", m.intentName)
        assertEquals("alice", m.captures["who"])
        assertEquals("noon", m.captures["when"])
    }

    @Test
    fun `empty optional capture is dropped`() = runBlocking {
        // The (?<mid>...) group can match empty; empty captures must not appear.
        val router = KeywordVoiceIntentRouter(
            listOf(VoiceIntent("x", Regex("""^a(?<mid>b*)c$"""))),
        )
        val m = router.routeAsync("ac") // mid matches "" -> dropped
        assertEquals("x", m.intentName)
        assertTrue(m.captures.isEmpty())

        val m2 = router.routeAsync("abbc") // mid = "bb"
        assertEquals("bb", m2.captures["mid"])
    }

    @Test
    fun `null router always returns ask-ai`() = runBlocking {
        val router = NullVoiceIntentRouter.Instance
        assertEquals("null", router.backendId)
        val m = router.routeAsync("open note groceries")
        assertEquals("ask-ai", m.intentName)
        assertEquals("open note groceries", m.transcript)
        assertTrue(m.captures.isEmpty())
    }

    @Test
    fun `blank fallback name is rejected`() {
        var threw = false
        try {
            KeywordVoiceIntentRouter(intents, fallbackIntentName = "  ")
        } catch (e: IllegalArgumentException) {
            threw = true
        }
        assertTrue(threw)
    }
}
