package com.bhengubv.circleai

import org.junit.Assert.*
import org.junit.Test
import java.time.Instant

class IdentityTest {
    @Test fun createVerifiedIdentity() {
        val device = RegisteredDevice(
            deviceId = "550e8400-e29b-41d4-a716-446655440000",
            deviceName = "Pixel 8",
            registeredAt = Instant.parse("2024-01-01T00:00:00Z"),
            isPrimary = true
        )
        val identity = CircleIdentity(
            identityId = "550e8400-e29b-41d4-a716-446655440001",
            tier = IdentityTier.VERIFIED,
            displayName = "Test User",
            createdAt = Instant.parse("2024-01-01T00:00:00Z"),
            devices = listOf(device)
        )
        assertEquals(IdentityTier.VERIFIED, identity.tier)
        assertEquals(1, identity.devices.size)
        assertTrue(identity.devices[0].isPrimary)
    }

    @Test fun anonymousHasNoDisplayName() {
        val identity = CircleIdentity(
            identityId = "550e8400-e29b-41d4-a716-446655440002",
            tier = IdentityTier.ANONYMOUS,
            createdAt = Instant.now(),
            devices = emptyList()
        )
        assertEquals(IdentityTier.ANONYMOUS, identity.tier)
        assertNull(identity.displayName)
    }

    @Test fun tierValues() {
        assertEquals(3, IdentityTier.values().size)
    }
}
