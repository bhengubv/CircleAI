// Integration.kt
//
// Kotlin port of CircleAI.Integration (Contracts.cs) — the C# reference is the
// EXACT spec. Shared abstractions for the external-integration layer: calendar,
// email, news, weather, routing and home-automation providers all implement
// these so the Companion's ProactiveBriefingService can stitch a coherent
// "what's happening" picture without coupling to specific providers.
//
// Fidelity notes:
//   * C# `record` -> Kotlin `data class`.
//   * C# `DateTimeOffset` -> `java.time.Instant`.
//   * C# `Uri` -> `java.net.URI`.
//   * C# `TimeSpan` -> `java.time.Duration`.
//   * C# `ValueTask<T>` async methods -> `suspend fun`.
//   * C# `IReadOnlyList<(double Lat, double Lon)>` -> `List<GeoPoint>`.
//   * The concrete connectors in the sibling packages are real request-builders
//     + response-parsers; the network itself is injected behind [HttpTransport]
//     (mirrors the C# "HTTP plumbing is host-supplied" convention). No real
//     sockets are opened by the ported code.

package com.bhengubv.circleai.integration

import java.math.BigDecimal
import java.math.RoundingMode
import java.net.URI
import java.time.Duration
import java.time.Instant
import java.util.concurrent.ConcurrentHashMap
import kotlin.math.PI
import kotlin.math.atan2
import kotlin.math.cos
import kotlin.math.sin
import kotlin.math.sqrt

// ── Calendar ─────────────────────────────────────────────────────────────

/** A single calendar event. Mirrors C# `CalendarEvent`. */
data class CalendarEvent(
    val eventId: String,
    val calendarId: String,
    val title: String,
    val description: String?,
    val location: String?,
    val startUtc: Instant,
    val endUtc: Instant,
    val isAllDay: Boolean,
    val attendees: List<String>,
)

/** A calendar connector. Mirrors C# `ICalendarConnector`. */
interface ICalendarConnector {
    val providerId: String
    val isConfigured: Boolean
    suspend fun listEvents(fromUtc: Instant, toUtc: Instant): List<CalendarEvent>
    suspend fun createEvent(ev: CalendarEvent): CalendarEvent
    suspend fun deleteEvent(calendarId: String, eventId: String)
}

// ── Email ────────────────────────────────────────────────────────────────

/** A single email message. Mirrors C# `EmailMessage`. */
data class EmailMessage(
    val messageId: String,
    val from: String,
    val to: List<String>,
    val subject: String,
    val bodyText: String,
    val receivedUtc: Instant,
    val unread: Boolean,
    val labels: List<String>,
)

/** An email connector. Mirrors C# `IEmailConnector`. */
interface IEmailConnector {
    val providerId: String
    val isConfigured: Boolean
    suspend fun listUnread(max: Int): List<EmailMessage>
    suspend fun search(query: String, max: Int): List<EmailMessage>
    suspend fun markRead(messageId: String)
}

// ── News + social feeds ──────────────────────────────────────────────────

/** A news / social feed item. Mirrors C# `NewsItem`. */
data class NewsItem(
    val itemId: String,
    val sourceId: String,
    val title: String,
    val summary: String,
    val url: URI,
    val publishedUtc: Instant,
    val tags: List<String>,
)

/** A news source. Mirrors C# `INewsSource`. */
interface INewsSource {
    val sourceId: String
    val isConfigured: Boolean
    suspend fun fetchLatest(max: Int): List<NewsItem>
}

// ── Weather ──────────────────────────────────────────────────────────────

/** A weather observation / forecast sample. Mirrors C# `WeatherSample`. */
data class WeatherSample(
    val atUtc: Instant,
    val tempC: Double,
    val feelsLikeC: Double,
    val precipMm: Double,
    val windKph: Double,
    val cloudPct: Int,
    val condition: String,
)

/** A weather provider. Mirrors C# `IWeatherProvider`. */
interface IWeatherProvider {
    val providerId: String
    suspend fun current(lat: Double, lon: Double): WeatherSample
    suspend fun hourly(lat: Double, lon: Double, hours: Int): List<WeatherSample>
}

// ── Routing / traffic ────────────────────────────────────────────────────

/** A latitude/longitude pair. Mirrors C# `(double Lat, double Lon)` tuple. */
data class GeoPoint(val lat: Double, val lon: Double)

/** A route estimate. Mirrors C# `RouteEstimate`. */
data class RouteEstimate(
    val distanceKm: Double,
    val duration: Duration,
    val polyline: List<GeoPoint>,
)

/** A routing / traffic provider. Mirrors C# `IRoutingProvider`. */
interface IRoutingProvider {
    val providerId: String
    suspend fun route(
        fromLat: Double,
        fromLon: Double,
        toLat: Double,
        toLon: Double,
        mode: String = "car",
    ): RouteEstimate
}

// ── Home automation ──────────────────────────────────────────────────────

