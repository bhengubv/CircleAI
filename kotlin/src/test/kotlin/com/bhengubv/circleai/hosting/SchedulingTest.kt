// SchedulingTest.kt
//
// Verifies the Hosting scheduled-task surface against the C# reference:
// InMemoryScheduledTaskStore CRUD + due-job filtering, ScheduledAIService job
// execution (state transitions + next-run recompute + completion event),
// IdleTrigger / ScheduleTrigger firing, and ProactiveReasoningService
// first-trigger-wins check-in generation.

package com.bhengubv.circleai.hosting

import com.bhengubv.circleai.memory.AffectState
import com.bhengubv.circleai.memory.Goal
import com.bhengubv.circleai.memory.GoalPriority
import com.bhengubv.circleai.memory.GoalStatus
import com.bhengubv.circleai.memory.IAffectStore
import com.bhengubv.circleai.memory.IGoalStore
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.time.Duration
import java.time.Instant
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

class SchedulingTest {

    // ── Store ──

    @Test
    fun `store upsert, get, delete, and due-job filtering`() = runTest {
        val store = InMemoryScheduledTaskStore()
        val past = Instant.now().minus(Duration.ofMinutes(1))
        val future = Instant.now().plus(Duration.ofHours(1))

        store.upsertAsync(CronJob("a", "A", "p", "* * * * *", DeliveryTarget.Local, nextRunUtc = past))
        store.upsertAsync(CronJob("b", "B", "p", "* * * * *", DeliveryTarget.Local, nextRunUtc = future))
        store.upsertAsync(CronJob("c", "C", "p", "* * * * *", DeliveryTarget.Local, nextRunUtc = past, isEnabled = false))

        assertEquals(3, store.listAsync().size)
        assertNotNull(store.getAsync("a"))

        // Only enabled + past-due jobs come back.
        val due = store.getDueJobsAsync().map { it.id }.toSet()
        assertEquals(setOf("a"), due)

        store.deleteAsync("a")
        assertNull(store.getAsync("a"))
    }

    // ── ScheduledAIService ──

    @Test
    fun `service executes a due job, marks it succeeded, and recomputes next run`() = runTest {
        val store = InMemoryScheduledTaskStore()
        val past = Instant.now().minus(Duration.ofMinutes(1))
        store.upsertAsync(CronJob("j", "J", "what time is it", "*/5 * * * *", DeliveryTarget.Local, nextRunUtc = past))

        val butler = FakeAIService()
        val service = ScheduledAIService(butler, store)
        val completed = ArrayList<JobCompletedEventArgs>()
        service.onJobCompleted = { completed.add(it) }

        // Drive the poll loop; its first cycle runs immediately.
        service.startAsync()
        // Give the loop a moment to run its first cycle.
        var tries = 0
        while (completed.isEmpty() && tries < 200) {
            kotlinx.coroutines.delay(5)
            tries++
        }
        service.stopAsync()

        assertEquals(listOf("what time is it"), butler.asks)
        assertEquals(1, completed.size)
        val done = completed.first()
        assertEquals(CronJobState.Succeeded, done.job.state)
        assertNotNull(done.job.nextRunUtc)
        assertNull(done.error)

        val stored = store.getAsync("j")!!
        assertEquals(CronJobState.Succeeded, stored.state)
    }

    @Test
    fun `failing butler marks the job failed`() = runTest {
        val store = InMemoryScheduledTaskStore()
        store.upsertAsync(
            CronJob("j", "J", "p", "*/5 * * * *", DeliveryTarget.Local, nextRunUtc = Instant.now().minusSeconds(60)),
        )
        val butler = FakeAIService(failAsk = true)
        val service = ScheduledAIService(butler, store)
        val completed = ArrayList<JobCompletedEventArgs>()
        service.onJobCompleted = { completed.add(it) }

        service.startAsync()
        var tries = 0
        while (completed.isEmpty() && tries < 200) {
            kotlinx.coroutines.delay(5); tries++
        }
        service.stopAsync()

        assertEquals(1, completed.size)
        assertEquals(CronJobState.Failed, completed.first().job.state)
        assertNotNull(completed.first().error)
    }

    // ── Triggers ──

    @Test
    fun `idle trigger fires only past the threshold`() = runTest {
        val trigger = IdleTrigger(Duration.ofHours(4))
        val ctxIdle = ProactiveContext("u", Instant.now(), Duration.ofHours(5), null, emptyList())
        val ctxFresh = ProactiveContext("u", Instant.now(), Duration.ofMinutes(5), null, emptyList())
        assertTrue(trigger.isMetAsync(ctxIdle))
        assertFalse(trigger.isMetAsync(ctxFresh))
        assertEquals("idle", trigger.name)
    }

    @Test
    fun `schedule trigger fires once per day within the window`() = runTest {
        // Build a context whose local time is exactly the trigger minute.
        val now = Instant.now()
        val localNow = now.atZone(java.time.ZoneId.systemDefault())
        val minuteOfDay = localNow.hour * 60 + localNow.minute
        val trigger = ScheduleTrigger(minuteOfDay)

        val ctx = ProactiveContext("u", now, Duration.ZERO, null, emptyList())
        assertTrue(trigger.isMetAsync(ctx))
        // Second call same day -> already fired.
        assertFalse(trigger.isMetAsync(ctx))
    }

    // ── ProactiveReasoningService ──

    private class FakeAffectStore(private val state: AffectState) : IAffectStore {
        override suspend fun loadAsync(userId: String) = state
        override suspend fun saveAsync(state: AffectState) {}
    }

    private class FakeGoalStore(private val goals: List<Goal>) : IGoalStore {
        override suspend fun listAsync(userId: String) = goals
        override suspend fun getAsync(id: String) = goals.firstOrNull { it.id == id }
        override suspend fun upsertAsync(goal: Goal) = goal
        override suspend fun deleteAsync(id: String) {}
        override suspend fun getActiveAsync(userId: String) = goals.filter { it.status == GoalStatus.Active }
    }

    @Test
    fun `proactive reasoning fires the first trigger and generates a check-in`() = runTest {
        val butler = FakeAIService(replyFor = { "check-in!" })
        val affect = AffectState(userId = "u", lastUpdatedUtc = Instant.now().minus(Duration.ofHours(6)))
        val goal = Goal("g", "u", "Learn Kotlin", "desc", GoalStatus.Active, GoalPriority.Normal, Instant.now())

        val svc = ProactiveReasoningService(
            butler = butler,
            goalStore = FakeGoalStore(listOf(goal)),
            affectStore = FakeAffectStore(affect),
            triggers = listOf(IdleTrigger(Duration.ofHours(4))),
        )

        val fired = ArrayList<ProactiveMessageEventArgs>()
        svc.proactiveMessageReady = { fired.add(it) }

        svc.checkAsync("u")

        assertEquals(1, fired.size)
        assertEquals("check-in!", fired.first().message)
        assertEquals("idle", fired.first().triggerName)
        // The prompt handed to the butler mentions the active goal.
        assertTrue(butler.asks.single().contains("Learn Kotlin"))
    }

    @Test
    fun `proactive reasoning with no triggers does nothing`() = runTest {
        val butler = FakeAIService()
        val svc = ProactiveReasoningService(butler, null, null, emptyList())
        val fired = ArrayList<ProactiveMessageEventArgs>()
        svc.proactiveMessageReady = { fired.add(it) }
        svc.checkAsync("u")
        assertTrue(fired.isEmpty())
        assertTrue(butler.asks.isEmpty())
    }
}
