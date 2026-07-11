// IntegrationHomeAssistantTest.kt
//
// Verifies the CircleAI.Integration.HomeAssistant port against the C# reference:
//   - listEntities walks /api/states: entity_id split -> domain, state,
//     friendly_name, stringified attributes; empty entity_id skipped.
//   - callService POSTs to /api/services/{domain}/{service} with a JSON body.
//   - turnOn/turnOff call homeassistant.turn_on/turn_off with entity_id.
//   - Bearer auth header + trailing-slash base URL composition.

package com.bhengubv.circleai.integrationhomeassistant

import com.bhengubv.circleai.integration.HttpVerb
import com.bhengubv.circleai.integration.support.FakeTransport
import com.bhengubv.circleai.integration.support.okTransport
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.net.URI
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

class IntegrationHomeAssistantTest {

    private val opts = HomeAssistantOptions(URI("http://ha.local:8123/"), "llt-token")

    @Test
    fun `list entities parses states`() = runTest {
        val json = """
            [
              {
                "entity_id": "light.kitchen",
                "state": "on",
                "attributes": { "friendly_name": "Kitchen Light", "brightness": 200, "on": true }
              },
              { "entity_id": "", "state": "ignored" },
              { "entity_id": "sensor.temp", "state": "21.5", "attributes": {} }
            ]
        """.trimIndent()
        val http = okTransport(json)
        val c = HomeAssistantConnector(opts, http)
        assertEquals("home-assistant", c.providerId)
        assertTrue(c.isConfigured)

        val entities = c.listEntities()
        assertEquals(2, entities.size) // empty entity_id skipped
        val light = entities.first { it.entityId == "light.kitchen" }
        assertEquals("light", light.domain)
        assertEquals("Kitchen Light", light.friendlyName)
        assertEquals("on", light.state)
        assertEquals("200", light.attributes["brightness"])
        assertEquals("true", light.attributes["on"])
        // friendly_name falls back to entity id
        val sensor = entities.first { it.entityId == "sensor.temp" }
        assertEquals("sensor.temp", sensor.friendlyName)

        assertTrue(http.last.url.endsWith("api/states"))
        assertEquals("Bearer llt-token", http.last.headers["Authorization"])
    }

    @Test
    fun `call service posts to services path`() = runTest {
        val http = okTransport("[]")
        val c = HomeAssistantConnector(opts, http)
        c.callService("light", "turn_on", mapOf("entity_id" to "light.kitchen", "brightness" to 128))
        assertEquals(HttpVerb.POST, http.last.verb)
        assertTrue(http.last.url.endsWith("api/services/light/turn_on"))
        assertTrue(http.last.body!!.contains("light.kitchen"))
        assertTrue(http.last.body!!.contains("128"))
    }

    @Test
    fun `turn on and off use homeassistant domain`() = runTest {
        val http = okTransport("[]")
        val c = HomeAssistantConnector(opts, http)
        c.turnOn("switch.fan")
        assertTrue(http.last.url.endsWith("api/services/homeassistant/turn_on"))
        c.turnOff("switch.fan")
        assertTrue(http.last.url.endsWith("api/services/homeassistant/turn_off"))
        assertTrue(http.last.body!!.contains("switch.fan"))
    }

    @Test
    fun `base url without trailing slash is normalized`() = runTest {
        val http = okTransport("[]")
        val c = HomeAssistantConnector(HomeAssistantOptions(URI("http://ha.local:8123"), "t"), http)
        c.listEntities()
        assertEquals("http://ha.local:8123/api/states", http.last.url)
    }

    @Test
    fun `not configured when token blank`() {
        val c = HomeAssistantConnector(HomeAssistantOptions(URI("http://ha.local:8123/"), ""), FakeTransport())
        assertFalse(c.isConfigured)
    }
}
