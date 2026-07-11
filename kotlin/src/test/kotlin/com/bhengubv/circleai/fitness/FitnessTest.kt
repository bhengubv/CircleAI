// FitnessTest.kt — verifies the CircleAI.Fitness port against the C# reference.

package com.bhengubv.circleai.fitness

import com.bhengubv.circleai.companion.support.FakeCompanionSession
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.time.Instant
import kotlin.test.assertEquals
import kotlin.test.assertTrue

class FitnessTest {

    private val wed = Instant.parse("2026-03-04T10:00:00Z")

    @Test
    fun `workouts this week calories goals and sets`() {
        val b = InMemoryFitnessBoard()
        b.log(Workout("w1", "u1", "run", 30, 300.0, Instant.parse("2026-03-02T08:00:00Z")))
        b.log(Workout("w2", "u1", "lift", 45, 400.0, Instant.parse("2026-03-04T08:00:00Z")))
        b.log(Workout("wOld", "u1", "run", 20, 999.0, Instant.parse("2026-02-20T08:00:00Z")))
        assertEquals(listOf("w1", "w2"), b.workoutsThisWeek("u1", wed).map { it.workoutId }) // ASC
        assertEquals(700.0, b.totalCaloriesSince("u1", Instant.parse("2026-03-01T00:00:00Z")), 1e-9)

        b.setGoal(FitnessGoal("g1", "u1", "bench", 100.0, wed))
        b.setGoal(FitnessGoal("g2", "u2", "squat", 140.0, wed))
        assertEquals(listOf("g1"), b.goalsFor("u1").map { it.goalId })

        b.addSet(ExerciseSet("s1", "w2", "bench", 5, 80.0))
        b.addSet(ExerciseSet("s2", "w2", "row", 8, 60.0))
        assertEquals(2, b.setsFor("w2").size)
        assertTrue(b.setsFor("w1").isEmpty())
    }

    @Test
    fun `domain context and adapter`() = runTest {
        assertTrue(FitnessDomainContext.SYSTEM_PROMPT_SNIPPET.startsWith("[DOMAIN: Fitness]"))
        assertTrue("Not_Medical_Advice" in FitnessDomainContext.complianceFlags)

        val fake = FakeCompanionSession()
        val a = FitnessCompanionAdapter(fake)
        a.streamAsync("hey")
        assertTrue(fake.lastMessage!!.startsWith("[DOMAIN: Fitness]"))
        a.suggestRecoveryProtocolAsync("legs sore", "6")
        assertTrue(fake.lastMessage!!.contains("Suggest recovery protocol for soreness"))
        assertTrue(fake.lastMessage!!.contains("avg sleep 6h"))
    }
}
