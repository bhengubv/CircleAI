// PersonalCalendar.kt
//
// Kotlin port of CircleAI.Personal.Calendar — the C# reference is the EXACT
// spec. Provider-neutral calendar contract + a consent-enforcing null adapter.
//
// Covers (C# file -> Kotlin type):
//   CalendarEvent.cs       -> CalendarEvent
//   ICalendarAdapter.cs    -> ICalendarAdapter
//   NullCalendarAdapter.cs -> NullCalendarAdapter
//
// Fidelity notes:
//   * C# `record` -> `data class`; `Guid` -> `UUID`; `DateTimeOffset` ->
//     `Instant`; `IReadOnlyList` -> `List`; `Task<T>` -> `suspend fun`.
//   * Every method requires a [UserConsentToken]; the null adapter calls
//     [ConsentGuard.require] before returning/throwing, exactly as in C#.
//   * Write ops on the null adapter throw `IllegalStateException`
//     (JVM stand-in for C# `InvalidOperationException`) with the verbatim
//     "bind a concrete adapter" message.

package com.bhengubv.circleai.personal.calendar

import com.bhengubv.circleai.personal.ConsentGuard
import com.bhengubv.circleai.personal.ConsentScope
import com.bhengubv.circleai.personal.UserConsentToken
import java.time.Instant
import java.util.UUID

/**
 * A user calendar event normalised across providers.
 *
 * @property recurrenceRule RFC 5545 RRULE string, or null. Opaque to this package.
 */
data class CalendarEvent(
    val id: UUID,
    val externalId: String,
    val title: String,
    val description: String?,
    val startUtc: Instant,
    val endUtc: Instant,
    val location: String?,
    val attendeeEmails: List<String>,
    val isAllDay: Boolean,
    val recurrenceRule: String?,
)

/**
 * Contract for a calendar adapter. Concrete implementations bind to a specific
 * provider (Google Calendar, Microsoft Graph, iOS EventKit, …) and ship in
 * separate packages.
 *
 * Every method requires a [UserConsentToken]. Implementations MUST throw when
 * the token lacks the required [ConsentScope] or has expired.
 */
interface ICalendarAdapter {
    /** Lists events overlapping [from]↔[to]. Requires [ConsentScope.CalendarRead]. */
    suspend fun listEvents(from: Instant, to: Instant, consent: UserConsentToken): List<CalendarEvent>

    /** Creates a new event. Requires [ConsentScope.CalendarWrite]. */
    suspend fun createEvent(ev: CalendarEvent, consent: UserConsentToken): CalendarEvent

    /** Updates an existing event. Requires [ConsentScope.CalendarWrite]. */
    suspend fun updateEvent(ev: CalendarEvent, consent: UserConsentToken): CalendarEvent

    /** Deletes an event by Circle id. Requires [ConsentScope.CalendarWrite]. */
    suspend fun deleteEvent(id: UUID, consent: UserConsentToken)
}

/**
 * A calendar adapter that holds no events. List operations return empty; write
 * operations throw. All methods enforce the consent contract before returning
 * or throwing.
 */
class NullCalendarAdapter : ICalendarAdapter {
    override suspend fun listEvents(
        from: Instant,
        to: Instant,
        consent: UserConsentToken,
    ): List<CalendarEvent> {
        ConsentGuard.require(consent, ConsentScope.CalendarRead)
        return emptyList()
    }

    override suspend fun createEvent(ev: CalendarEvent, consent: UserConsentToken): CalendarEvent {
        ConsentGuard.require(consent, ConsentScope.CalendarWrite)
        throw IllegalStateException(
            "NullCalendarAdapter cannot create events. Bind a concrete adapter " +
                "(Google, Microsoft Graph, iOS EventKit, ...).",
        )
    }

    override suspend fun updateEvent(ev: CalendarEvent, consent: UserConsentToken): CalendarEvent {
        ConsentGuard.require(consent, ConsentScope.CalendarWrite)
        throw IllegalStateException(
            "NullCalendarAdapter cannot update events. Bind a concrete adapter " +
                "(Google, Microsoft Graph, iOS EventKit, ...).",
        )
    }

    override suspend fun deleteEvent(id: UUID, consent: UserConsentToken) {
        ConsentGuard.require(consent, ConsentScope.CalendarWrite)
        throw IllegalStateException(
            "NullCalendarAdapter cannot delete events. Bind a concrete adapter " +
                "(Google, Microsoft Graph, iOS EventKit, ...).",
        )
    }
}
