// ChatRuntime.kt
//
// Host-neutral chat runtime seam — the Kotlin port of
// CircleAI.Hosting.Chat.IChatRuntime (pulled down verbatim from
// circle-concierge). Zero CircleAI-internal deps: any harness can drive a Neuron
// node through this surface without touching engine internals.

package com.bhengubv.circleai.hosting

import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.flow

/** One turn in a host-neutral conversation. Mirrors ChatTurn(role, content). */
data class ChatTurn(val role: String, val content: String)

/**
 * A host-neutral chat engine. Mirrors CircleAI.Hosting.Chat.IChatRuntime:
 * identity + readiness + a single streaming entrypoint.
 */
interface IChatRuntime {
    /** Stable identifier for this runtime. */
    val id: String

    /** Human-readable engine label (e.g. the resolved model id). */
    val engineLabel: String

    /** True once the underlying engine is loaded and can serve. */
    val isReady: Boolean

    /** Short status string for UIs ("loading model…", "ready", …). */
    val statusMessage: String

    /** Stream an assistant reply for the given turns, piece by piece. */
    fun streamAsync(turns: List<ChatTurn>): Flow<String>
}

/**
 * A chat runtime whose session (KV cache) can be snapshotted to disk and
 * restored — RT-02 durability. Mirrors IPersistableChatRuntime.
 */
interface IPersistableChatRuntime : IChatRuntime {
    /** Where this runtime persists its session snapshot, or null if none. */
    val sessionSnapshotPath: String?

    /** Persist the current session. Returns true on success. */
    suspend fun saveSessionAsync(path: String): Boolean

    /** Restore a previously-saved session. Returns true on success. */
    suspend fun loadSessionAsync(path: String): Boolean
}

/**
 * A runtime that is never ready and streams a single "no engine" notice.
 * Mirrors NullChatRuntime — the safe default when no engine is wired.
 */
class NullChatRuntime : IChatRuntime {
    override val id: String = "null-chat-runtime"
    override val engineLabel: String = "none"
    override val isReady: Boolean = false
    override val statusMessage: String = "No chat engine configured."
    override fun streamAsync(turns: List<ChatTurn>): Flow<String> = flow {
        emit("No chat engine is configured.")
    }
}
