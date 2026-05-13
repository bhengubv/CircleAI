package com.bhengubv.circleai

import java.time.Instant

enum class SyncDeliveryMode { REALTIME, BEST_EFFORT, BATCH }

data class SyncDelta(
    val deltaId: String,
    val domain: String,
    val entityId: String,
    val timestamp: Instant,
    val payloadJson: String,
    val deliveryMode: SyncDeliveryMode,
    val sequence: Int
)

object SyncDomainKeys {
    const val AFFECT   = "affect"
    const val GOALS    = "goals"
    const val PERSONA  = "persona"
    const val MEMORY   = "memory"
    const val IDENTITY = "identity"
}

interface ISyncChannel {
    suspend fun sendDelta(delta: SyncDelta)
    fun onReceive(handler: suspend (SyncDelta) -> Unit)
}