/** A home-automation entity + its attributes. Mirrors C# `HaEntity`. */
data class HaEntity(
    val entityId: String,
    val friendlyName: String,
    val domain: String,
    val state: String,
    val attributes: Map<String, String>,
)

/** A home-automation connector. Mirrors C# `IHomeAutomationConnector`. */
interface IHomeAutomationConnector {
    val providerId: String
    val isConfigured: Boolean
    suspend fun listEntities(): List<HaEntity>
    suspend fun callService(
        domain: String,
        service: String,
        data: Map<String, Any?>?,
    )
}

// ── Injected HTTP transport (host-supplied network) ──────────────────────
//
// The C# connectors take an `HttpClient`; the network in this port is injected
// behind this interface so the ported request-building + response-parsing logic
// is fully exercised without opening real sockets. This mirrors the existing
// "HTTP plumbing is host-supplied" pattern used by the Commerce integrations.

/** HTTP method verb (covers the verbs the connectors use, incl. WebDAV REPORT). */
enum class HttpVerb { GET, PUT, POST, PATCH, DELETE, REPORT }

/**
 * A single HTTP request assembled by a connector. [url] is the absolute request
 * URL (connectors resolve any base address themselves, mirroring C#
 * `HttpClient.BaseAddress` composition).
 */
data class HttpRequest(
    val verb: HttpVerb,
    val url: String,
    val headers: Map<String, String> = emptyMap(),
    val body: String? = null,
    val contentType: String? = null,
)

/** An HTTP response. [status] is the numeric status code. */
data class HttpResponse(val status: Int, val body: String) {
    /** True for 2xx. Mirrors C# `HttpResponseMessage.IsSuccessStatusCode`. */
    val isSuccess: Boolean get() = status in 200..299
}

/** Injected transport standing in for the real network. */
interface HttpTransport {
    suspend fun send(request: HttpRequest): HttpResponse
}

/**
 * Async access-token provider. Mirrors C#
 * `Func<CancellationToken, ValueTask<string?>>`.
 */
fun interface AccessTokenProvider {
    suspend fun getToken(): String?
}

/** Thrown when a request the connector issued did not succeed. */
class IntegrationHttpException(val status: Int, message: String) : RuntimeException(message)

/** Ensures a 2xx response, else throws — mirrors C# `EnsureSuccessStatusCode()`. */
internal fun HttpResponse.ensureSuccess(): HttpResponse {
    if (!isSuccess) throw IntegrationHttpException(status, "Response status code does not indicate success: $status.")
    return this
}

// ===========================================================================
// In-memory reference connectors (InMemoryIntegrationConnectors.cs)
// ===========================================================================
//
// Deterministic, dependency-free in-memory reference implementations of the six
// connector contracts above — the canonical offline/test doubles usable without
// any external provider, mirroring the InMemory* pattern every other package
// ships. The real provider bindings live in the sibling integration.* packages.
// Numeric results (weather, haversine) are byte-for-byte parity with the C#
// reference: `Math.Round(x, n)` (banker's rounding) → BigDecimal HALF_EVEN.

/** Round to [scale] decimals using banker's rounding — parity with C# `Math.Round(x, scale)`. */
private fun roundHalfEven(value: Double, scale: Int): Double =
    BigDecimal(value).setScale(scale, RoundingMode.HALF_EVEN).toDouble()

/**
 * In-memory [ICalendarConnector]: events are held in a map; listing returns those
 * overlapping the window, ordered by start. Mirrors C# `InMemoryCalendarConnector`.
 */
class InMemoryCalendarConnector : ICalendarConnector {
    private val events = ConcurrentHashMap<String, CalendarEvent>()

    override val providerId: String get() = "in-memory"
    override val isConfigured: Boolean get() = true

    override suspend fun listEvents(fromUtc: Instant, toUtc: Instant): List<CalendarEvent> =
        events.values
            .filter { it.startUtc.isBefore(toUtc) && it.endUtc.isAfter(fromUtc) }
            .sortedBy { it.startUtc }

    override suspend fun createEvent(ev: CalendarEvent): CalendarEvent {
        events[ev.eventId] = ev
        return ev
    }

    override suspend fun deleteEvent(calendarId: String, eventId: String) {
        events.remove(eventId)
    }
}

/**
 * In-memory [IEmailConnector]: seeded with messages; unread + search read
 * newest-first, [markRead] flips the flag. Mirrors C# `InMemoryEmailConnector`.
 */
class InMemoryEmailConnector(seed: Iterable<EmailMessage>? = null) : IEmailConnector {
    private val messages = ConcurrentHashMap<String, EmailMessage>()

    init {
        seed?.forEach { messages[it.messageId] = it }
    }

    override val providerId: String get() = "in-memory"
    override val isConfigured: Boolean get() = true

    override suspend fun listUnread(max: Int): List<EmailMessage> =
        messages.values
            .filter { it.unread }
            .sortedByDescending { it.receivedUtc }
            .take(maxOf(0, max))

