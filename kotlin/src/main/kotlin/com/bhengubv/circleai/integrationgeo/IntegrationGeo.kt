// IntegrationGeo.kt
//
// Kotlin port of CircleAI.Integration.Geo (OpenMeteoWeatherProvider.cs +
// OsrmRoutingProvider.cs) — the C# reference is the EXACT spec.
//   * Open-Meteo — free, no-API-key weather; current + hourly forecast.
//   * OSRM — Open Source Routing Machine HTTP client.
//
// Fidelity notes:
//   * The network is injected via [HttpTransport]; URL composition, the
//     m/s -> km/h wind conversion (*3.6), WMO code decoding, and JSON walks
//     mirror the C# code exactly.
//   * OpenMeteo hourly clamps n = min(time.length, hours) and requires
//     0 < hours <= 168.
//   * OSRM profile mapping: bike/bicycle -> "bike", foot/walk -> "foot",
//     else "driving"; coords are lon,lat; distance metres/1000 -> km,
//     duration seconds; a non-"Ok" code throws; polyline is [lat,lon] pairs.

package com.bhengubv.circleai.integrationgeo

import com.bhengubv.circleai.integration.GeoPoint
import com.bhengubv.circleai.integration.HttpRequest
import com.bhengubv.circleai.integration.HttpTransport
import com.bhengubv.circleai.integration.HttpVerb
import com.bhengubv.circleai.integration.IRoutingProvider
import com.bhengubv.circleai.integration.IWeatherProvider
import com.bhengubv.circleai.integration.RouteEstimate
import com.bhengubv.circleai.integration.WeatherSample
import com.bhengubv.circleai.integration.ensureSuccess
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import java.time.Duration
import java.time.Instant
import java.time.OffsetDateTime
import java.time.ZoneOffset
import kotlin.math.min

internal val GEO_JSON = Json { ignoreUnknownKeys = true; isLenient = true }

/** Invariant-culture double rendering (mirrors C# `ToString(InvariantCulture)`). */
internal fun Double.inv(): String {
    val s = this.toString()
    return s // Kotlin Double.toString already uses '.' decimal + no grouping
}

internal fun JsonObject.obj(key: String): JsonObject? = this[key] as? JsonObject
internal fun JsonObject.arr(key: String): JsonArray? = this[key] as? JsonArray
internal fun JsonObject.dbl(key: String): Double = ((this[key] as? JsonPrimitive)?.content)?.toDoubleOrNull() ?: 0.0
internal fun JsonObject.intv(key: String): Int = ((this[key] as? JsonPrimitive)?.content)?.toDoubleOrNull()?.toInt() ?: 0
internal fun JsonObject.text(key: String): String? = (this[key] as? JsonPrimitive)?.content

/** Parse an ISO timestamp as UTC; falls back to now (mirrors the C# default). */
internal fun parseIsoUtc(s: String?): Instant {
    if (s.isNullOrEmpty()) return Instant.now()
    runCatching { return OffsetDateTime.parse(s).toInstant() }
    // Open-Meteo emits naive "yyyy-MM-dd'T'HH:mm" -> assume UTC.
    runCatching { return java.time.LocalDateTime.parse(s).atOffset(ZoneOffset.UTC).toInstant() }
    return Instant.now()
}

// =====================================================================
// Open-Meteo weather (OpenMeteoWeatherProvider.cs)
// =====================================================================

/** Open-Meteo weather provider. Mirrors C# `OpenMeteoWeatherProvider`. */
class OpenMeteoWeatherProvider(private val http: HttpTransport) : IWeatherProvider {

    override val providerId: String get() = "open-meteo"

    override suspend fun current(lat: Double, lon: Double): WeatherSample {
        val url = "https://api.open-meteo.com/v1/forecast?latitude=${lat.inv()}&longitude=${lon.inv()}" +
            "&current=temperature_2m,apparent_temperature,precipitation,wind_speed_10m,cloud_cover,weather_code"
        val resp = http.send(HttpRequest(HttpVerb.GET, url)).ensureSuccess()
        val root = GEO_JSON.parseToJsonElement(resp.body).jsonObj()
        val cur = root.obj("current") ?: JsonObject(emptyMap())
        return WeatherSample(
            atUtc = parseIsoUtc(cur.text("time")),
            tempC = cur.dbl("temperature_2m"),
            feelsLikeC = cur.dbl("apparent_temperature"),
            precipMm = cur.dbl("precipitation"),
            windKph = cur.dbl("wind_speed_10m") * 3.6,
            cloudPct = cur.intv("cloud_cover"),
            condition = wmoDecode(cur.intv("weather_code")),
        )
    }

