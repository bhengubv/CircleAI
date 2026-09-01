// InMemoryIdentityStore.kt
//
// Identities and their devices, in memory.
//
// Ported from src/CircleAI.Identity/InMemoryIdentityStore.cs.

package com.bhengubv.circleai.identity

class InMemoryIdentityStore : IIdentityStore {

    /*
     * Guards both maps.
     *
     * A `@Synchronized` suspend function is refused by Kotlin, and rightly:
     * the annotation holds a monitor for the whole call, and a suspending
     * call can resume on a different thread — which would release a lock
     * the releasing thread never took. This block is held only across the
     * map access, never across a suspension point.
     */
    private val lock = Any()

    private val identities = LinkedHashMap<String, CircleIdentity>()
    private val devices = LinkedHashMap<String, RegisteredDevice>()

    override suspend fun getAsync(identityId: String): CircleIdentity? =
        synchronized(lock) { identities[identityId] }

    override suspend fun saveAsync(identity: CircleIdentity) {
        synchronized(lock) { identities[identity.identityId] = identity }
    }

    /**
     * Sorted by device id so the list does not reorder between calls. A "your
     * devices" screen that shuffles on every refresh looks broken even though
     * nothing changed.
     */
    override suspend fun getDevicesAsync(identityId: String): List<RegisteredDevice> =
        synchronized(lock) {
            devices.values.filter { it.identityId == identityId }.sortedBy { it.deviceId }
        }

    override suspend fun registerDeviceAsync(device: RegisteredDevice) {
        synchronized(lock) { devices[device.deviceId] = device }
    }

    /**
     * Which identity owns this device.
     *
     * A device registered to an identity that was never saved returns null
     * rather than a half-built identity — the device row exists, the person does
     * not, and pretending otherwise puts an empty name on a screen.
     */
    override suspend fun getByDeviceAsync(deviceId: String): CircleIdentity? =
        synchronized(lock) { devices[deviceId]?.let { identities[it.identityId] } }
}
