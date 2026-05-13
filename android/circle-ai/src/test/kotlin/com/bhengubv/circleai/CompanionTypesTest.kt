package com.bhengubv.circleai

import org.junit.Assert.*
import org.junit.Test
import java.time.Instant

class CompanionTypesTest {
    @Test fun createCompanionContext() {
        val ctx = CompanionContext(
            sessionId = "550e8400-e29b-41d4-a716-446655440000",
            identityId = "550e8400-e29b-41d4-a716-446655440001",
            interfaceKind = InterfaceKind.VOICE,
            locale = "en-US",
            startedAt = Instant.now()
        )
        assertEquals(InterfaceKind.VOICE, ctx.interfaceKind)
        assertEquals("en-US", ctx.locale)
    }

    @Test fun interfaceKindValues() {
        assertEquals(4, InterfaceKind.values().size)
    }

    @Test fun createCompanionTurn() {
        val turn = CompanionTurn(
            turnId = "550e8400-e29b-41d4-a716-446655440000",
            sessionId = "550e8400-e29b-41d4-a716-446655440001",
            userInput = "Hello",
            assistantResponse = "Hi there!",
            createdAt = Instant.now(),
            turnIndex = 0
        )
        assertEquals(0, turn.turnIndex)
        assertEquals("Hello", turn.userInput)
    }
}
