// IntegrationCalendar.kt
//
// Kotlin port of CircleAI.Integration.Calendar (CalDavCalendarConnector.cs +
// GoogleCalendarConnector.cs + MsGraphCalendarConnector.cs) — the C# reference
// is the EXACT spec. Three [ICalendarConnector] implementations:
//   * CalDAV (iCloud, Fastmail, Nextcloud, …) — REPORT query + minimal ICS.
//   * Google Calendar v3 — host-supplied OAuth bearer.
//   * Microsoft Graph 1.0 — host-supplied OAuth bearer.
//
// Fidelity notes:
//   * The real network is injected via [HttpTransport]; each connector builds
//     the exact same request URLs/verbs/bodies as the C# `HttpClient` code and
//     parses the responses identically. No real sockets are opened.
//   * C# `record` options -> Kotlin `data class`; `Uri` -> `java.net.URI`.
//   * C# `DateTimeOffset` -> `java.time.Instant`; the ISO-8601 "O" round-trip
//     format is emitted via a UTC `DateTimeFormatter`.
//   * ICS parsing mirrors the C# regex-based parser exactly (UID/SUMMARY/
//     DESCRIPTION/LOCATION/DTSTART/DTEND; all-day detection).
//   * Google/Graph JSON parsing mirrors the C# `JsonDocument` walks field-for-
//     field, including `status == "cancelled"` skipping and attendee extraction.

package com.bhengubv.circleai.integrationcalendar

import com.bhengubv.circleai.integration.AccessTokenProvider
import com.bhengubv.circleai.integration.CalendarEvent
import com.bhengubv.circleai.integration.HttpRequest
import com.bhengubv.circleai.integration.HttpTransport
import com.bhengubv.circleai.integration.HttpVerb
import com.bhengubv.circleai.integration.ICalendarConnector
import com.bhengubv.circleai.integration.ensureSuccess
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.buildJsonArray
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.put
import java.net.URI
import java.net.URLEncoder
import java.time.Instant
import java.time.ZoneOffset
import java.time.format.DateTimeFormatter

// =====================================================================
// Shared helpers
// =====================================================================

internal val CAL_JSON = Json { ignoreUnknownKeys = true; isLenient = true }

/** ISO-8601 round-trip ("O") emitter in UTC, e.g. 2026-07-10T12:00:00.0000000Z. */
internal fun Instant.toIsoO(): String =
    DateTimeFormatter.ISO_INSTANT.format(this)

/** yyyy-MM-dd of the UTC date. Mirrors C# `UtcDateTime.ToString("yyyy-MM-dd")`. */
internal fun Instant.toUtcDate(): String =
    DateTimeFormatter.ofPattern("yyyy-MM-dd").withZone(ZoneOffset.UTC).format(this)

/** yyyyMMdd'T'HHmmss'Z' — the ICS/CalDAV timestamp form (Z is a literal). */
internal val ICS_STAMP: DateTimeFormatter =
    DateTimeFormatter.ofPattern("yyyyMMdd'T'HHmmss'Z'").withZone(ZoneOffset.UTC)

/**
 * yyyyMMdd'T'HHmmss'Z' parsed into a LocalDateTime. The trailing Z is a required
 * literal (mirrors the C# `TryParseExact(v, "yyyyMMddTHHmmssZ", …)` datetime branch,
 * where the Z is a literal marker); the parsed value is then pinned to UTC.
 */
internal val ICS_LOCAL: DateTimeFormatter =
    DateTimeFormatter.ofPattern("yyyyMMdd'T'HHmmss'Z'")

internal fun esc(s: String): String =
    URLEncoder.encode(s, Charsets.UTF_8).replace("+", "%20")

internal fun JsonObject.string(key: String): String? {
    val p = this[key] as? JsonPrimitive ?: return null
    return if (p.content == "null" && !p.isString) null else p.content
}

// =====================================================================
// CalDAV (CalDavCalendarConnector.cs)
// =====================================================================

/**
 * CalDAV connector config. Mirrors C# `CalDavCalendarOptions`.
 * @param calendarUri Full URL of the calendar collection.
 * @param username CalDAV username.
 * @param password CalDAV password (often an app-specific password).
 */
data class CalDavCalendarOptions(val calendarUri: URI, val username: String, val password: String)

