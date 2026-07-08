// ProactiveSchedulerTest.kt
//
// Verifies ProactiveScheduler + the source/runner backings against the C#
// reference: refresh snapshots, getNextRun, cron tick firing with per-(context,
// id) last-run tracking, event dispatch, run-by-id, null-runner fail-closed,
// and the in-memory source's upsert/remove/context isolation.

package com.bhengubv.circleai.proactive

import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.time.Instant
import java.time.ZoneOffset
import java.time.ZonedDateTime
import java.util.concurrent.atomic.AtomicInteger
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

class ProactiveSchedulerTest {

    private fun utc(y: Int, mo: Int, d: Int, h: Int, mi: Int): Instant =
        ZonedDateTime.of(y, mo, d, h, mi, 0, 0, ZoneOffset.UTC).toInstant()

    /** Runner that counts every task it runs and reports success. */
    private class CountingRunner : IProactiveTaskRunner {
        val runs = ArrayList<Pair<String, Map<String, String>?>>()
        override val backendId = "counting"
        override suspend fun runAsync(task: ProactiveTask, variables: Map<String, String>?): ProactiveTaskRunResult {
            runs.add(task.id to variables)
            return ProactiveTaskRunResult(task.id, true)
        }
    }

    @Test
    fun `refresh snapshots tasks and errors from the source`() = runTest {
        val src = InMemoryProactiveTaskSource()
        src.upsert(ProactiveTask("t1", ProactiveTrigger(cron = "* * * * *"), payload = "p"))
        src.recordError(ProactiveTaskLoadError("bad", "parse fail"))
        val sched = ProactiveScheduler(src, CountingRunner())
        assertTrue(sched.tasks.isEmpty())
        sched.refreshAsync()
        assertEquals(listOf("t1"), sched.tasks.map { it.id })
        assertEquals(listOf("bad"), sched.loadErrors.map { it.taskId })
    }

    @Test
    fun `getNextRun returns null for non-cron and a time for cron`() = runTest {
        val sched = ProactiveScheduler(NullProactiveTaskSource.Instance, CountingRunner())
        val manual = ProactiveTask("m", ProactiveTrigger(manual = true), "p")
        assertNull(sched.getNextRun(manual, Instant.now()))
        val cron = ProactiveTask("c", ProactiveTrigger(cron = "30 9 * * *"), "p")
        assertEquals(utc(2026, 7, 8, 9, 30), sched.getNextRun(cron, utc(2026, 7, 8, 9, 0)))
    }

    @Test
    fun `tick fires a due cron task once per due minute`() = runTest {
        val src = InMemoryProactiveTaskSource()
        src.upsert(ProactiveTask("hourly", ProactiveTrigger(cron = "0 * * * *"), "p"))
        val runner = CountingRunner()
        val sched = ProactiveScheduler(src, runner)
        sched.refreshAsync()

        // At 10:00 the top-of-hour task is due (anchor = now-1min -> next = 10:00 <= now).
        sched.tickAsync(utc(2026, 7, 8, 10, 0))
        assertEquals(1, runner.runs.size)

        // Ticking again at the same minute must not double-fire (last-run recorded).
        sched.tickAsync(utc(2026, 7, 8, 10, 0))
        assertEquals(1, runner.runs.size)

        // A minute later it is not due again.
        sched.tickAsync(utc(2026, 7, 8, 10, 1))
        assertEquals(1, runner.runs.size)

        // Next top-of-hour fires again.
        sched.tickAsync(utc(2026, 7, 8, 11, 0))
        assertEquals(2, runner.runs.size)
    }

    @Test
    fun `dispatch event fires only matching event tasks with variables`() = runTest {
        val src = InMemoryProactiveTaskSource()
        src.upsert(ProactiveTask("onSave", ProactiveTrigger(onEvent = "note-saved"), "p"))
        src.upsert(ProactiveTask("onDelete", ProactiveTrigger(onEvent = "note-deleted"), "p"))
        val runner = CountingRunner()
        val sched = ProactiveScheduler(src, runner)
        sched.refreshAsync()

        sched.dispatchEventAsync("note-saved", mapOf("id" to "42"))
        assertEquals(listOf("onSave"), runner.runs.map { it.first })
        assertEquals(mapOf("id" to "42"), runner.runs.single().second)
    }

    @Test
    fun `run by id runs the task and reports unknown ids`() = runTest {
        val src = InMemoryProactiveTaskSource()
        src.upsert(ProactiveTask("job", ProactiveTrigger(manual = true), "p"))
        val runner = CountingRunner()
        val sched = ProactiveScheduler(src, runner)
        sched.refreshAsync()

        val ok = sched.runByIdAsync("job")
        assertTrue(ok.success)
        assertEquals(1, runner.runs.size)

        val missing = sched.runByIdAsync("nope")
        assertFalse(missing.success)
        assertTrue(missing.failureMessage!!.contains("No task with id"))
    }

    @Test
    fun `null runner fails closed`() = runTest {
        val src = InMemoryProactiveTaskSource()
        src.upsert(ProactiveTask("job", ProactiveTrigger(manual = true), "p"))
        val sched = ProactiveScheduler(src, NullProactiveTaskRunner.Instance)
        sched.refreshAsync()
        val result = sched.runByIdAsync("job")
        assertFalse(result.success)
        assertTrue(result.failureMessage!!.contains("NullProactiveTaskRunner"))
    }

    @Test
    fun `per-context last-run is isolated across source contexts`() = runTest {
        val src = InMemoryProactiveTaskSource()
        // Same task id in two contexts.
        src.upsert(ProactiveTask("job", ProactiveTrigger(cron = "0 * * * *"), "p", sourceContext = "tenantA"))
        src.upsert(ProactiveTask("job", ProactiveTrigger(cron = "0 * * * *"), "p", sourceContext = "tenantB"))
        val runner = CountingRunner()
        val sched = ProactiveScheduler(src, runner)
        sched.refreshAsync()

        sched.tickAsync(utc(2026, 7, 8, 10, 0))
        // Both tenants' jobs fire independently at the same due minute.
        assertEquals(2, runner.runs.size)
    }

    @Test
    fun `in-memory source upsert and remove work by context`() = runTest {
        val src = InMemoryProactiveTaskSource()
        src.upsert(ProactiveTask("a", ProactiveTrigger(manual = true), "p"))
        src.upsert(ProactiveTask("a", ProactiveTrigger(manual = true), "p", sourceContext = "ctx"))
        assertEquals(2, src.getTasksAsync().size)
        assertTrue(src.remove("a")) // removes the no-context one
        assertEquals(1, src.getTasksAsync().size)
        assertFalse(src.remove("a")) // already gone
        assertTrue(src.remove("a", "ctx"))
        assertTrue(src.getTasksAsync().isEmpty())
    }

    @Test
    fun `delegate runner hands off to the supplied lambda`() = runTest {
        val calls = AtomicInteger()
        val runner = DelegateProactiveTaskRunner { task, _ ->
            calls.incrementAndGet()
            ProactiveTaskRunResult(task.id, true)
        }
        val src = InMemoryProactiveTaskSource()
        src.upsert(ProactiveTask("j", ProactiveTrigger(manual = true), "p"))
        val sched = ProactiveScheduler(src, runner)
        sched.refreshAsync()
        sched.runByIdAsync("j")
        assertEquals(1, calls.get())
    }
}
