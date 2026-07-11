// Collaboration.kt
//
// Kotlin port of CircleAI.Collaboration — the C# reference is the EXACT spec
// (Contracts.cs, InMemoryCollaboration.cs, NullImplementations.cs).
//
// Channel / message / presence stores. Messages kept per-channel; presence has
// online + last-seen timestamps.
//
// C# -> Kotlin conventions: DateTimeOffset -> java.time.Instant,
// ValueTask -> suspend, ConcurrentDictionary -> synchronized MutableMap.

package com.bhengubv.circleai.collaboration

import java.time.Instant

// ===========================================================================
// Contracts  (Contracts.cs)
// ===========================================================================

data class Channel(val channelId: String, val name: String, val teamId: String)

data class Message(
    val messageId: String,
    val channelId: String,
    val authorId: String,
    val body: String,
    val atUtc: Instant,
)

interface IChannelStore {
    val backendId: String
    suspend fun get(id: String): Channel?
    suspend fun listForTeam(teamId: String): List<Channel>
}

interface IMessageStore {
    val backendId: String
    suspend fun post(msg: Message): Message
    suspend fun read(channelId: String, limit: Int = 100): List<Message>
}

data class PresenceState(val userId: String, val online: Boolean, val lastSeenUtc: Instant)

interface IPresence {
    val backendId: String
    suspend fun get(userId: String): PresenceState?
}

// ===========================================================================
// In-memory implementations  (InMemoryCollaboration.cs)
// ===========================================================================

class InMemoryChannelStore : IChannelStore {
    private val items = HashMap<String, Channel>()
    private val lock = Any()
    override val backendId: String get() = "in-memory"

    fun upsert(c: Channel) {
        synchronized(lock) { items[c.channelId] = c }
    }

    override suspend fun get(id: String): Channel? {
        require(id.isNotBlank()) { "id required" }
        return synchronized(lock) { items[id] }
    }

    override suspend fun listForTeam(teamId: String): List<Channel> {
        require(teamId.isNotBlank()) { "teamId required" }
        return synchronized(lock) { items.values.filter { it.teamId == teamId }.sortedBy { it.name } }
    }
}

class InMemoryMessageStore : IMessageStore {
    private val byChannel = HashMap<String, MutableList<Message>>()
    private val lock = Any()
    override val backendId: String get() = "in-memory"

    override suspend fun post(msg: Message): Message {
        require(msg.channelId.isNotBlank()) { "ChannelId required" }
        synchronized(lock) { byChannel.getOrPut(msg.channelId) { ArrayList() }.add(msg) }
        return msg
    }

    override suspend fun read(channelId: String, limit: Int): List<Message> {
        require(channelId.isNotBlank()) { "channelId required" }
        return synchronized(lock) {
            val list = byChannel[channelId] ?: return emptyList()
            list.sortedByDescending { it.atUtc }.take(limit)
        }
    }
}

class InMemoryPresence : IPresence {
    private val states = HashMap<String, PresenceState>()
    private val lock = Any()
    override val backendId: String get() = "in-memory"

    fun set(s: PresenceState) {
        synchronized(lock) { states[s.userId] = s }
    }

    override suspend fun get(userId: String): PresenceState? {
        require(userId.isNotBlank()) { "userId required" }
        return synchronized(lock) { states[userId] }
    }
}

// ===========================================================================
// Null implementations  (NullImplementations.cs)
// ===========================================================================

class NullChannelStore private constructor() : IChannelStore {
    override val backendId: String get() = "null"
    override suspend fun get(id: String): Channel? = null
    override suspend fun listForTeam(teamId: String): List<Channel> = emptyList()

    companion object {
        val Instance = NullChannelStore()
    }
}

class NullMessageStore private constructor() : IMessageStore {
    override val backendId: String get() = "null"
    override suspend fun post(msg: Message): Message = msg
    override suspend fun read(channelId: String, limit: Int): List<Message> = emptyList()

    companion object {
        val Instance = NullMessageStore()
    }
}

class NullPresence private constructor() : IPresence {
    override val backendId: String get() = "null"
    override suspend fun get(userId: String): PresenceState? = null

    companion object {
        val Instance = NullPresence()
    }
}