/** Generic CalDAV connector. Mirrors C# `CalDavCalendarConnector`. */
class CalDavCalendarConnector(
    private val opts: CalDavCalendarOptions,
    private val http: HttpTransport,
) : ICalendarConnector {

    private val authHeader: String = run {
        val creds = java.util.Base64.getEncoder()
            .encodeToString("${opts.username}:${opts.password}".toByteArray(Charsets.UTF_8))
        "Basic $creds"
    }

    override val providerId: String get() = "caldav"
    override val isConfigured: Boolean
        get() = opts.username.isNotBlank() && opts.password.isNotBlank()

    override suspend fun listEvents(fromUtc: Instant, toUtc: Instant): List<CalendarEvent> {
        val xml = """
            <?xml version="1.0" encoding="utf-8" ?>
            <C:calendar-query xmlns:D="DAV:" xmlns:C="urn:ietf:params:xml:ns:caldav">
              <D:prop>
                <D:getetag/>
                <C:calendar-data/>
              </D:prop>
              <C:filter>
                <C:comp-filter name="VCALENDAR">
                  <C:comp-filter name="VEVENT">
                    <C:time-range start="${ICS_STAMP.format(fromUtc)}" end="${ICS_STAMP.format(toUtc)}"/>
                  </C:comp-filter>
                </C:comp-filter>
              </C:filter>
            </C:calendar-query>
        """.trimIndent()
        val resp = http.send(
            HttpRequest(
                verb = HttpVerb.REPORT,
                url = opts.calendarUri.toString(),
                headers = mapOf("Authorization" to authHeader, "Depth" to "1"),
                body = xml,
                contentType = "application/xml",
            ),
        ).ensureSuccess()

        val result = ArrayList<CalendarEvent>()
        // The C# code pulls every <calendar-data> element's text and parses ICS.
        for (calData in extractCalendarData(resp.body)) {
            result += parseIcs(calData, opts.calendarUri.toString())
        }
        return result
    }

    override suspend fun createEvent(ev: CalendarEvent): CalendarEvent {
        val uid = ev.eventId.ifBlank { java.util.UUID.randomUUID().toString().replace("-", "") }
        val ics = buildIcs(ev.copy(eventId = uid))
        val targetUri = opts.calendarUri.resolve("$uid.ics")
        http.send(
            HttpRequest(
                verb = HttpVerb.PUT,
                url = targetUri.toString(),
                headers = mapOf("Authorization" to authHeader, "If-None-Match" to "*"),
                body = ics,
                contentType = "text/calendar",
            ),
        ).ensureSuccess()
        return ev.copy(eventId = uid)
    }

    override suspend fun deleteEvent(calendarId: String, eventId: String) {
        require(eventId.isNotBlank()) { "eventId required" }
        val targetUri = opts.calendarUri.resolve("$eventId.ics")
        val resp = http.send(
            HttpRequest(HttpVerb.DELETE, targetUri.toString(), mapOf("Authorization" to authHeader)),
        )
        if (resp.status != 204 && resp.status != 200 && resp.status != 404) resp.ensureSuccess()
    }

    private companion object {
        private val RX_CALDATA =
            Regex("<[^>]*calendar-data[^>]*>(.*?)</[^>]*calendar-data>", RegexOption.DOT_MATCHES_ALL)
        private val RX_EVENT = Regex("BEGIN:VEVENT(.*?)END:VEVENT", RegexOption.DOT_MATCHES_ALL)

        fun extractCalendarData(body: String): List<String> =
            RX_CALDATA.findAll(body).map { unescapeXml(it.groupValues[1]) }.toList()

        private fun unescapeXml(s: String): String = s
            .replace("&lt;", "<").replace("&gt;", ">").replace("&quot;", "\"")
            .replace("&apos;", "'").replace("&amp;", "&")

        fun parseIcs(ics: String, calendarId: String): List<CalendarEvent> {
            if (ics.isBlank()) return emptyList()
            val out = ArrayList<CalendarEvent>()
            for (m in RX_EVENT.findAll(ics)) {
                val body = m.groupValues[1]
                fun get(key: String): String {
                    val line = Regex("(?m)^${Regex.escape(key)}(?:;[^:]*)?:(.*)$").find(body)
                    return line?.groupValues?.get(1)?.trim() ?: ""
                }
                fun time(key: String): Instant {
                    val v = get(key)
                    if (v.isEmpty()) return Instant.MIN
                    // "yyyyMMdd'T'HHmmss'Z'" — the trailing Z is a literal, so parse
                    // the local part and pin it to UTC (mirrors the C# AssumeUniversal).
                    runCatching {
                        return java.time.LocalDateTime.parse(v, ICS_LOCAL).atOffset(ZoneOffset.UTC).toInstant()
                    }
                    runCatching {
                        val d = java.time.LocalDate.parse(v, DateTimeFormatter.ofPattern("yyyyMMdd"))
                        return d.atStartOfDay(ZoneOffset.UTC).toInstant()
                    }
                    return Instant.MIN
                }
                val uid = get("UID")
                val title = get("SUMMARY")
                val desc = get("DESCRIPTION")
                val loc = get("LOCATION")
                val startUtc = time("DTSTART")
                val endUtc = time("DTEND")
                val allDay = startUtc != Instant.MIN &&
                    isMidnight(startUtc) && isMidnight(endUtc)
                out += CalendarEvent(
                    eventId = uid,
                    calendarId = calendarId,
                    title = title,
                    description = desc.ifEmpty { null },
                    location = loc.ifEmpty { null },
                    startUtc = startUtc,
                    endUtc = endUtc,
                    isAllDay = allDay,
                    attendees = emptyList(),
                )
            }
            return out
        }

        private fun isMidnight(i: Instant): Boolean {
            if (i == Instant.MIN) return false
            val t = i.atOffset(ZoneOffset.UTC).toLocalTime()
            return t == java.time.LocalTime.MIDNIGHT
        }

        fun buildIcs(ev: CalendarEvent): String {
            val dtStamp = ICS_STAMP.format(Instant.now())
            val dtStart = ICS_STAMP.format(ev.startUtc)
            val dtEnd = ICS_STAMP.format(ev.endUtc)
            val sb = StringBuilder()
            sb.appendLine("BEGIN:VCALENDAR")
            sb.appendLine("VERSION:2.0")
            sb.appendLine("PRODID:-//CircleAI//Calendar//EN")
            sb.appendLine("BEGIN:VEVENT")
            sb.appendLine("UID:${ev.eventId}")
            sb.appendLine("DTSTAMP:$dtStamp")
            sb.appendLine("DTSTART:$dtStart")
            sb.appendLine("DTEND:$dtEnd")
            sb.appendLine("SUMMARY:${escapeIcs(ev.title)}")
            if (!ev.description.isNullOrEmpty()) sb.appendLine("DESCRIPTION:${escapeIcs(ev.description)}")
            if (!ev.location.isNullOrEmpty()) sb.appendLine("LOCATION:${escapeIcs(ev.location)}")
            sb.appendLine("END:VEVENT")
            sb.appendLine("END:VCALENDAR")
            return sb.toString()
        }

        private fun escapeIcs(s: String): String = s
            .replace("\\", "\\\\").replace("\n", "\\n").replace(",", "\\,").replace(";", "\\;")
    }
}

