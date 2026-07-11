// Home.kt
//
// Kotlin port of CircleAI.Home (HomePrimitives.cs + HomeDomainContext.cs +
// HomeCompanionAdapter.cs) — the C# reference is the EXACT spec. A
// deterministic in-memory home-management board: rooms, devices, and
// maintenance tasks.
//
// Fidelity notes:
//   * C# `record` -> Kotlin `data class`; `DateTime` -> `Instant`.
//   * `Toggle`/`CompleteTask` throw on unknown ids and mutate via copy.
//   * `Rooms` orders by Name ASC; `DevicesIn` filters by RoomId; `ActiveDevices`
//     filters IsOn.
//   * `UpcomingTasks(by)` returns not-completed tasks with DueOn <= by, ordered
//     by DueOn ASC.

package com.bhengubv.circleai.home

import com.bhengubv.circleai.companion.CompanionContext
import com.bhengubv.circleai.companion.CompanionProactiveEvent
import com.bhengubv.circleai.companion.CompanionTurn
import com.bhengubv.circleai.companion.ICompanionSession
import com.bhengubv.circleai.companion.InterfaceKind
import kotlinx.coroutines.flow.Flow
import java.time.Instant
import java.util.concurrent.ConcurrentHashMap

// =====================================================================
// Primitives (HomePrimitives.cs)
// =====================================================================

/** A room in the home. Mirrors C# `Room`. */
data class Room(val roomId: String, val name: String, val areaM2: Double)

/** A home device (possibly assigned to a room). Mirrors C# `HomeDevice`. */
data class HomeDevice(val deviceId: String, val name: String, val kind: String, val roomId: String?, val isOn: Boolean)

/** A scheduled maintenance task. Mirrors C# `MaintenanceTask`. */
data class MaintenanceTask(val taskId: String, val description: String, val dueOn: Instant, val completed: Boolean)

/** Deterministic home board. Mirrors C# `IHomeBoard`. */
interface IHomeBoard {
    fun addRoom(r: Room)
    fun getRoom(id: String): Room?
    val rooms: List<Room>
    fun addDevice(d: HomeDevice)
    fun toggle(deviceId: String, on: Boolean)
    fun devicesIn(roomId: String): List<HomeDevice>
    val activeDevices: List<HomeDevice>
    fun scheduleTask(t: MaintenanceTask)
    fun completeTask(taskId: String)
    fun upcomingTasks(by: Instant): List<MaintenanceTask>
}

/** In-memory [IHomeBoard]. Mirrors C# `InMemoryHomeBoard`. */
class InMemoryHomeBoard : IHomeBoard {
    private val rooms_ = ConcurrentHashMap<String, Room>()
    private val devices = ConcurrentHashMap<String, HomeDevice>()
    private val tasks = ConcurrentHashMap<String, MaintenanceTask>()

    override fun addRoom(r: Room) { rooms_[r.roomId] = r }
    override fun getRoom(id: String): Room? = rooms_[id]
    override val rooms: List<Room>
        get() = rooms_.values.sortedBy { it.name }

    override fun addDevice(d: HomeDevice) { devices[d.deviceId] = d }

    override fun toggle(deviceId: String, on: Boolean) {
        val d = devices[deviceId] ?: throw IllegalStateException("Unknown device $deviceId")
        devices[deviceId] = d.copy(isOn = on)
    }

    override fun devicesIn(roomId: String): List<HomeDevice> =
        devices.values.filter { it.roomId == roomId }

    override val activeDevices: List<HomeDevice>
        get() = devices.values.filter { it.isOn }

    override fun scheduleTask(t: MaintenanceTask) { tasks[t.taskId] = t }

    override fun completeTask(taskId: String) {
        val t = tasks[taskId] ?: throw IllegalStateException("Unknown task $taskId")
        tasks[taskId] = t.copy(completed = true)
    }

    override fun upcomingTasks(by: Instant): List<MaintenanceTask> =
        tasks.values.filter { !it.completed && !it.dueOn.isAfter(by) }.sortedBy { it.dueOn }
}

// =====================================================================
// DomainContext (HomeDomainContext.cs)
// =====================================================================

/** Static domain context for Home. Mirrors C# `HomeDomainContext`. */
object HomeDomainContext {
    const val SYSTEM_PROMPT_SNIPPET: String =
        "[DOMAIN: Home] Expert home management assistant. Help with maintenance schedules, renovation " +
            "planning and budgeting, appliance troubleshooting, utility cost optimisation, and smart home " +
            "setup. Practical, no-nonsense advice. Compliance: NHBRC, National Building Regulations, POPIA."

    val complianceFlags: List<String> = listOf("NHBRC", "National_Building_Regs", "POPIA")

    val suggestedTools: List<String> = listOf("home_inventory", "task_manager", "web_search", "calculator")
}

// =====================================================================
// CompanionAdapter (HomeCompanionAdapter.cs)
// =====================================================================

/** Wraps an [ICompanionSession] with the Home snippet + helpers. Mirrors C# `HomeCompanionAdapter`. */
class HomeCompanionAdapter(private val inner: ICompanionSession) : ICompanionSession {
    override val sessionId: String get() = inner.sessionId
    override val identityId: String get() = inner.identityId
    override val interfaceKind: InterfaceKind get() = inner.interfaceKind
    override val history: List<CompanionTurn> get() = inner.history
    override val proactiveEvents: Flow<CompanionProactiveEvent> get() = inner.proactiveEvents

    override fun getContext(): CompanionContext = inner.getContext()
    override suspend fun refreshContextAsync() = inner.refreshContextAsync()
    override suspend fun signalFeedbackAsync(positive: Boolean, note: String?) =
        inner.signalFeedbackAsync(positive, note)
    override fun close() = inner.close()

    override suspend fun sendAsync(message: String): String = inner.sendAsync(enrich(message))
    override fun streamAsync(message: String): Flow<String> = inner.streamAsync(enrich(message))
    override suspend fun agentAsync(instruction: String): String = inner.agentAsync(enrich(instruction))

    private fun enrich(m: String): String = "${HomeDomainContext.SYSTEM_PROMPT_SNIPPET}\n\n$m"

    suspend fun planMaintenanceAsync(homeType: String): String =
        inner.agentAsync("Create an annual home maintenance schedule for a $homeType. Include monthly, quarterly, bi-annual, and annual tasks with estimated time and cost per task.")

    suspend fun estimateRenovationAsync(scope: String, area: String): String =
        inner.agentAsync("Estimate the cost and timeline for this renovation: $scope in $area. Break down labour, materials, and contingency. Identify potential hidden costs.")

    suspend fun scheduleMaintenanceAsync(homeAge: String, climate: String): String =
        inner.agentAsync("Generate a 12-month home maintenance schedule for a $homeAge-year-old home in $climate climate. Monthly tasks + seasonal big-ticket items.")

    suspend fun diagnoseHomeIssueAsync(symptom: String, location: String): String =
        inner.agentAsync("Diagnose home issue: $symptom in $location. List 5 likely causes ranked by probability + a 1-minute check for each.")

    suspend fun designRoomLayoutAsync(roomDimensions: String, primaryUse: String, furnitureList: String): String =
        inner.agentAsync("Design layout for $roomDimensions room, primary use: $primaryUse. Furniture: $furnitureList. Cover circulation, lighting, focal point.")

    suspend fun estimateRenovationCostAsync(scope: String, region: String, finishLevel: String): String =
        inner.agentAsync("Estimate $finishLevel-finish renovation cost for: $scope in $region. Range with 20% contingency + biggest cost drivers.")
}
