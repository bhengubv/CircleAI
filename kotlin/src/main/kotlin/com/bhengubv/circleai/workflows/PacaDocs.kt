// PacaDocs.kt
//
// Kotlin port of CircleAI.Workflows/PacaDocs.cs.
//
// (3.3.0) Project-level living documents with folders, version snapshots,
// activity feed, task/epic linkage, and @mentions of humans + agents (paca
// port).

package com.bhengubv.circleai.workflows

import java.time.Instant
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap

/** (3.3.0) A doc node (folder OR document). */
data class DocNode(
    val id: String,
    val projectId: String,
    val parentId: String?,
    val isFolder: Boolean,
    val title: String,
    val contentJson: String,
    val createdAtUtc: Instant,
    val deletedAtUtc: Instant?,
)

/** (3.3.0) One immutable snapshot of a doc. */
data class DocVersion(
    val versionId: String,
    val docId: String,
    val contentJson: String,
    val savedAtUtc: Instant,
    val authorMemberId: String,
)

/** (3.3.0) One document-activity event. */
data class DocActivity(
    val activityId: String,
    val docId: String,
    val authorMemberId: String,
    val action: String,           // "created" / "edited" / "ai-edited" / "linked" / "commented"
    val detail: String?,
    val at: Instant,
)

/** (3.3.0) Link between a doc section and a task / epic. */
data class DocLink(
    val linkId: String,
    val docId: String,
    val sectionAnchor: String,
    val projectId: String,
    val taskNumber: Int,
)

/** (3.3.0) Result of a cheap two-version line diff. */
data class DocDiff(val added: List<String>, val removed: List<String>)

/** (3.3.0) In-memory doc service. */
class PacaDocService(private val clock: () -> Instant = { Instant.now() }) {

    private val nodes = ConcurrentHashMap<String, DocNode>()
    private val versions = ConcurrentHashMap<String, MutableList<DocVersion>>()
    private val activity = ConcurrentHashMap<String, MutableList<DocActivity>>()
    private val links = ConcurrentHashMap<String, MutableList<DocLink>>()

    fun createFolder(id: String, projectId: String, parentId: String?, title: String): DocNode =
        create(id, projectId, parentId, isFolder = true, title = title, contentJson = "{}", authorMemberId = "system")

    fun createDocument(id: String, projectId: String, parentId: String?, title: String, contentJson: String, authorMemberId: String): DocNode =
        create(id, projectId, parentId, isFolder = false, title = title, contentJson = contentJson, authorMemberId = authorMemberId)

    private fun create(id: String, projectId: String, parentId: String?, isFolder: Boolean, title: String?, contentJson: String?, authorMemberId: String): DocNode {
        require(id.isNotBlank()) { "id required" }
        require(projectId.isNotBlank()) { "projectId required" }
        val node = DocNode(id, projectId, parentId, isFolder, title ?: "", contentJson ?: "{}", clock(), null)
        if (nodes.putIfAbsent(id, node) != null) throw IllegalStateException("Doc '$id' already exists.")

        if (!isFolder) {
            versions[id] = ArrayList()
            activity[id] = arrayListOf(DocActivity(newId(), id, authorMemberId, "created", null, clock()))
        }
        return node
    }

    fun get(id: String): DocNode? = nodes[id]?.takeIf { it.deletedAtUtc == null }

    fun listChildren(projectId: String, parentId: String?): List<DocNode> =
        nodes.values.filter { it.projectId == projectId && it.parentId == parentId && it.deletedAtUtc == null }
            .sortedBy { it.title }

    /**
     * (3.3.0) Edit a document: writes a new version + activity entry, returns
     * mentioned handles.
     */
    fun edit(id: String, newContentJson: String?, authorMemberId: String, isAiEdit: Boolean = false): List<String> {
        val node = nodes[id]
        if (node == null || node.isFolder || node.deletedAtUtc != null) {
            throw IllegalStateException("Doc '$id' is not editable.")
        }

        val updated = node.copy(contentJson = newContentJson ?: "{}")
        nodes[id] = updated

        val version = DocVersion(newId(), id, node.contentJson, clock(), authorMemberId)
        versions.getValue(id).add(version)

        activity.getValue(id).add(
            DocActivity(newId(), id, authorMemberId, if (isAiEdit) "ai-edited" else "edited", null, clock()),
        )

        return extractMentions(newContentJson ?: "")
    }

    fun versions(docId: String): List<DocVersion> = versions[docId]?.toList() ?: emptyList()

    /** (3.3.0) Cheap diff between two versions — returns added + removed text lines. */
    fun diffLines(before: String?, after: String?): DocDiff {
        val b = (before ?: "").split("\n").toHashSet()
        val a = (after ?: "").split("\n").toHashSet()
        return DocDiff(added = (a - b).toList(), removed = (b - a).toList())
    }

    fun activity(docId: String): List<DocActivity> = activity[docId]?.toList() ?: emptyList()

    fun link(docId: String, sectionAnchor: String, projectId: String, taskNumber: Int): DocLink {
        val link = DocLink(newId(), docId, sectionAnchor, projectId, taskNumber)
        val bucket = links.computeIfAbsent(docId) { ArrayList() }
        synchronized(bucket) { bucket.add(link) }
        activity.getValue(docId).add(
            DocActivity(newId(), docId, "system", "linked", "$projectId-$taskNumber@$sectionAnchor", clock()),
        )
        return link
    }

    fun links(docId: String): List<DocLink> = links[docId]?.toList() ?: emptyList()

    companion object {
        private val MENTION_PATTERN = Regex("@([a-zA-Z0-9_\\-]+)")

        private fun newId(): String = UUID.randomUUID().toString().replace("-", "")

        private fun extractMentions(content: String): List<String> {
            val set = LinkedHashSet<String>()
            for (m in MENTION_PATTERN.findAll(content)) {
                set.add(m.groupValues[1])
            }
            // Case-insensitive de-dup like the C# HashSet(OrdinalIgnoreCase).
            val seen = HashSet<String>()
            return set.filter { seen.add(it.lowercase()) }
        }
    }
}