// =====================================================================
// Google Calendar v3 (GoogleCalendarConnector.cs)
// =====================================================================

/**
 * Google Calendar connector config. Mirrors C# `GoogleCalendarOptions`.
 * @param accessTokenProvider Async callback returning a fresh Bearer token.
 * @param calendarId Calendar to read/write. Default "primary".
 */
data class GoogleCalendarOptions(
    val accessTokenProvider: AccessTokenProvider,
    val calendarId: String = "primary",
)

/** Google Calendar v3 connector. Mirrors C# `GoogleCalendarConnector`. */
class GoogleCalendarConnector(
    private val opts: GoogleCalendarOptions,
    private val http: HttpTransport,
) : ICalendarConnector {

    private val baseUri = "https://www.googleapis.com/calendar/v3/"

    override val providerId: String get() = "google-calendar"
    override val isConfigured: Boolean get() = true // accessTokenProvider is non-null by construction

    override suspend fun listEvents(fromUtc: Instant, toUtc: Instant): List<CalendarEvent> {
        val token = ensureAuth()
        val path = "calendars/${esc(opts.calendarId)}/events" +
            "?timeMin=${esc(fromUtc.toIsoO())}" +
            "&timeMax=${esc(toUtc.toIsoO())}" +
            "&singleEvents=true&orderBy=startTime&maxResults=250"
        val resp = http.send(HttpRequest(HttpVerb.GET, baseUri + path, bearer(token))).ensureSuccess()
        val root = CAL_JSON.parseToJsonElement(resp.body).jsonObject

        val list = ArrayList<CalendarEvent>()
        val items = root["items"] as? JsonArray ?: return list
        for (ev in items) {
            val o = ev.jsonObject
            if ((o["status"] as? JsonPrimitive)?.content == "cancelled") continue
            val (startUtc, isAllDay) = parseTime(o, "start")
            val (endUtc, _) = parseTime(o, "end")
            val attendees = ArrayList<String>()
            (o["attendees"] as? JsonArray)?.forEach { a ->
                (a.jsonObject["email"] as? JsonPrimitive)?.let { attendees += it.content }
            }
            list += CalendarEvent(
                eventId = o.string("id") ?: "",
                calendarId = opts.calendarId,
                title = o.string("summary") ?: "",
                description = o.string("description"),
                location = o.string("location"),
                startUtc = startUtc,
                endUtc = endUtc,
                isAllDay = isAllDay,
                attendees = attendees,
            )
        }
        return list
    }

    override suspend fun createEvent(ev: CalendarEvent): CalendarEvent {
        val token = ensureAuth()
        val body = buildJsonObject {
            put("summary", ev.title)
            ev.description?.let { put("description", it) }
            ev.location?.let { put("location", it) }
            if (ev.isAllDay) {
                put("start", buildJsonObject { put("date", ev.startUtc.toUtcDate()) })
                put("end", buildJsonObject { put("date", ev.endUtc.toUtcDate()) })
            } else {
                put("start", buildJsonObject { put("dateTime", ev.startUtc.toIsoO()); put("timeZone", "UTC") })
                put("end", buildJsonObject { put("dateTime", ev.endUtc.toIsoO()); put("timeZone", "UTC") })
            }
            put(
                "attendees",
                buildJsonArray { ev.attendees.forEach { add(buildJsonObject { put("email", it) }) } },
            )
        }
        val resp = http.send(
            HttpRequest(
                HttpVerb.POST,
                baseUri + "calendars/${esc(ev.calendarId)}/events",
                bearer(token),
                body.toString(),
                "application/json",
            ),
        ).ensureSuccess()
        val id = CAL_JSON.parseToJsonElement(resp.body).jsonObject.string("id") ?: ""
        return ev.copy(eventId = id)
    }

    override suspend fun deleteEvent(calendarId: String, eventId: String) {
        require(calendarId.isNotBlank()) { "calendarId required" }
        require(eventId.isNotBlank()) { "eventId required" }
        val token = ensureAuth()
        val resp = http.send(
            HttpRequest(
                HttpVerb.DELETE,
                baseUri + "calendars/${esc(calendarId)}/events/${esc(eventId)}",
                bearer(token),
            ),
        )
        if (resp.status != 204 && resp.status != 410) resp.ensureSuccess()
    }

    private suspend fun ensureAuth(): String {
        val token = opts.accessTokenProvider.getToken()
        if (token.isNullOrBlank()) error("Google Calendar access token unavailable; refresh OAuth.")
        return token
    }

    private fun bearer(token: String) = mapOf("Authorization" to "Bearer $token")

    private companion object {
        fun parseTime(parent: JsonObject, property: String): Pair<Instant, Boolean> {
            val node = parent[property] as? JsonObject ?: return Instant.MIN to false
            (node["dateTime"] as? JsonPrimitive)?.let { dt ->
                runCatching { return java.time.OffsetDateTime.parse(dt.content).toInstant() to false }
            }
            (node["date"] as? JsonPrimitive)?.let { d ->
                runCatching {
                    val date = java.time.LocalDate.parse(d.content)
                    return date.atStartOfDay(ZoneOffset.UTC).toInstant() to true
                }
            }
            return Instant.MIN to false
        }
    }
}

