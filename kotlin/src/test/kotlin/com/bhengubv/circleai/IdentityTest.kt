// IdentityTest.kt
//
// Verifies IdentityTier enum values and CircleIdentity / RegisteredDevice
// data class construction against the three fixture examples in identity.json.

package com.bhengubv.circleai

import com.bhengubv.circleai.identity.CircleIdentity
import com.bhengubv.circleai.identity.IdentityTier
import com.bhengubv.circleai.identity.RegisteredDevice
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonNull
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.jupiter.api.Test
import java.io.File
import java.time.Instant
import kotlin.test.assertEquals
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

class IdentityTest {

    private val json = Json { ignoreUnknownKeys = true }

    private fun locateFixture(name: String): File {
        // Walk UP from the working directory looking for a `fixtures` dir.
        // The previous version hardcoded a Windows absolute path with a
        // relative fallback that resolved on neither CI nor a Mac, so these
        // tests could only pass on one machine.
        var dir: File? = File(".").absoluteFile
        while (dir != null) {
            val candidate = File(dir, "fixtures/" + name)
            if (candidate.exists()) return candidate
            dir = dir.parentFile
        }
        error("Cannot locate fixture " + name)
    }

    // ── IdentityTier enum ─────────────────────────────────────────────────────

    @Test
    fun `IdentityTier has exactly 3 values`() {
        assertEquals(3, IdentityTier.entries.size)
    }

    @Test
    fun `IdentityTier values are Anonymous Pseudonymous Verified in order`() {
        val values = IdentityTier.entries.map { it.name }
        assertEquals(listOf("Anonymous", "Pseudonymous", "Verified"), values)
    }

    @Test
    fun `IdentityTier ordinals are correct`() {
        assertEquals(0, IdentityTier.Anonymous.ordinal)
        assertEquals(1, IdentityTier.Pseudonymous.ordinal)
        assertEquals(2, IdentityTier.Verified.ordinal)
    }

    // ── Fixture: verified_multi_device ────────────────────────────────────────

    @Test
    fun `fixture verified_multi_device identity parses correctly`() {
        val root = json.parseToJsonElement(locateFixture("identity.json").readText()).jsonObject
        val example = root["examples"]!!.jsonArray.first { it.jsonObject["id"]!!.jsonPrimitive.content == "verified_multi_device" }.jsonObject
        val id = example["identity"]!!.jsonObject

        val identity = CircleIdentity(
            identityId        = id["identityId"]!!.jsonPrimitive.content,
            displayName       = id["displayName"]!!.jsonPrimitive.content,
            preferredLanguage = id["preferredLanguage"]!!.takeIf { it !is JsonNull }?.jsonPrimitive?.content,
            tier              = IdentityTier.valueOf(id["tier"]!!.jsonPrimitive.content),
            deviceIds         = id["deviceIds"]!!.jsonArray.map { it.jsonPrimitive.content },
            createdAt         = Instant.parse(id["createdAt"]!!.jsonPrimitive.content),
            lastSeenAt        = Instant.parse(id["lastSeenAt"]!!.jsonPrimitive.content)
        )

        assertEquals("a1b2c3d4-e5f6-7890-abcd-ef1234567890", identity.identityId)
        assertEquals("Sipho Dlamini", identity.displayName)
        assertEquals("zu", identity.preferredLanguage)
        assertEquals(IdentityTier.Verified, identity.tier)
        assertEquals(3, identity.deviceIds.size)
        assertEquals("d1000000-0000-0000-0000-000000000001", identity.deviceIds[0])
        assertEquals("d3000000-0000-0000-0000-000000000003", identity.deviceIds[2])
        assertEquals(Instant.parse("2025-01-15T08:00:00Z"), identity.createdAt)
        assertEquals(Instant.parse("2026-05-12T09:30:00Z"), identity.lastSeenAt)
    }

    @Test
    fun `fixture verified_multi_device devices parse correctly`() {
        val root = json.parseToJsonElement(locateFixture("identity.json").readText()).jsonObject
        val example = root["examples"]!!.jsonArray.first { it.jsonObject["id"]!!.jsonPrimitive.content == "verified_multi_device" }.jsonObject
        val devicesArray = example["devices"]!!.jsonArray

        val devices = devicesArray.map { element ->
            val d = element.jsonObject
            RegisteredDevice(
                deviceId     = d["deviceId"]!!.jsonPrimitive.content,
                identityId   = d["identityId"]!!.jsonPrimitive.content,
                platform     = d["platform"]!!.jsonPrimitive.content,
                deviceName   = d["deviceName"]!!.takeIf { it !is JsonNull }?.jsonPrimitive?.content,
                registeredAt = Instant.parse(d["registeredAt"]!!.jsonPrimitive.content),
                lastActiveAt = Instant.parse(d["lastActiveAt"]!!.jsonPrimitive.content)
            )
        }

        assertEquals(3, devices.size)
        assertEquals("android",  devices[0].platform)
        assertEquals("Samsung Galaxy S25", devices[0].deviceName)
        assertEquals("watch",    devices[1].platform)
        assertEquals("Galaxy Watch 7", devices[1].deviceName)
        assertEquals("windows",  devices[2].platform)
        assertEquals("Work Laptop", devices[2].deviceName)
    }