    override suspend fun search(query: String, max: Int): List<EmailMessage> =
        messages.values
            .filter {
                it.subject.contains(query, ignoreCase = true) || it.bodyText.contains(query, ignoreCase = true)
            }
            .sortedByDescending { it.receivedUtc }
            .take(maxOf(0, max))

    override suspend fun markRead(messageId: String) {
        val m = messages[messageId] ?: return
        messages[messageId] = m.copy(unread = false)
    }
}

/** In-memory [INewsSource]: seeded items, newest-first. Mirrors C# `InMemoryNewsSource`. */
class InMemoryNewsSource(seed: Iterable<NewsItem>? = null) : INewsSource {
    private val items = ConcurrentHashMap<String, NewsItem>()

    init {
        seed?.forEach { items[it.itemId] = it }
    }

    override val sourceId: String get() = "in-memory"
    override val isConfigured: Boolean get() = true

    override suspend fun fetchLatest(max: Int): List<NewsItem> =
        items.values
            .sortedByDescending { it.publishedUtc }
            .take(maxOf(0, max))
}

/**
 * In-memory [IWeatherProvider]: deterministic pseudo-weather derived from
 * coordinates + hour (no randomness, reproducible across platforms). Mirrors C#
 * `InMemoryWeatherProvider`.
 */
class InMemoryWeatherProvider : IWeatherProvider {
    override val providerId: String get() = "in-memory"

    override suspend fun current(lat: Double, lon: Double): WeatherSample = sample(lat, lon, 0)

    override suspend fun hourly(lat: Double, lon: Double, hours: Int): List<WeatherSample> =
        (0 until maxOf(0, hours)).map { sample(lat, lon, it) }

    private fun sample(lat: Double, lon: Double, hourOffset: Int): WeatherSample {
        val tempC = roundHalfEven(15.0 + 10.0 * cos((lat + hourOffset) * PI / 12.0), 2)
        return WeatherSample(
            atUtc = Instant.EPOCH.plus(Duration.ofHours(hourOffset.toLong())),
            tempC = tempC,
            feelsLikeC = roundHalfEven(tempC - 1.5, 2),
            precipMm = 0.0,
            windKph = 12.0,
            cloudPct = 40,
            condition = "Clear",
        )
    }
}

/**
 * In-memory [IRoutingProvider]: great-circle distance and a mode-based speed give
 * a deterministic estimate with a 2-point polyline. Mirrors C#
 * `InMemoryRoutingProvider`.
 */
class InMemoryRoutingProvider : IRoutingProvider {
    override val providerId: String get() = "in-memory"

    override suspend fun route(
        fromLat: Double,
        fromLon: Double,
        toLat: Double,
        toLon: Double,
        mode: String,
    ): RouteEstimate {
        val km = haversine(fromLat, fromLon, toLat, toLon)
        val kph = when (mode) {
            "walk" -> 5.0
            "bike" -> 18.0
            "transit" -> 30.0
            else -> 60.0
        }
        val hours = if (kph <= 0) 0.0 else km / kph
        val dur = Duration.ofNanos((hours * 3_600_000_000_000.0).toLong())
        return RouteEstimate(
            distanceKm = roundHalfEven(km, 3),
            duration = dur,
            polyline = listOf(GeoPoint(fromLat, fromLon), GeoPoint(toLat, toLon)),
        )
    }

    private fun haversine(lat1: Double, lon1: Double, lat2: Double, lon2: Double): Double {
        val r = 6371.0
        val dLat = (lat2 - lat1) * PI / 180.0
        val dLon = (lon2 - lon1) * PI / 180.0
        val a = sin(dLat / 2) * sin(dLat / 2) +
            cos(lat1 * PI / 180.0) * cos(lat2 * PI / 180.0) *
            sin(dLon / 2) * sin(dLon / 2)
        return r * 2 * atan2(sqrt(a), sqrt(1 - a))
    }
}

/**
 * In-memory [IHomeAutomationConnector]: seeded entities; turn_on/turn_off/toggle
 * deterministically mutate matching-domain entity state. Mirrors C#
 * `InMemoryHomeAutomationConnector`.
 */
class InMemoryHomeAutomationConnector(seed: Iterable<HaEntity>? = null) : IHomeAutomationConnector {
    private val entities = ConcurrentHashMap<String, HaEntity>()

    init {
        seed?.forEach { entities[it.entityId] = it }
    }

    override val providerId: String get() = "in-memory"
    override val isConfigured: Boolean get() = true

    override suspend fun listEntities(): List<HaEntity> =
        entities.values.sortedBy { it.entityId }

    override suspend fun callService(
        domain: String,
        service: String,
        data: Map<String, Any?>?,
    ) {
        entities.values
            .filter { it.domain.equals(domain, ignoreCase = true) }
            .toList()
            .forEach { e ->
                val newState = when (service) {
                    "turn_on" -> "on"
                    "turn_off" -> "off"
                    "toggle" -> if (e.state == "on") "off" else "on"
                    else -> e.state
                }
                entities[e.entityId] = e.copy(state = newState)
            }
    }
}
