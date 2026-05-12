// IdentityTest.kt
//
// Verifies IdentityTier enum values and CircleIdentity / RegisteredDevice
// data class construction against the three fixture examples in identity.json.

package com.bhengubv.circleai

import com.bhengubv.circleai.identity.CircleIdentity
import com.bhengubv.circleai.identity.IdentityTier
import com.bhengubv.circleai.identity.RegisteredDevice
import com.fasterxml.jackson.databind.ObjectMapper
import org.junit.jupiter.api.Test
import java.io.File
import java.time.Instant
import kotlin.test.assertEquals
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

class IdentityTest {

    private val mapper = ObjectMapper()

    private fun locateFixture(name: String): File {
        val absolute = File("C:\\Dev\\Solutions\\com.bhengubv\\CircleAI\\fixtures\\$name")
        if (absolute.exists()) return absolute
        val relative = File("../../fixtures/$name")
        if (relative.exists()) return relative
        error("Cannot locate fixture $name")
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
        val root = mapper.readTree(locateFixture("identity.json"))
        val example = root["examples"].first { it["id"].asText() == "verified_multi_device" }
        val id = example["identity"]

        val identity = CircleIdentity(
            identityId        = id["identityId"].asText(),
            displayName       = id["displayName"].asText(),
            preferredLanguage = id["preferredLanguage"].takeIf { !it.isNull }?.asText(),
            tier              = IdentityTier.valueOf(id["tier"].asText()),
            deviceIds         = id["deviceIds"].map { it.asText() },
            createdAt         = Instant.parse(id["createdAt"].asText()),
            lastSeenAt        = Instant.parse(id["lastSeenAt"].asText())
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
        val root = mapper.readTree(locateFixture("identity.json"))
        val example = root["examples"].first { it["id"].asText() == "verified_multi_device" }
        val devicesNode = example["devices"]

        val devices = devicesNode.map { d ->
            RegisteredDevice(
                deviceId     = d["deviceId"].asText(),
                identityId   = d["identityId"].asText(),
                platform     = d["platform"].asText(),
                deviceName   = d["deviceName"].takeIf { !it.isNull }?.asText(),
                registeredAt = Instant.parse(d["registeredAt"].asText()),
                lastActiveAt = Instant.parse(d["lastActiveAt"].asText())
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
        val root = mapper.readTree(locateFixture("identity.json"))
        val example = root["examples"].first { it["id"].asText() == "pseudonymous_single_device" }
        val id = example["identity"]

        val identity = CircleIdentity(
            identityId        = id["identityId"].asText(),
            displayName       = id["displayName"].asText(),
            preferredLanguage = id["preferredLanguage"].takeIf { !it.isNull }?.asText(),
            tier              = IdentityTier.valueOf(id["tier"].asText()),
            deviceIds         = id["deviceIds"].map { it.asText() },
            createdAt         = Instant.parse(id["createdAt"].asText()),
            lastSeenAt        = Instant.parse(id["lastSeenAt"].asText())
        )

        assertEquals(IdentityTier.Pseudonymous, identity.tier)
        assertEquals("B! User", identity.displayName)
        assertEquals("en", identity.preferredLanguage)
        assertEquals(1, identity.deviceIds.size)
    }

    // ── Fixture: anonymous_iot ────────────────────────────────────────────────

    @Test
    fun `fixture anonymous_iot has null preferredLanguage and null deviceName`() {
        val root = mapper.readTree(locateFixture("identity.json"))
        val example = root["examples"].first { it["id"].asText() == "anonymous_iot" }
        val id = example["identity"]

        val identity = CircleIdentity(
            identityId        = id["identityId"].asText(),
            displayName       = id["displayName"].asText(),
            preferredLanguage = id["preferredLanguage"].takeIf { !it.isNull }?.asText(),
            tier              = IdentityTier.valueOf(id["tier"].asText()),
            deviceIds         = id["deviceIds"].map { it.asText() },
            createdAt         = Instant.parse(id["createdAt"].asText()),
            lastSeenAt        = Instant.parse(id["lastSeenAt"].asText())
        )

        assertEquals(IdentityTier.Anonymous, identity.tier)
        assertEquals("Guest", identity.displayName)
        assertNull(identity.preferredLanguage)

        val deviceNode = example["devices"][0]
        val device = RegisteredDevice(
            deviceId     = deviceNode["deviceId"].asText(),
            identityId   = deviceNode["identityId"].asText(),
            platform     = deviceNode["platform"].asText(),
            deviceName   = deviceNode["deviceName"].takeIf { !it.isNull }?.asText(),
            registeredAt = Instant.parse(deviceNode["registeredAt"].asText()),
            lastActiveAt = Instant.parse(deviceNode["lastActiveAt"].asText())
        )

        assertEquals("iot", device.platform)
        assertNull(device.deviceName)
    }

    // ── Platforms list ────────────────────────────────────────────────────────

    @Test
    fun `fixture platforms list has 8 entries`() {
        val root = mapper.readTree(locateFixture("identity.json"))
        val platforms = root["platforms"].map { it.asText() }
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