    // ── Fixture: pseudonymous_single_device ───────────────────────────────────

    @Test
    fun `fixture pseudonymous_single_device identity parses correctly`() {
        val root = json.parseToJsonElement(locateFixture("identity.json").readText()).jsonObject
        val example = root["examples"]!!.jsonArray.first { it.jsonObject["id"]!!.jsonPrimitive.content == "pseudonymous_single_device" }.jsonObject
        val id = example["identity"]!!.jsonObject

        val identity = CircleIdentity(
            identityId        = id["identityId"]!!.jsonPrimitive.content,
            displayName       = id["displayName"]!!.jsonPrimitive.content,
            preferredLanguage = id["preferredLanguage"]!!.takeIf { it !is JsonNull }?.jsonPrimitive?.content,
            tier              = IdentityTier.valueOf(id["tier"]!!.jsonPrimitive.content),
            deviceIds         = id["deviceIds"]!!.jsonArray.map { it.jsonPrimitive.content },
            createdAt         = Instant.parse(id["createdAt"]!!.jsonPrimitive.content),
            lastSeenAt        = Instant.parse(id["lastSeenAt"]!!.jsonPrimitive.content)
        )

        assertEquals(IdentityTier.Pseudonymous, identity.tier)
        assertEquals("B! User", identity.displayName)
        assertEquals("en", identity.preferredLanguage)
        assertEquals(1, identity.deviceIds.size)
    }

    // ── Fixture: anonymous_iot ────────────────────────────────────────────────

    @Test
    fun `fixture anonymous_iot has null preferredLanguage and null deviceName`() {
        val root = json.parseToJsonElement(locateFixture("identity.json").readText()).jsonObject
        val example = root["examples"]!!.jsonArray.first { it.jsonObject["id"]!!.jsonPrimitive.content == "anonymous_iot" }.jsonObject
        val id = example["identity"]!!.jsonObject

        val identity = CircleIdentity(
            identityId        = id["identityId"]!!.jsonPrimitive.content,
            displayName       = id["displayName"]!!.jsonPrimitive.content,
            preferredLanguage = id["preferredLanguage"]!!.takeIf { it !is JsonNull }?.jsonPrimitive?.content,
            tier              = IdentityTier.valueOf(id["tier"]!!.jsonPrimitive.content),
            deviceIds         = id["deviceIds"]!!.jsonArray.map { it.jsonPrimitive.content },
            createdAt         = Instant.parse(id["createdAt"]!!.jsonPrimitive.content),
            lastSeenAt        = Instant.parse(id["lastSeenAt"]!!.jsonPrimitive.content)
        )

        assertEquals(IdentityTier.Anonymous, identity.tier)
        assertEquals("Guest", identity.displayName)
        assertNull(identity.preferredLanguage)

        val deviceNode = example["devices"]!!.jsonArray[0].jsonObject
        val device = RegisteredDevice(
            deviceId     = deviceNode["deviceId"]!!.jsonPrimitive.content,
            identityId   = deviceNode["identityId"]!!.jsonPrimitive.content,
            platform     = deviceNode["platform"]!!.jsonPrimitive.content,
            deviceName   = deviceNode["deviceName"]!!.takeIf { it !is JsonNull }?.jsonPrimitive?.content,
            registeredAt = Instant.parse(deviceNode["registeredAt"]!!.jsonPrimitive.content),
            lastActiveAt = Instant.parse(deviceNode["lastActiveAt"]!!.jsonPrimitive.content)
        )

        assertEquals("iot", device.platform)
        assertNull(device.deviceName)
    }

    // ── Platforms list ────────────────────────────────────────────────────────

    @Test
    fun `fixture platforms list has 8 entries`() {
        val root = json.parseToJsonElement(locateFixture("identity.json").readText()).jsonObject
        val platforms = root["platforms"]!!.jsonArray.map { it.jsonPrimitive.content }
        assertEquals(8, platforms.size)
        assertTrue(platforms.containsAll(listOf("android", "ios", "windows", "macos", "linux", "web", "watch", "iot")))
    }

    // ── Data class structural checks ──────────────────────────────────────────

    @Test
    fun `CircleIdentity copy works correctly`() {
        val identity = CircleIdentity(
            identityId        = "test-id",
            displayName       = "Test User",
            preferredLanguage = "en",
            tier              = IdentityTier.Verified,
            deviceIds         = listOf("device-1"),
            createdAt         = Instant.EPOCH,
            lastSeenAt        = Instant.EPOCH
        )
        val updated = identity.copy(displayName = "Updated User")
        assertEquals("Updated User", updated.displayName)
        assertEquals("test-id", updated.identityId)
    }
}
