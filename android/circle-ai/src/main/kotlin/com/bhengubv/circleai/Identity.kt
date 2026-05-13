package com.bhengubv.circleai

import java.time.Instant

enum class IdentityTier { ANONYMOUS, PSEUDONYMOUS, VERIFIED }

data class RegisteredDevice(
    val deviceId: String,
    val deviceName: String,
    val registeredAt: Instant,
    val isPrimary: Boolean
)

data class CircleIdentity(
    val identityId: String,
    val tier: IdentityTier,
    val displayName: String? = null,
    val createdAt: Instant = Instant.now(),
    val devices: List<RegisteredDevice> = emptyList()
)

interface IIdentityStore {
    suspend fun getIdentity(identityId: String): CircleIdentity?
    suspend fun saveIdentity(identity: CircleIdentity)
}

interface IIdentityProvider {
    suspend fun getCurrentIdentity(): CircleIdentity
    suspend fun registerDevice(device: RegisteredDevice)
}