    override suspend fun hourly(lat: Double, lon: Double, hours: Int): List<WeatherSample> {
        require(hours in 1..168) { "hours" }
        val url = "https://api.open-meteo.com/v1/forecast?latitude=${lat.inv()}&longitude=${lon.inv()}" +
            "&hourly=temperature_2m,apparent_temperature,precipitation,wind_speed_10m,cloud_cover,weather_code" +
            "&forecast_hours=$hours"
        val resp = http.send(HttpRequest(HttpVerb.GET, url)).ensureSuccess()
        val root = GEO_JSON.parseToJsonElement(resp.body).jsonObj()
        val h = root.obj("hourly") ?: return emptyList()
        val time = h.arr("time") ?: return emptyList()
        val temp = h.arr("temperature_2m") ?: return emptyList()
        val feel = h.arr("apparent_temperature") ?: return emptyList()
        val prec = h.arr("precipitation") ?: return emptyList()
        val wind = h.arr("wind_speed_10m") ?: return emptyList()
        val cld = h.arr("cloud_cover") ?: return emptyList()
        val code = h.arr("weather_code") ?: return emptyList()
        val n = min(time.size, hours)
        val result = ArrayList<WeatherSample>(n)
        for (i in 0 until n) {
            result += WeatherSample(
                atUtc = parseIsoUtc((time[i] as? JsonPrimitive)?.content),
                tempC = prim(temp, i),
                feelsLikeC = prim(feel, i),
                precipMm = prim(prec, i),
                windKph = prim(wind, i) * 3.6,
                cloudPct = prim(cld, i).toInt(),
                condition = wmoDecode(prim(code, i).toInt()),
            )
        }
        return result
    }

    private companion object {
        fun prim(a: JsonArray, i: Int): Double = ((a[i] as? JsonPrimitive)?.content)?.toDoubleOrNull() ?: 0.0

        /** Decode WMO weather code (Open-Meteo standard). Mirrors C# `WmoDecode`. */
        fun wmoDecode(code: Int): String = when (code) {
            0 -> "clear sky"
            1, 2, 3 -> "partly cloudy"
            45, 48 -> "fog"
            51, 53, 55 -> "drizzle"
            56, 57 -> "freezing drizzle"
            61, 63, 65 -> "rain"
            66, 67 -> "freezing rain"
            71, 73, 75 -> "snow"
            77 -> "snow grains"
            80, 81, 82 -> "rain showers"
            85, 86 -> "snow showers"
            95 -> "thunderstorm"
            96, 99 -> "thunderstorm with hail"
            else -> "unknown"
        }
    }
}

// =====================================================================
// OSRM routing (OsrmRoutingProvider.cs)
// =====================================================================

/** OSRM connector config. Mirrors C# `OsrmOptions`. */
data class OsrmOptions(val host: String = "https://router.project-osrm.org")

/** OSRM routing provider. Mirrors C# `OsrmRoutingProvider`. */
class OsrmRoutingProvider(
    private val opts: OsrmOptions,
    private val http: HttpTransport,
) : IRoutingProvider {

    constructor(http: HttpTransport) : this(OsrmOptions(), http)

    override val providerId: String get() = "osrm"

    override suspend fun route(
        fromLat: Double,
        fromLon: Double,
        toLat: Double,
        toLon: Double,
        mode: String,
    ): RouteEstimate {
        val profile = when (mode) {
            "bike", "bicycle" -> "bike"
            "foot", "walk" -> "foot"
            else -> "driving"
        }
        val url = "${opts.host.trimEnd('/')}/route/v1/$profile/" +
            "${fromLon.inv()},${fromLat.inv()};" +
            "${toLon.inv()},${toLat.inv()}" +
            "?overview=full&geometries=geojson"
        val resp = http.send(HttpRequest(HttpVerb.GET, url)).ensureSuccess()
        val root = GEO_JSON.parseToJsonElement(resp.body).jsonObj()

        val code = root.text("code")
        if (code != "Ok") error("OSRM returned code=$code")

        val route = (root.arr("routes")?.getOrNull(0) as? JsonObject) ?: error("OSRM returned no routes")
        val dist = route.dbl("distance") // metres
        val dur = route.dbl("duration") // seconds
        val poly = ArrayList<GeoPoint>()
        val coords = route.obj("geometry")?.arr("coordinates")
        coords?.forEach { pt ->
            val a = pt as? JsonArray ?: return@forEach
            if (a.size < 2) return@forEach
            val lon = ((a[0] as? JsonPrimitive)?.content)?.toDoubleOrNull() ?: return@forEach
            val lat = ((a[1] as? JsonPrimitive)?.content)?.toDoubleOrNull() ?: return@forEach
            poly += GeoPoint(lat, lon)
        }
        return RouteEstimate(distanceKm = dist / 1000.0, duration = Duration.ofMillis((dur * 1000).toLong()), polyline = poly)
    }
}

// ── shared JSON convenience ───────────────────────────────────────────────

internal fun kotlinx.serialization.json.JsonElement.jsonObj(): JsonObject =
    this as? JsonObject ?: JsonObject(emptyMap())