// =====================================================================
// Microsoft Graph 1.0 (MsGraphCalendarConnector.cs)
// =====================================================================

/**
 * Microsoft Graph calendar connector config. Mirrors C# `MsGraphCalendarOptions`.
 * @param accessTokenProvider Async callback returning a fresh Bearer token.
 * @param calendarId Calendar to read/write. Default "primary".
 */
data class MsGraphCalendarOptions(
    val accessTokenProvider: AccessTokenProvider,
    val calendarId: String = "primary",
)

/** Microsoft Graph 1.0 calendar connector. Mirrors C# `MsGraphCalendarConnector`. */
class MsGraphCalendarConnector(
    private val opts: MsGraphCalendarOptions,
    private val http: HttpTransport,
) : ICalendarConnector {

    private val baseUri = "https://graph.microsoft.com/v1.0/"

    override val providerId: String get() = "ms-graph-calendar"
    override val isConfigured: Boolean get() = true

    override suspend fun listEvents(fromUtc: Instant, toUtc: Instant): List<CalendarEvent> {
        val token = ensureAuth()
        val path = "me/calendar/calendarView" +
            "?startDateTime=${esc(fromUtc.toIsoO())}" +
            "&endDateTime=${esc(toUtc.toIsoO())}" +
            "&\$top=250&\$orderby=start/dateTime"
        val resp = http.send(HttpRequest(HttpVerb.GET, baseUri + path, bearer(token))).ensureSuccess()
        val root = CAL_JSON.parseToJsonElement(resp.body).jsonObject

        val list = ArrayList<CalendarEvent>()
        val arr = root["value"] as? JsonArray ?: return list
        for (ev in arr) {
            val o = ev.jsonObject
            val attendees = ArrayList<String>()
            (o["attendees"] as? JsonArray)?.forEach { a ->
                val addr = (a.jsonObject["emailAddress"] as? JsonObject)?.string("address")
                if (addr != null) attendees += addr
            }
            val startUtc = parseGraphTime(o, "start")
            val endUtc = parseGraphTime(o, "end")
            val allDay = (o["isAllDay"] as? JsonPrimitive)?.content?.toBoolean() ?: false
            val location = (o["location"] as? JsonObject)?.string("displayName")
            list += CalendarEvent(
                eventId = o.string("id") ?: "",
                calendarId = opts.calendarId,
                title = o.string("subject") ?: "",
                description = o.string("bodyPreview"),
                location = location,
                startUtc = startUtc,
                endUtc = endUtc,
                isAllDay = allDay,
                attendees = attendees,
            )
        }
        return list
    }

    override suspend fun createEvent(ev: CalendarEvent): CalendarEvent {
        val token = ensureAuth()
        val body = buildJsonObject {
            put("subject", ev.title)
            put("body", buildJsonObject { put("contentType", "text"); put("content", ev.description ?: "") })
            put("start", buildJsonObject { put("dateTime", ev.startUtc.toIsoO()); put("timeZone", "UTC") })
            put("end", buildJsonObject { put("dateTime", ev.endUtc.toIsoO()); put("timeZone", "UTC") })
            put("isAllDay", ev.isAllDay)
            put("location", buildJsonObject { put("displayName", ev.location ?: "") })
            put(
                "attendees",
                buildJsonArray {
                    ev.attendees.forEach {
                        add(
                            buildJsonObject {
                                put("emailAddress", buildJsonObject { put("address", it) })
                                put("type", "required")
                            },
                        )
                    }
                },
            )
        }
        val resp = http.send(
            HttpRequest(HttpVerb.POST, baseUri + "me/events", bearer(token), body.toString(), "application/json"),
        ).ensureSuccess()
        val id = CAL_JSON.parseToJsonElement(resp.body).jsonObject.string("id") ?: ""
        return ev.copy(eventId = id)
    }

    override suspend fun deleteEvent(calendarId: String, eventId: String) {
        require(eventId.isNotBlank()) { "eventId required" }
        val token = ensureAuth()
        val resp = http.send(HttpRequest(HttpVerb.DELETE, baseUri + "me/events/${esc(eventId)}", bearer(token)))
        if (resp.status != 204) resp.ensureSuccess()
    }

    private suspend fun ensureAuth(): String {
        val token = opts.accessTokenProvider.getToken()
        if (token.isNullOrBlank()) error("Microsoft Graph access token unavailable; refresh OAuth.")
        return token
    }

    private fun bearer(token: String) = mapOf("Authorization" to "Bearer $token")

    private companion object {
        fun parseGraphTime(parent: JsonObject, property: String): Instant {
            val node = parent[property] as? JsonObject ?: return Instant.MIN
            val dt = (node["dateTime"] as? JsonPrimitive)?.content ?: return Instant.MIN
            if (dt.isEmpty()) return Instant.MIN
            runCatching {
                // Graph emits naive datetimes with a separate timeZone; assume UTC.
                return java.time.LocalDateTime.parse(dt.substringBefore("+").trimEnd('Z'))
                    .atOffset(ZoneOffset.UTC).toInstant()
            }
            runCatching { return java.time.OffsetDateTime.parse(dt).toInstant() }
            return Instant.MIN
        }
    }
}
