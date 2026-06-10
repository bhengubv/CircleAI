// AgentMessage.kt
//
// AgentMessage with auto-synthesised correlation ID.

package com.bhengubv.circleai.agents.peer

import java.security.SecureRandom
import java.time.Instant
import java.util.UUID

enum class AgentMessageKind { DISCOVER, GREET, CAPABILITY_QUERY, INVOKE, RESPONSE, DECLINE, HEARTBEAT }

data class AgentMessage(
    val id: UUID,
    val kind: AgentMessageKind,
    val fromUhid: String,
    val toUhid: String,
    val contentType: String,
    val payload: ByteArray,
    val signature: ByteArray,
    val sentAt: Instant,
    val correlationId: String,
) {
    companion object {
        private val random = SecureRandom()

        fun create(
            kind: AgentMessageKind,
            fromUhid: String,
            toUhid: String,
            contentType: String,
            payload: ByteArray,
            signature: ByteArray,
            correlationId: String? = null,
        ): AgentMessage {
            val cid = correlationId ?: run {
                val buf = ByteArray(16)
                random.nextBytes(buf)
                buf.joinToString("") { "%02x".format(it) }
            }
            return AgentMessage(
                id = UUID.randomUUID(),
                kind = kind,
                fromUhid = fromUhid,
                toUhid = toUhid,
                contentType = contentType,
                payload = payload,
                signature = signature,
                sentAt = Instant.now(),
                correlationId = cid,
            )
        }
    }

    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is AgentMessage) return false
        return id == other.id && correlationId == other.correlationId
    }

    override fun hashCode(): Int = id.hashCode() * 31 + correlationId.hashCode()
}
