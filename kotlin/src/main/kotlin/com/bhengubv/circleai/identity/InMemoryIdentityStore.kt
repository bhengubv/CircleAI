// InMemoryIdentityStore.kt
//
// Identities and their devices, in memory.
//
// Ported from src/CircleAI.Identity/InMemoryIdentityStore.cs.

package com.bhengubv.circleai.identity

class InMemoryIdentityStore : IIdentityStore {

    private val identities = LinkedHashMap<String, CircleIdentity>()
    private val devices = LinkedHashMap<String, RegisteredDevice>()

    @Synchronized
    override suspend fun get(identityId: String): CircleIdentity? = identities[identityId]

    @Synchronized
    override suspend fun save(identity: CircleIdentity) {
        identities[identity.identityId] = identity
    }

    /**
     * Sorted by device id so the list does not reorder between calls. A "your
     * devices" screen that shuffles on every refresh looks broken even though
     * nothing changed.
     */
    @Synchronized
    override suspend fun getDevices(identityId: String): List<RegisteredDevice> =
        devices.values.filter { it.identityId == identityId }.sortedBy { it.deviceId }

    @Synchronized
    override suspend fun registerDevice(device: RegisteredDevice) {
        devices[device.deviceId] = device
    }

    /**
     * Which identity owns this device.
     *
     * A device registered to an identity that was never saved returns null
     * rather than a half-built identity — the device row exists, the person does
     * not, and pretending otherwise puts an empty name on a screen.
     */
    @Synchronized
    override suspend fun getByDevice(deviceId: String): CircleIdentity? =
        devices[deviceId]?.let { identities[it.identityId] }
}
