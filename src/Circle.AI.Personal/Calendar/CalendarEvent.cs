// CalendarEvent.cs

namespace Circle.AI.Personal.Calendar;

/// <summary>
/// A user calendar event normalised across providers.
/// </summary>
/// <param name="Id">Stable identifier within Circle.</param>
/// <param name="ExternalId">Provider-native identifier (Google event id, Microsoft Graph event id, etc.).</param>
/// <param name="Title">Event title.</param>
/// <param name="Description">Optional long-form description.</param>
/// <param name="StartUtc">Start time in UTC.</param>
/// <param name="EndUtc">End time in UTC.</param>
/// <param name="Location">Free-text location, or null.</param>
/// <param name="AttendeeEmails">Email addresses of invitees.</param>
/// <param name="IsAllDay">True for all-day events.</param>
/// <param name="RecurrenceRule">RFC 5545 RRULE string, or null. Opaque to this package.</param>
public sealed record CalendarEvent(
    Guid Id,
    string ExternalId,
    string Title,
    string? Description,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    string? Location,
    IReadOnlyList<string> AttendeeEmails,
    bool IsAllDay,
    string? RecurrenceRule
);
