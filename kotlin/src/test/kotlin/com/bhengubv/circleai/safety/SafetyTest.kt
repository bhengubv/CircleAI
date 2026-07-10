// SafetyTest.kt
//
// Verifies the Safety port against the C# reference: IncidentSeverity order, the
// in-memory board's newest-first ordering + severity filter + hazard overwrite +
// first-contact/insertion-order semantics, the domain-context constants, and the
// companion adapter's domain-context prefix + workflow prompts.

package com.bhengubv.circleai.safety

import com.bhengubv.circleai.companion.CompanionContext
import com.bhengubv.circleai.companion.CompanionProactiveEvent
import com.bhengubv.circleai.companion.CompanionTurn
import com.bhengubv.circleai.companion.ICompanionSession
import com.bhengubv.circleai.companion.InterfaceKind
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.emptyFlow
import kotlinx.coroutines.flow.flow
import kotlinx.coroutines.flow.toList
import kotlinx.coroutines.test.runTest
import java.time.Instant
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

class SafetyTest {

    private fun inc(id: String, sev: IncidentSeverity, at: String) =
        Incident(id, sev, "desc $id", null, null, Instant.parse(at))

    // ── IncidentSeverity ───────────────────────────────────────────────────

    @Test
    fun `IncidentSeverity has four values in ascending declared order`() {
        assertEquals(
            listOf("Info", "Warning", "Critical", "Emergency"),
            IncidentSeverity.entries.map { it.name },
        )
    }

    // ── InMemorySafetyBoard: incidents ─────────────────────────────────────

    @Test
    fun `active returns incidents newest-first`() {
        val b = InMemorySafetyBoard()
        b.log(inc("old", IncidentSeverity.Info, "2026-01-01T00:00:00Z"))
        b.log(inc("new", IncidentSeverity.Warning, "2026-01-03T00:00:00Z"))
        b.log(inc("mid", IncidentSeverity.Critical, "2026-01-02T00:00:00Z"))
        assertEquals(listOf("new", "mid", "old"), b.active.map { it.incidentId })
    }

    @Test
    fun `active is empty on a fresh board`() {
        assertTrue(InMemorySafetyBoard().active.isEmpty())
    }

    @Test
    fun `atOrAboveSeverity filters by ordinal and keeps newest-first`() {
        val b = InMemorySafetyBoard()
        b.log(inc("i", IncidentSeverity.Info, "2026-01-01T00:00:00Z"))
        b.log(inc("w", IncidentSeverity.Warning, "2026-01-02T00:00:00Z"))
        b.log(inc("c", IncidentSeverity.Critical, "2026-01-03T00:00:00Z"))
        b.log(inc("e", IncidentSeverity.Emergency, "2026-01-04T00:00:00Z"))
        assertEquals(
            listOf("e", "c", "w"),
            b.atOrAboveSeverity(IncidentSeverity.Warning).map { it.incidentId },
        )
        assertEquals(listOf("e"), b.atOrAboveSeverity(IncidentSeverity.Emergency).map { it.incidentId })
        assertEquals(4, b.atOrAboveSeverity(IncidentSeverity.Info).size)
    }

    // ── InMemorySafetyBoard: hazards ───────────────────────────────────────

    @Test
    fun `hazards are keyed by id and overwrite, ordered newest-noted first`() {
        val b = InMemorySafetyBoard()
        b.noteHazard(Hazard("h1", "loose wiring", "electrical", Instant.parse("2026-01-01T00:00:00Z")))
        b.noteHazard(Hazard("h2", "wet floor", "slip", Instant.parse("2026-01-02T00:00:00Z")))
        // Overwrite h1 with a newer note.
        b.noteHazard(Hazard("h1", "loose wiring FIXED PENDING", "electrical", Instant.parse("2026-01-03T00:00:00Z")))
        val h = b.hazards
        assertEquals(2, h.size)
        assertEquals(listOf("h1", "h2"), h.map { it.hazardId })
        assertEquals("loose wiring FIXED PENDING", h[0].description)
    }

    // ── InMemorySafetyBoard: contacts ──────────────────────────────────────

    @Test
    fun `contacts preserve insertion order and firstContact is the first added`() {
        val b = InMemorySafetyBoard()
        assertNull(b.firstContact)
        b.addContact(EmergencyContact("c1", "Alice", "0111", "neighbour"))
        b.addContact(EmergencyContact("c2", "Bob", "0222", "sibling"))
        assertEquals("Alice", b.firstContact!!.name)
        assertEquals(listOf("c1", "c2"), b.contacts.map { it.contactId })
    }

    // ── SafetyDomainContext ────────────────────────────────────────────────

    @Test
    fun `domain context snippet and flags match the C-sharp reference`() {
        assertTrue(SafetyDomainContext.systemPromptSnippet.startsWith("[DOMAIN: Safety]"))
        assertTrue(SafetyDomainContext.systemPromptSnippet.contains("10111 (SAPS) or 10177 (ambulance)"))
        assertTrue(SafetyDomainContext.systemPromptSnippet.endsWith("Compliance: POPIA, OHS Act."))
        assertEquals(listOf("POPIA", "OHS_Act", "Emergency_Protocol_10111"), SafetyDomainContext.complianceFlags)
        assertEquals(
            listOf("emergency_contacts", "document_editor", "map", "web_search"),
            SafetyDomainContext.suggestedTools,
        )
    }

