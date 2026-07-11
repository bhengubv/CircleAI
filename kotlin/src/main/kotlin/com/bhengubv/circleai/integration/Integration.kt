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

import java.net.URI
import java.time.Duration
import java.time.Instant

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
