// PacaRealtime.kt
//
// Kotlin port of CircleAI.Workflows/PacaRealtime.cs.
//
// (3.3.0) Realtime fan-out for paca workflows: pub/sub with permission-aware
// rooms, query-invalidation events, collaborative document editing, agent
// activity feed. The Socket.IO / Valkey transport is host-supplied via
// IRealtimeBroadcaster.
//
// C# abstract-record hierarchy -> Kotlin sealed class + data subclasses.
// [ConversationStep] lives in Workflows.kt (same package).

package com.bhengubv.circleai.workflows

import java.time.Instant
import java.util.concurrent.ConcurrentHashMap

/** (3.3.0) Realtime event union. */
sealed class RealtimePacaEvent(val projectId: String, val at: Instant)

class TaskUpdatedEvent(projectId: String, at: Instant, val taskNumber: Int) :
    RealtimePacaEvent(projectId, at)

class QueryInvalidationEvent(projectId: String, at: Instant, val queryKey: String) :
    RealtimePacaEvent(projectId, at)

class DocCursorMoveEvent(projectId: String, at: Instant, val docId: String, val memberId: String, val cursorOffset: Int) :
    RealtimePacaEvent(projectId, at)

class AgentActivityEvent(projectId: String, at: Instant, val agentMemberId: String, val action: String, val detailJson: String) :
    RealtimePacaEvent(projectId, at)

class ConversationStepEvent(projectId: String, at: Instant, val conversationId: String, val step: ConversationStep) :
    RealtimePacaEvent(projectId, at)

/** (3.3.0) Host-supplied broadcaster (Socket.IO / Valkey Streams / etc.). */
interface IRealtimeBroadcaster {
    suspend fun broadcast(room: String, ev: RealtimePacaEvent)
}

/** (3.3.0) Permission check — returns true if the member may join the room. */
fun interface PermissionCheck {
    suspend fun canJoin(memberId: String, room: String): Boolean
}

/**
 * (3.3.0) Realtime hub: routes events into rooms, gates joins with a permission
 * check.
 */
class PacaRealtimeHub(
    private val broadcaster: IRealtimeBroadcaster,
    private val permission: PermissionCheck = PermissionCheck { _, _ -> true },
) {
    private val membersByRoom = ConcurrentHashMap<String, ConcurrentHashMap<String, Byte>>()

    /** (3.3.0) Member tries to join a room. Returns true if permission allowed. */
    suspend fun join(memberId: String, room: String): Boolean {
        if (!permission.canJoin(memberId, room)) return false
        val members = membersByRoom.computeIfAbsent(room) { ConcurrentHashMap() }
        members[memberId] = 1
        return true
    }

    fun leave(memberId: String, room: String) {
        membersByRoom[room]?.remove(memberId)
    }

    fun members(room: String): List<String> =
        membersByRoom[room]?.keys?.toList() ?: emptyList()

    /** (3.3.0) Publish an event to the project's main room. */
    suspend fun publish(ev: RealtimePacaEvent) {
        broadcaster.broadcast("project:${ev.projectId}", ev)
    }

    /** (3.3.0) Publish to a doc collaboration sub-room. */
    suspend fun publishToDoc(docId: String, ev: RealtimePacaEvent) {
        broadcaster.broadcast("doc:$docId", ev)
    }
}

/**
 * (3.3.0) Helper that maps known events to query-invalidation keys for client
 * UIs.
 */
object QueryInvalidation {
    fun keysFor(ev: RealtimePacaEvent): List<String> = when (ev) {
        is TaskUpdatedEvent -> listOf("tasks/${ev.projectId}", "task/${ev.projectId}/${ev.taskNumber}")
        is AgentActivityEvent -> listOf("activity/${ev.projectId}", "agent/${ev.agentMemberId}")
        is ConversationStepEvent -> listOf("conversation/${ev.conversationId}", "conversations/${ev.projectId}")
        is DocCursorMoveEvent -> listOf("doc/${ev.docId}/cursors")
        is QueryInvalidationEvent -> listOf(ev.queryKey)
    }
}
