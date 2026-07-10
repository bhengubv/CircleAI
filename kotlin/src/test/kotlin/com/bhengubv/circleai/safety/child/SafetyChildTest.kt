// SafetyChildTest.kt
//
// Verifies the Safety.Child port against the C# reference: the trusted-adult ring
// ordering, geofence definition/lookup/overwrite, the Haversine membership test
// (byte-identical R = 6_371_000 m formula), check-in recency + limit validation,
// the domain-context constants, and the companion adapter's prefix + workflows.

package com.bhengubv.circleai.safety.child

import com.bhengubv.circleai.companion.CompanionContext
import com.bhengubv.circleai.companion.CompanionProactiveEvent
import com.bhengubv.circleai.companion.CompanionTurn
import com.bhengubv.circleai.companion.ICompanionSession
import com.bhengubv.circleai.companion.InterfaceKind
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.emptyFlow
import kotlinx.coroutines.flow.flow
import kotlinx.coroutines.test.runTest
import java.time.Instant
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

class SafetyChildTest {

    private fun checkIn(child: String, at: String) =
        CheckIn(child, "ok", null, null, Instant.parse(at))

    // ── Trusted-adult ring ─────────────────────────────────────────────────

    @Test
    fun `ring is ordered by ascending priority`() {
        val b = InMemoryChildSafetyBoard()
        b.addAdult(TrustedAdult("a3", "Carol", "3", "aunt", 3))
        b.addAdult(TrustedAdult("a1", "Alice", "1", "mother", 1))
        b.addAdult(TrustedAdult("a2", "Bob", "2", "father", 2))
        assertEquals(listOf("a1", "a2", "a3"), b.ringOrdered.map { it.adultId })
    }

    @Test
    fun `addAdult overwrites by id`() {
        val b = InMemoryChildSafetyBoard()
        b.addAdult(TrustedAdult("a1", "Alice", "1", "mother", 5))
        b.addAdult(TrustedAdult("a1", "Alice Updated", "1", "mother", 1))
        assertEquals(1, b.ringOrdered.size)
        assertEquals("Alice Updated", b.ringOrdered.single().name)
        assertEquals(1, b.ringOrdered.single().ringPriority)
    }

    // ── Geofences ──────────────────────────────────────────────────────────

    @Test
    fun `defineGeofence and getGeofence round-trip - unknown id is null`() {
        val b = InMemoryChildSafetyBoard()
        val g = Geofence("home", "Home", -26.1, 28.0, 100.0)
        b.defineGeofence(g)
        assertEquals(g, b.getGeofence("home"))
        assertNull(b.getGeofence("nope"))
    }

    @Test
    fun `defineGeofence overwrites by id`() {
        val b = InMemoryChildSafetyBoard()
        b.defineGeofence(Geofence("home", "Home", -26.1, 28.0, 100.0))
        b.defineGeofence(Geofence("home", "Home", -26.1, 28.0, 500.0))
        assertEquals(500.0, b.getGeofence("home")!!.radiusMeters)
    }

    @Test
    fun `isInsideAnyFence uses the Haversine radius`() {
        val b = InMemoryChildSafetyBoard()
        // ~111,320 m per degree of latitude at the equator. 0.001 deg ~= 111.3 m.
        b.defineGeofence(Geofence("small", "Small", 0.0, 0.0, 50.0))
        b.defineGeofence(Geofence("big", "Big", 0.0, 0.0, 200.0))
        // A point 0.001 deg north (~111 m) is outside 50 m but inside 200 m.
        assertTrue(b.isInsideAnyFence(0.001, 0.0))
        // The centre is inside everything.
        assertTrue(b.isInsideAnyFence(0.0, 0.0))
    }

    @Test
    fun `isInsideAnyFence returns false when outside every fence`() {
        val b = InMemoryChildSafetyBoard()
        b.defineGeofence(Geofence("small", "Small", 0.0, 0.0, 50.0))
        // 0.01 deg north (~1113 m) is well outside a 50 m fence.
        assertFalse(b.isInsideAnyFence(0.01, 0.0))
    }

    @Test
    fun `isInsideAnyFence is false with no fences defined`() {
        assertFalse(InMemoryChildSafetyBoard().isInsideAnyFence(0.0, 0.0))
    }

    // ── Check-ins ──────────────────────────────────────────────────────────

    @Test
    fun `recentCheckIns returns newest-first, filtered by child, capped by limit`() {
        val b = InMemoryChildSafetyBoard()
        b.recordCheckIn(checkIn("kid", "2026-01-01T00:00:00Z"))
        b.recordCheckIn(checkIn("kid", "2026-01-03T00:00:00Z"))
        b.recordCheckIn(checkIn("kid", "2026-01-02T00:00:00Z"))
        b.recordCheckIn(checkIn("other", "2026-01-04T00:00:00Z"))
        val recent = b.recentCheckIns("kid")
        assertEquals(3, recent.size)
        assertEquals(
            listOf(
                Instant.parse("2026-01-03T00:00:00Z"),
                Instant.parse("2026-01-02T00:00:00Z"),
                Instant.parse("2026-01-01T00:00:00Z"),
            ),
            recent.map { it.atUtc },
        )
    }

    @Test
    fun `recentCheckIns honours the limit`() {
        val b = InMemoryChildSafetyBoard()
        for (d in 1..5) b.recordCheckIn(checkIn("kid", "2026-01-0${d}T00:00:00Z"))
        assertEquals(2, b.recentCheckIns("kid", 2).size)
        assertEquals(Instant.parse("2026-01-05T00:00:00Z"), b.recentCheckIns("kid", 2).first().atUtc)
    }

