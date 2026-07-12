// PacaBoards.kt
//
// Kotlin port of CircleAI.Workflows/PacaBoards.cs.
//
// (3.3.0) Sprintboard surface ported from paca: rich JSON description, custom
// fields, story points, importance, parent/child relations, status columns with
// position-ordered workflow, drag-and-drop status transitions, sprints with
// lifecycle states, Scrumban swimlanes, per-view persistent configs (filters +
// sort + visible fields), tags, lazy-load pagination per column.

package com.bhengubv.circleai.workflows

import java.time.Instant
import java.util.concurrent.ConcurrentHashMap

/** (3.3.0) Sprint lifecycle. */
enum class SprintState { Planning, Active, Completed }

/** (3.3.0) Status column in the workflow. */
data class StatusColumn(
    val name: String,       // "todo" / "in_progress" / "in_review" / "done"
    val category: String,   // "open" / "in-flight" / "review" / "closed" / "cancelled" / "blocked"
    val position: Int,
    val collapsed: Boolean,
)

/** (3.3.0) Sprint. */
data class PacaSprint(
    val id: String,
    val projectId: String,
    val name: String,
    val goal: String,
    val startDate: Instant,
    val endDate: Instant,
    val state: SprintState,
)

/** (3.3.0) Extra board-only metadata on top of [PacaTask]. */
data class TaskBoardMetadata(
    val projectId: String,
    val number: Int,
    val storyPoints: Int,
    val importance: Int,           // 0..5
    val assigneeMemberId: String?,
    val reporterMemberId: String?,
    val parentTaskNumber: Int?,
    val sprintId: String?,
    val tags: List<String>,
    val customFields: Map<String, String>,
    val positionInColumn: Int,
)

/** (3.3.0) A per-user / per-board "named view". */
data class BoardView(
    val name: String,
    val filterTagsCsv: String?,
    val filterAssignee: String?,
    val sortBy: String?,           // "importance" / "story_points" / "newest"
    val sortDescending: Boolean,
    val visibleColumns: List<String>,
    val visibleFields: List<String>,
)

/**
 * (3.3.0) Board service over a project. Sprints + columns + per-task metadata +
 * views.
 */
class PacaBoard(
    private val tasks: InMemoryPacaStore,
    private val clock: () -> Instant = { Instant.now() },
) {
    private val columns = ConcurrentHashMap<String, StatusColumn>()
    private val sprints = ConcurrentHashMap<String, PacaSprint>()
    private val metadata = ConcurrentHashMap<Pair<String, Int>, TaskBoardMetadata>()
    private val views = ConcurrentHashMap<String, BoardView>()

    init {
        addDefaultColumns()
    }

    private fun addDefaultColumns() {
        columns["todo"] = StatusColumn("todo", "open", 0, false)
        columns["in_progress"] = StatusColumn("in_progress", "in-flight", 1, false)
        columns["in_review"] = StatusColumn("in_review", "review", 2, false)
        columns["done"] = StatusColumn("done", "closed", 3, false)
        columns["cancelled"] = StatusColumn("cancelled", "cancelled", 4, false)
        columns["blocked"] = StatusColumn("blocked", "blocked", 5, true)
    }

    val columnsList: List<StatusColumn> get() = columns.values.sortedBy { it.position }

    fun addColumn(col: StatusColumn) {
        columns[col.name] = col
    }

    fun collapseColumn(name: String, collapsed: Boolean) {
        val col = columns[name] ?: return
        columns[name] = col.copy(collapsed = collapsed)
    }

    /** (3.3.0) Move a task between status columns, updating its in-column position. */
    fun moveTask(projectId: String, number: Int, newStatus: String, newPosition: Int) {
        val task = tasks.getTaskByReference(projectId, "$projectId-$number")
            ?: tasks.listTasks(projectId).firstOrNull { it.number == number }
            ?: throw IllegalStateException("Task not found.")
        require(columns.containsKey(newStatus)) { "Unknown status '$newStatus'." }

        tasks.updateTask(task.copy(status = newStatus))
        val meta = getOrCreateMetadata(projectId, number).copy(positionInColumn = newPosition)
        metadata[projectId to number] = meta
    }

    /** (3.3.0) Attach board metadata to an existing task. */
    fun setTaskMetadata(meta: TaskBoardMetadata) {
        metadata[meta.projectId to meta.number] = meta
    }

    fun getTaskMetadata(projectId: String, number: Int): TaskBoardMetadata? =
        metadata[projectId to number]

    /** (3.3.0) Paginated column read for lazy loading. */
    fun tasksInColumn(projectId: String, status: String, skip: Int = 0, take: Int = 50): List<PacaTask> {
        return tasks.listTasks(projectId)
            .filter { it.status == status }
            .sortedBy { getOrCreateMetadata(it.projectId, it.number).positionInColumn }
            .drop(skip).take(take)
    }

    /** (3.3.0) Tasks bucketed by sprint, useful for the Scrumban board. */
    fun tasksInSprint(sprintId: String): List<PacaTask> {
        return metadata.values
            .filter { it.sprintId == sprintId }
            .mapNotNull { m -> tasks.listTasks(m.projectId).firstOrNull { it.number == m.number } }
    }

    /** (3.3.0) Create a sprint in Planning. */
    fun createSprint(id: String, projectId: String, name: String, goal: String, start: Instant, end: Instant): PacaSprint {
        val s = PacaSprint(id, projectId, name, goal, start, end, SprintState.Planning)
        sprints[id] = s
        return s
    }

    fun getSprint(id: String): PacaSprint? = sprints[id]

    fun startSprint(id: String): PacaSprint = transition(id, SprintState.Active)
    fun completeSprint(id: String): PacaSprint = transition(id, SprintState.Completed)

    private fun transition(id: String, to: SprintState): PacaSprint {
        val sprint = sprints[id] ?: throw IllegalStateException("Sprint '$id' not found.")
        val updated = sprint.copy(state = to)
        sprints[id] = updated
        return updated
    }

    /** (3.3.0) Save a named view (filters + sort + visible fields). */
    fun saveView(view: BoardView) {
        views[view.name] = view
    }

    fun getView(name: String): BoardView? = views[name]

    fun listViews(): List<BoardView> = views.values.sortedBy { it.name }

    private fun getOrCreateMetadata(projectId: String, number: Int): TaskBoardMetadata =
        metadata.computeIfAbsent(projectId to number) {
            TaskBoardMetadata(projectId, number, 0, 3, null, null, null, null, emptyList(), emptyMap(), 0)
        }
}
