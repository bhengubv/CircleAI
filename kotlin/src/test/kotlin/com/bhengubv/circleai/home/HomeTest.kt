// HomeTest.kt
//
// Verifies the CircleAI.Home port against the C# reference:
//   - addRoom/get; Rooms ordered by name; addDevice/toggle (throws on unknown);
//     devicesIn by room; activeDevices filter
//   - scheduleTask/completeTask (throws on unknown); upcomingTasks filters
//     not-completed + DueOn <= by, ordered by DueOn
//   - domain-context constants; adapter enrichment + a domain helper

package com.bhengubv.circleai.home

import com.bhengubv.circleai.companion.support.FakeCompanionSession
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.time.Instant
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertTrue

class HomeTest {

    private val t0 = Instant.parse("2026-07-01T00:00:00Z")

    @Test
    fun `rooms devices and toggles`() {
        val b = InMemoryHomeBoard()
        b.addRoom(Room("r2", "Kitchen", 20.0))
        b.addRoom(Room("r1", "Bedroom", 15.0))
        assertEquals(listOf("Bedroom", "Kitchen"), b.rooms.map { it.name }) // ordered
        assertEquals("Kitchen", b.getRoom("r2")!!.name)

        b.addDevice(HomeDevice("d1", "Lamp", "light", "r1", false))
        b.addDevice(HomeDevice("d2", "Fridge", "appliance", "r2", true))
        assertEquals(listOf("d1"), b.devicesIn("r1").map { it.deviceId })
        assertEquals(listOf("d2"), b.activeDevices.map { it.deviceId })

        b.toggle("d1", true)
        assertEquals(setOf("d1", "d2"), b.activeDevices.map { it.deviceId }.toSet())
        assertFailsWith<IllegalStateException> { b.toggle("ghost", true) }
    }

    @Test
    fun `maintenance tasks`() {
        val b = InMemoryHomeBoard()
        b.scheduleTask(MaintenanceTask("t1", "Gutters", t0.plusSeconds(86400), false))
        b.scheduleTask(MaintenanceTask("t2", "Filter", t0.plusSeconds(3600), false)) // sooner
        b.scheduleTask(MaintenanceTask("t3", "Later", t0.plusSeconds(86400 * 30), false)) // beyond window

        val by = t0.plusSeconds(86400 * 2)
        assertEquals(listOf("t2", "t1"), b.upcomingTasks(by).map { it.taskId }) // ordered by due, t3 excluded

        b.completeTask("t2")
        assertEquals(listOf("t1"), b.upcomingTasks(by).map { it.taskId }) // completed excluded
        assertFailsWith<IllegalStateException> { b.completeTask("ghost") }
    }

    @Test
    fun `domain context and adapter`() = runTest {
        assertTrue(HomeDomainContext.SYSTEM_PROMPT_SNIPPET.startsWith("[DOMAIN: Home]"))
        assertTrue("NHBRC" in HomeDomainContext.complianceFlags)

        val fake = FakeCompanionSession()
        val a = HomeCompanionAdapter(fake)
        a.sendAsync("hi")
        assertTrue(fake.lastMessage!!.startsWith("[DOMAIN: Home]"))
        a.diagnoseHomeIssueAsync("damp patch", "ceiling")
        assertTrue(fake.lastMessage!!.contains("Diagnose home issue: damp patch"))
    }
}