    @Test
    fun `recentCheckIns rejects a non-positive limit`() {
        val b = InMemoryChildSafetyBoard()
        assertFailsWith<IllegalArgumentException> { b.recentCheckIns("kid", 0) }
        assertFailsWith<IllegalArgumentException> { b.recentCheckIns("kid", -1) }
    }

    @Test
    fun `recentCheckIns is empty for an unknown child`() {
        assertTrue(InMemoryChildSafetyBoard().recentCheckIns("ghost").isEmpty())
    }

    // ── SafetyChildDomainContext ───────────────────────────────────────────

    @Test
    fun `domain context snippet and flags match the C-sharp reference`() {
        assertTrue(SafetyChildDomainContext.systemPromptSnippet.startsWith("[DOMAIN: Safety.Child]"))
        assertTrue(SafetyChildDomainContext.systemPromptSnippet.contains("SAPS (10111) or Childline (116)"))
        assertTrue(SafetyChildDomainContext.systemPromptSnippet.endsWith("Cybercrimes Act."))
        assertEquals(
            listOf("Childrens_Act_38_2005", "POPIA_Children", "Films_Publications_Act", "Cybercrimes_Act", "Emergency_116"),
            SafetyChildDomainContext.complianceFlags,
        )
        assertEquals(
            listOf("parental_controls", "web_search", "document_editor", "reporting_tools"),
            SafetyChildDomainContext.suggestedTools,
        )
    }

    // ── SafetyChildCompanionAdapter ────────────────────────────────────────

    private class RecordingSession : ICompanionSession {
        val sent = ArrayList<String>()
        val agented = ArrayList<String>()
        override val sessionId: String get() = "inner-sess"
        override val identityId: String get() = "inner-id"
        override val interfaceKind: InterfaceKind get() = InterfaceKind.Web
        override val history: List<CompanionTurn> get() = emptyList()
        override val proactiveEvents: Flow<CompanionProactiveEvent> get() = emptyFlow()
        override suspend fun sendAsync(message: String): String { sent.add(message); return "S:$message" }
        override fun streamAsync(message: String): Flow<String> = flow { emit(message) }
        override suspend fun agentAsync(instruction: String): String { agented.add(instruction); return "A:$instruction" }
        override fun getContext(): CompanionContext =
            CompanionContext("inner-id", "inner-id", null, InterfaceKind.Web, "", "", emptyList(), emptyList(), Instant.now())
        override suspend fun refreshContextAsync() {}
        override suspend fun signalFeedbackAsync(positive: Boolean, note: String?) {}
        override fun close() {}
    }

    @Test
    fun `adapter delegates identity to the inner session`() {
        val a = SafetyChildCompanionAdapter(RecordingSession())
        assertEquals("inner-sess", a.sessionId)
        assertEquals("inner-id", a.identityId)
        assertEquals(InterfaceKind.Web, a.interfaceKind)
    }

    @Test
    fun `send prefixes the child-safety domain context`() = runTest {
        val inner = RecordingSession()
        SafetyChildCompanionAdapter(inner).sendAsync("hi")
        assertEquals("${SafetyChildDomainContext.systemPromptSnippet}\n\nhi", inner.sent.single())
    }

    @Test
    fun `agent prefixes the child-safety domain context`() = runTest {
        val inner = RecordingSession()
        SafetyChildCompanionAdapter(inner).agentAsync("plan")
        assertEquals("${SafetyChildDomainContext.systemPromptSnippet}\n\nplan", inner.agented.single())
    }

    @Test
    fun `setDigitalRules builds the reference prompt without the domain prefix`() = runTest {
        val inner = RecordingSession()
        SafetyChildCompanionAdapter(inner).setDigitalRulesAsync("9")
        val p = inner.agented.single()
        assertFalse(p.startsWith("[DOMAIN: Safety.Child]"))
        assertTrue(p.startsWith("Create age-appropriate digital safety rules for a 9-year-old."))
        assertTrue(p.contains("how to report concerning content."))
    }

    @Test
    fun `educateOnlineRisks builds the reference prompt`() = runTest {
        val inner = RecordingSession()
        SafetyChildCompanionAdapter(inner).educateOnlineRisksAsync("7")
        assertTrue(inner.agented.single().startsWith("Explain online safety concepts appropriate for a 7-year-old."))
    }

    @Test
    fun `designSafetyConversation and assessOnlineRisk build reference prompts`() = runTest {
        val inner = RecordingSession()
        val a = SafetyChildCompanionAdapter(inner)
        a.designSafetyConversationAsync("10", "strangers")
        a.assessOnlineRiskAsync("TikTok", "12", "late-night messaging")
        assertTrue(inner.agented[0].startsWith("Design an age-appropriate safety conversation for 10 on: strangers."))
        assertTrue(inner.agented[1].startsWith("Assess online risk on TikTok for 12-year-old showing late-night messaging."))
    }

    @Test
    fun `verifyTrustedAdults and draftSchoolNotification build reference prompts`() = runTest {
        val inner = RecordingSession()
        val a = SafetyChildCompanionAdapter(inner)
        a.verifyTrustedAdultsAsync("gran, coach, neighbour")
        a.draftSchoolNotificationAsync("bullying", "screenshots")
        assertTrue(inner.agented[0].startsWith("Help vet trusted-adult ring from: gran, coach, neighbour."))
        assertEquals(
            "Draft a school notification about: bullying. Evidence: screenshots. Calm, factual, requesting specific action.",
            inner.agented[1],
        )
    }
}
