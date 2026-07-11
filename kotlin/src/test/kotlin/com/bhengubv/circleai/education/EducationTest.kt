// EducationTest.kt
//
// Verifies the CircleAI.Education port against the C# reference semantics:
//   - addCourse/getCourse; lessonsFor orders by orderIndex ASC
//   - enrol + updateProgress (unknown student throws)
//   - studentsFor filters by course; avgProgressFor is 0.0 for empty else mean
//   - domain-context constants; adapter enrichment + a domain helper

package com.bhengubv.circleai.education

import com.bhengubv.circleai.companion.support.FakeCompanionSession
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.time.Duration
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertNull
import kotlin.test.assertTrue

class EducationTest {

    @Test
    fun `courses lessons ordering and students`() {
        val b = InMemoryEducationBoard()
        b.addCourse(Course("c1", "Maths", "STEM", "Gr7"))
        assertEquals("Maths", b.getCourse("c1")!!.name)
        assertNull(b.getCourse("x"))

        b.addLesson(Lesson("l3", "c1", "Third", Duration.ofMinutes(30), 3))
        b.addLesson(Lesson("l1", "c1", "First", Duration.ofMinutes(30), 1))
        b.addLesson(Lesson("l2", "c1", "Second", Duration.ofMinutes(30), 2))
        b.addLesson(Lesson("lx", "c2", "Other", Duration.ofMinutes(30), 1))
        assertEquals(listOf("l1", "l2", "l3"), b.lessonsFor("c1").map { it.lessonId })
    }

    @Test
    fun `progress update and averages`() {
        val b = InMemoryEducationBoard()
        assertEquals(0.0, b.avgProgressFor("c1"))
        b.enrol(StudentRecord("s1", "A", "c1", 40.0))
        b.enrol(StudentRecord("s2", "B", "c1", 60.0))
        b.enrol(StudentRecord("s3", "C", "c2", 100.0))
        assertEquals(2, b.studentsFor("c1").size)
        assertEquals(50.0, b.avgProgressFor("c1"))

        b.updateProgress("s1", 80.0)
        assertEquals(70.0, b.avgProgressFor("c1"))
        assertFailsWith<IllegalStateException> { b.updateProgress("ghost", 1.0) }
    }

    @Test
    fun `domain context and adapter`() = runTest {
        assertTrue(EducationDomainContext.SYSTEM_PROMPT_SNIPPET.startsWith("[DOMAIN: Education]"))
        assertTrue("CAPS_NCS" in EducationDomainContext.complianceFlags)

        val fake = FakeCompanionSession()
        val a = EducationCompanionAdapter(fake)
        a.sendAsync("hello")
        assertTrue(fake.lastMessage!!.startsWith("[DOMAIN: Education]"))
        a.designLessonPlanAsync("Fractions", "Gr7", 45)
        assertTrue(fake.lastMessage!!.contains("45-minute lesson plan on 'Fractions' for Gr7"))
    }
}
