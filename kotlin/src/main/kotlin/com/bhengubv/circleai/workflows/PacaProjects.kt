// PacaProjects.kt
//
// Kotlin port of CircleAI.Workflows/PacaProjects.cs.
//
// (3.3.0) Project + task primitives ported from paca. Auto-generates task IDs
// as <PROJECT_PREFIX>-N. Soft deletes via deletedAtUtc. Row-level project
// scoping via every query taking a projectId.
//
// C# -> Kotlin conventions:
//   ConcurrentDictionary     -> java.util.concurrent.ConcurrentHashMap
//   Func<DateTimeOffset>     -> () -> Instant
//   record `with { ... }`    -> data class copy(...)

package com.bhengubv.circleai.workflows

import java.time.Instant
import java.util.concurrent.ConcurrentHashMap

/**
 * (3.3.0) A workspace that contains tasks.
 *
 * @property id Stable project id.
 * @property name Display name.
 * @property prefix Task-id prefix (e.g. "PACA").
 * @property settingsJson Free-form JSON configuration bag.
 * @property createdAtUtc Creation timestamp.
 * @property deletedAtUtc Soft-delete timestamp; null = live.
 */
data class PacaProject(
    val id: String,
    val name: String,
    val prefix: String,
    val settingsJson: String,
    val createdAtUtc: Instant,
    val deletedAtUtc: Instant?,
)

/**
 * (3.3.0) A unit of work inside a project.
 *
 * @property projectId Owning project.
 * @property number Sequential id within the project (PACA-1, PACA-2, …).
 * @property title Short title.
 * @property descriptionJson Rich-text JSON body (BlockNote shape).
 * @property status Current status name.
 * @property createdAtUtc Creation timestamp.
 * @property deletedAtUtc Soft-delete timestamp; null = live.
 */
data class PacaTask(
    val projectId: String,
    val number: Int,
    val title: String,
    val descriptionJson: String,
    val status: String,
    val createdAtUtc: Instant,
    val deletedAtUtc: Instant?,
) {
    fun reference(prefix: String): String = "$prefix-$number"
}

/**
 * (3.3.0) In-memory project + task store. Replace for production storage.
 */
class InMemoryPacaStore(private val clock: () -> Instant = { Instant.now() }) {

    private val projects = ConcurrentHashMap<String, PacaProject>()
    private val tasksByProject = ConcurrentHashMap<String, MutableList<PacaTask>>()
    private val nextNumber = ConcurrentHashMap<String, Int>()
    private val numberLock = Any()

    /** (3.3.0) Create a new project. Throws if the id already exists. */
    fun createProject(id: String, name: String, prefix: String, settingsJson: String? = null): PacaProject {
        require(id.isNotBlank()) { "id required" }
        require(name.isNotBlank()) { "name required" }
        require(prefix.isNotBlank()) { "prefix required" }

        val project = PacaProject(
            id = id,
            name = name,
            prefix = prefix,
            settingsJson = settingsJson ?: "{}",
            createdAtUtc = clock(),
            deletedAtUtc = null,
        )

        if (projects.putIfAbsent(id, project) != null) {
            throw IllegalStateException("Project '$id' already exists.")
        }
        tasksByProject[id] = ArrayList()
        nextNumber[id] = 1
        return project
    }

    /** (3.3.0) Get a live project by id (excludes soft-deleted). */
    fun getProject(id: String): PacaProject? =
        projects[id]?.takeIf { it.deletedAtUtc == null }

    /** (3.3.0) Soft-delete a project. Idempotent. */
    fun deleteProject(id: String) {
        val existing = projects[id] ?: return
        if (existing.deletedAtUtc != null) return
        projects[id] = existing.copy(deletedAtUtc = clock())
    }

    /** (3.3.0) Update the JSON settings bag on a project. */
    fun updateProjectSettings(projectId: String, newSettingsJson: String?): PacaProject {
        val existing = getProject(projectId) ?: throw IllegalStateException("Project '$projectId' not found.")
        val updated = existing.copy(settingsJson = newSettingsJson ?: "{}")
        projects[projectId] = updated
        return updated
    }

    /** (3.3.0) Add a task to a project. Auto-numbers it. */
    fun addTask(projectId: String, title: String?, descriptionJson: String? = null, status: String = "todo"): PacaTask {
        getProject(projectId) ?: throw IllegalStateException("Project '$projectId' not found.")
        val number: Int
        synchronized(numberLock) {
            number = nextNumber[projectId] ?: 1
            nextNumber[projectId] = number + 1
        }
        val task = PacaTask(
            projectId = projectId,
            number = number,
            title = title ?: "",
            descriptionJson = descriptionJson ?: "{}",
            status = status,
            createdAtUtc = clock(),
            deletedAtUtc = null,
        )

        val list = tasksByProject.getValue(projectId)
        synchronized(list) { list.add(task) }
        return task
    }

    /** (3.3.0) List live tasks for a project, ordered by number ascending. */
    fun listTasks(projectId: String): List<PacaTask> {
        val list = tasksByProject[projectId] ?: return emptyList()
        synchronized(list) {
            return list.filter { it.deletedAtUtc == null }.sortedBy { it.number }
        }
    }

    /** (3.3.0) Find one task by reference like "PACA-3". */
    fun getTaskByReference(projectId: String, reference: String): PacaTask? {
        val project = getProject(projectId) ?: return null
        val expectedPrefix = project.prefix + "-"
        if (!reference.startsWith(expectedPrefix, ignoreCase = true)) return null
        val n = reference.substring(expectedPrefix.length).toIntOrNull() ?: return null
        val list = tasksByProject[projectId] ?: return null
        synchronized(list) {
            return list.firstOrNull { it.number == n && it.deletedAtUtc == null }
        }
    }

    /** (3.3.0) Update a task in place. Caller mutates via copy(). */
    fun updateTask(updated: PacaTask) {
        val list = tasksByProject[updated.projectId] ?: return
        synchronized(list) {
            for (i in list.indices) {
                if (list[i].number == updated.number) {
                    list[i] = updated
                    return
                }
            }
        }
    }

    /** (3.3.0) Soft-delete a task. */
    fun deleteTask(projectId: String, number: Int) {
        val list = tasksByProject[projectId] ?: return
        synchronized(list) {
            for (i in list.indices) {
                if (list[i].number == number) {
                    list[i] = list[i].copy(deletedAtUtc = clock())
                    return
                }
            }
        }
    }
}