    // ── SafetyCompanionAdapter ─────────────────────────────────────────────

    /** Records every prompt the adapter forwards to the inner session. */
    private class RecordingSession : ICompanionSession {
        val sent = ArrayList<String>()
        val agented = ArrayList<String>()
        val streamed = ArrayList<String>()
        override val sessionId: String get() = "inner-sess"
        override val identityId: String get() = "inner-id"
        override val interfaceKind: InterfaceKind get() = InterfaceKind.Mobile
        override val history: List<CompanionTurn> get() = emptyList()
        override val proactiveEvents: Flow<CompanionProactiveEvent> get() = emptyFlow()
        override suspend fun sendAsync(message: String): String { sent.add(message); return "S:$message" }
        override fun streamAsync(message: String): Flow<String> { streamed.add(message); return flow { emit(message) } }
        override suspend fun agentAsync(instruction: String): String { agented.add(instruction); return "A:$instruction" }
        override fun getContext(): CompanionContext =
            CompanionContext("inner-id", "inner-id", null, InterfaceKind.Mobile, "", "", emptyList(), emptyList(), Instant.now())
        override suspend fun refreshContextAsync() {}
        override suspend fun signalFeedbackAsync(positive: Boolean, note: String?) {}
        override fun close() {}
    }

    @Test
    fun `adapter delegates identity + interface to the inner session`() {
        val a = SafetyCompanionAdapter(RecordingSession())
        assertEquals("inner-sess", a.sessionId)
        assertEquals("inner-id", a.identityId)
        assertEquals(InterfaceKind.Mobile, a.interfaceKind)
    }

    @Test
    fun `send prefixes the domain context`() = runTest {
        val inner = RecordingSession()
        SafetyCompanionAdapter(inner).sendAsync("help me")
        assertEquals("${SafetyDomainContext.systemPromptSnippet}\n\nhelp me", inner.sent.single())
    }

    @Test
    fun `stream prefixes the domain context`() = runTest {
        val inner = RecordingSession()
        val out = SafetyCompanionAdapter(inner).streamAsync("stream this").toList()
        assertEquals("${SafetyDomainContext.systemPromptSnippet}\n\nstream this", inner.streamed.single())
        assertEquals(listOf("${SafetyDomainContext.systemPromptSnippet}\n\nstream this"), out)
    }

    @Test
    fun `agent prefixes the domain context`() = runTest {
        val inner = RecordingSession()
        SafetyCompanionAdapter(inner).agentAsync("do a thing")
        assertEquals("${SafetyDomainContext.systemPromptSnippet}\n\ndo a thing", inner.agented.single())
    }

    @Test
    fun `createEmergencyPlan builds the reference prompt without the domain prefix`() = runTest {
        val inner = RecordingSession()
        SafetyCompanionAdapter(inner).createEmergencyPlanAsync("4", "Sandton")
        // The C# workflow calls _i.AgentAsync directly (no domain prefix).
        val p = inner.agented.single()
        assertFalse(p.startsWith("[DOMAIN: Safety]"))
        assertTrue(p.startsWith("Create a personalised emergency preparedness plan for a 4-person household in Sandton."))
        assertTrue(p.contains("go-bag checklist"))
    }

    @Test
    fun `assessSecurity builds the reference prompt`() = runTest {
        val inner = RecordingSession()
        SafetyCompanionAdapter(inner).assessSecurityAsync("townhouse", "back gate")
        val p = inner.agented.single()
        assertTrue(p.startsWith("Assess home security for a townhouse. Concerns: back gate."))
    }

    @Test
    fun `conductRiskAssessment builds the reference prompt`() = runTest {
        val inner = RecordingSession()
        SafetyCompanionAdapter(inner).conductRiskAssessmentAsync("welding", "workshop")
        assertEquals(
            "Conduct a risk assessment for welding in workshop. Hazard, likelihood, severity, controls.",
            inner.agented.single(),
        )
    }

    @Test
    fun `draftEmergencyResponse and briefSafetyToolbox and reviewIncidentReport build reference prompts`() = runTest {
        val inner = RecordingSession()
        val a = SafetyCompanionAdapter(inner)
        a.draftEmergencyResponseAsync("fire", "plant B")
        a.briefSafetyToolboxAsync("lifting", "back strain")
        a.reviewIncidentReportAsync("worker slipped")
        assertEquals(
            "Draft emergency response steps for fire at plant B. Roles, escalation, comms, debrief.",
            inner.agented[0],
        )
        assertEquals(
            "Brief a 5-min toolbox talk for task: lifting. Top hazards: back strain. Controls, PPE, sign-off.",
            inner.agented[1],
        )
        assertTrue(inner.agented[2].startsWith("Review this incident narrative: worker slipped."))
    }
}
