// NullCalendarAdapter.cs
//
// Empty calendar that still enforces the consent contract. Use as the default
// adapter when no provider has been connected, or as a baseline in tests.

namespace Circle.AI.Personal.Calendar;

/// <summary>
/// A calendar adapter that holds no events. List operations return empty;
/// write operations throw <see cref="InvalidOperationException"/>. All
/// methods enforce the consent contract before returning or throwing.
/// </summary>
public sealed class NullCalendarAdapter : ICalendarAdapter
{
    /// <inheritdoc />
    public Task<IReadOnlyList<CalendarEvent>> ListEventsAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        UserConsentToken consent,
        CancellationToken cancellationToken)
    {
        ConsentGuard.Require(consent, ConsentScope.CalendarRead);
        return Task.FromResult<IReadOnlyList<CalendarEvent>>(Array.Empty<CalendarEvent>());
    }

    /// <inheritdoc />
    public Task<CalendarEvent> CreateEventAsync(
        CalendarEvent ev,
        UserConsentToken consent,
        CancellationToken cancellationToken)
    {
        ConsentGuard.Require(consent, ConsentScope.CalendarWrite);
        throw new InvalidOperationException(
            "NullCalendarAdapter cannot create events. Bind a concrete adapter (Google, Microsoft Graph, iOS EventKit, ...).");
    }

    /// <inheritdoc />
    public Task<CalendarEvent> UpdateEventAsync(
        CalendarEvent ev,
        UserConsentToken consent,
        CancellationToken cancellationToken)
    {
        ConsentGuard.Require(consent, ConsentScope.CalendarWrite);
        throw new InvalidOperationException(
            "NullCalendarAdapter cannot update events. Bind a concrete adapter (Google, Microsoft Graph, iOS EventKit, ...).");
    }

    /// <inheritdoc />
    public Task DeleteEventAsync(
        Guid id,
        UserConsentToken consent,
        CancellationToken cancellationToken)
    {
        ConsentGuard.Require(consent, ConsentScope.CalendarWrite);
        throw new InvalidOperationException(
            "NullCalendarAdapter cannot delete events. Bind a concrete adapter (Google, Microsoft Graph, iOS EventKit, ...).");
    }
}
