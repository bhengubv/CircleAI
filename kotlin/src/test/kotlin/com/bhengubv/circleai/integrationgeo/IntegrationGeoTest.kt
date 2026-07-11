// IntegrationGeoTest.kt
//
// Verifies the CircleAI.Integration.Geo port against the C# reference:
//   - OpenMeteo current: field mapping + wind m/s->km/h (*3.6) + WMO decode.
//   - OpenMeteo hourly: n = min(time.length, hours); hours range guard.
//   - OSRM: profile mapping; lon,lat ordering in URL; distance m/1000 -> km;
//     duration seconds; polyline [lat,lon]; non-"Ok" code throws.

package com.bhengubv.circleai.integrationgeo

import com.bhengubv.circleai.integration.support.okTransport
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.time.Duration
import java.time.Instant
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertTrue

class IntegrationGeoTest {

    @Test
    fun `open-meteo current decodes fields`() = runTest {
        val json = """
            {
              "current": {
                "time": "2026-07-10T12:00",
                "temperature_2m": 21.5,
                "apparent_temperature": 20.0,
                "precipitation": 0.2,
                "wind_speed_10m": 10.0,
                "cloud_cover": 40,
                "weather_code": 3
              }
            }
        """.trimIndent()
        val p = OpenMeteoWeatherProvider(okTransport(json))
        assertEquals("open-meteo", p.providerId)

        val s = p.current(-26.2, 28.0)
        assertEquals(Instant.parse("2026-07-10T12:00:00Z"), s.atUtc)
        assertEquals(21.5, s.tempC, 1e-9)
        assertEquals(20.0, s.feelsLikeC, 1e-9)
        assertEquals(0.2, s.precipMm, 1e-9)
        assertEquals(36.0, s.windKph, 1e-9) // 10 m/s * 3.6
        assertEquals(40, s.cloudPct)
        assertEquals("partly cloudy", s.condition)
    }

    @Test
    fun `open-meteo hourly clamps to available samples`() = runTest {
        val json = """
            {
              "hourly": {
                "time": [ "2026-07-10T00:00", "2026-07-10T01:00" ],
                "temperature_2m": [ 10.0, 11.0 ],
                "apparent_temperature": [ 9.0, 10.0 ],
                "precipitation": [ 0.0, 0.0 ],
                "wind_speed_10m": [ 5.0, 6.0 ],
                "cloud_cover": [ 0, 50 ],
                "weather_code": [ 0, 61 ]
              }
            }
        """.trimIndent()
        val p = OpenMeteoWeatherProvider(okTransport(json))
        val list = p.hourly(-26.2, 28.0, 24) // only 2 samples available
        assertEquals(2, list.size)
        assertEquals("clear sky", list[0].condition)
        assertEquals("rain", list[1].condition)
        assertEquals(21.6, list[1].windKph, 1e-9) // 6 * 3.6
    }

    @Test
    fun `open-meteo hourly rejects bad hours`() = runTest {
        val p = OpenMeteoWeatherProvider(okTransport("{}"))
        assertFailsWith<IllegalArgumentException> { p.hourly(0.0, 0.0, 0) }
        assertFailsWith<IllegalArgumentException> { p.hourly(0.0, 0.0, 200) }
    }

    @Test
    fun `osrm routes and maps profile`() = runTest {
        val json = """
            {
              "code": "Ok",
              "routes": [
                {
                  "distance": 12000.0,
                  "duration": 900.0,
                  "geometry": { "coordinates": [ [28.0, -26.2], [28.1, -26.3] ] }
                }
              ]
            }
        """.trimIndent()
        val http = okTransport(json)
        val p = OsrmRoutingProvider(http)
        assertEquals("osrm", p.providerId)

        val r = p.route(-26.2, 28.0, -26.3, 28.1, "bike")
        assertEquals(12.0, r.distanceKm, 1e-9)
        assertEquals(Duration.ofSeconds(900), r.duration)
        assertEquals(2, r.polyline.size)
        // polyline is [lat, lon]
        assertEquals(-26.2, r.polyline[0].lat, 1e-9)
        assertEquals(28.0, r.polyline[0].lon, 1e-9)
        // URL: profile=bike, coords lon,lat
        assertTrue(http.last.url.contains("/route/v1/bike/"))
        assertTrue(http.last.url.contains("28.0,-26.2;28.1,-26.3"))
    }

    @Test
    fun `osrm throws on non-ok code`() = runTest {
        val p = OsrmRoutingProvider(okTransport("""{ "code": "NoRoute" }"""))
        assertFailsWith<IllegalStateException> { p.route(0.0, 0.0, 1.0, 1.0) }
    }

    @Test
    fun `osrm default profile is driving`() = runTest {
        val http = okTransport("""{ "code": "Ok", "routes": [ { "distance": 0.0, "duration": 0.0 } ] }""")
        val p = OsrmRoutingProvider(http)
        p.route(0.0, 0.0, 1.0, 1.0) // default mode "car"
        assertTrue(http.last.url.contains("/route/v1/driving/"))
    }
}
