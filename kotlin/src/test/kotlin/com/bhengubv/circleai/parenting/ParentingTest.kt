// ParentingTest.kt
//
// Verifies the CircleAI.Parenting port against the C# reference:
//   - addChild/get; Children ordered by name; recordMilestone requires ChildId,
//     milestonesFor newest-first; setRoutine/getRoutine by (child, day);
//     ageAsOf throws on unknown, returns at - DateOfBirth
//   - domain-context constants; adapter enrichment + a domain helper

package com.bhengubv.circleai.parenting

import com.bhengubv.circleai.companion.support.FakeCompanionSession
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.time.DayOfWeek
import java.time.Duration
import java.time.Instant
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertNull
import kotlin.test.assertTrue

class ParentingTest {

    private val dob = Instant.parse("2018-07-01T00:00:00Z")
    private val t0 = Instant.parse("2026-07-01T00:00:00Z")

    @Test
    fun `children milestones and routines`() {
        val b = InMemoryParentingBoard()
        b.addChild(Child("c2", "Zola", dob, "F"))
        b.addChild(Child("c1", "Ayanda", dob, null))
        assertEquals(listOf("Ayanda", "Zola"), b.children.map { it.name })
        assertEquals("Zola", b.getChild("c2")!!.name)

        b.recordMilestone(Milestone("m1", "c1", "motor", "walked", t0))
        b.recordMilestone(Milestone("m2", "c1", "speech", "first word", t0.plusSeconds(60))) // newer
        assertEquals(listOf("m2", "m1"), b.milestonesFor("c1").map { it.milestoneId }) // newest-first
        assertTrue(b.milestonesFor("nobody").isEmpty())
        assertFailsWith<IllegalArgumentException> { b.recordMilestone(Milestone("x", " ", "c", "d", t0)) }

        val routine = Routine("c1", DayOfWeek.MONDAY, listOf(RoutineEntry("07:00", "wake"), RoutineEntry("08:00", "school")))
        b.setRoutine(routine)
        assertEquals(2, b.getRoutine("c1", DayOfWeek.MONDAY)!!.entries.size)
        assertNull(b.getRoutine("c1", DayOfWeek.TUESDAY))

        val age = b.ageAsOf("c1", t0)
        assertEquals(Duration.between(dob, t0), age)
        assertFailsWith<IllegalStateException> { b.ageAsOf("ghost", t0) }
    }

    @Test
    fun `domain context and adapter`() = runTest {
        assertTrue(ParentingDomainContext.SYSTEM_PROMPT_SNIPPET.startsWith("[DOMAIN: Parenting]"))
        assertTrue("Childrens_Act_38_2005" in ParentingDomainContext.complianceFlags)

        val fake = FakeCompanionSession()
        val a = ParentingCompanionAdapter(fake)
        a.sendAsync("hi")
        assertTrue(fake.lastMessage!!.startsWith("[DOMAIN: Parenting]"))
        a.designRoutineAsync("4", "bedtime")
        assertTrue(fake.lastMessage!!.contains("bedtime routine for a 4-year-old"))
    }
}
