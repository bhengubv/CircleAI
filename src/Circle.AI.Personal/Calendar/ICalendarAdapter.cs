// ICalendarAdapter.cs

namespace Circle.AI.Personal.Calendar;

/// <summary>
/// Contract for a calendar adapter. Concrete implementations bind to a specific
/// provider (Google Calendar, Microsoft Graph, iOS EventKit, …) and ship in
/// separate packages.
/// </summary>
/// <remarks>
/// Every method requires a <see cref="UserConsentToken"/>. Implementations
/// MUST throw <see cref="UnauthorizedAccessException"/> when the token lacks
/// the required <see cref="ConsentScope"/> or has expired.
/// </remarks>
public interface ICalendarAdapter
{
    /// <summary>
    /// Lists events in the inclusive time range <paramref name="from"/> ↔ <paramref name="to"/>.
    /// Requires <see cref="ConsentScope.CalendarRead"/>.
    /// </summary>
    /// <param name="from">Start of the range (UTC).</param>
    /// <param name="to">End of the range (UTC).</param>
    /// <param name="consent">User consent token.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Events overlapping the range.</returns>
    Task<IReadOnlyList<CalendarEvent>> ListEventsAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        UserConsentToken consent,
        CancellationToken cancellationToken);

    /// <summary>
    /// Creates a new event. Requires <see cref="ConsentScope.CalendarWrite"/>.
    /// </summary>
    /// <param name="ev">The event to create.</param>
    /// <param name="consent">User consent token.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The created event, populated with provider-assigned identifiers.</returns>
    Task<CalendarEvent> CreateEventAsync(
        CalendarEvent ev,
        UserConsentToken consent,
        CancellationToken cancellationToken);

    /// <summary>
    /// Updates an existing event. Requires <see cref="ConsentScope.CalendarWrite"/>.
    /// </summary>
    /// <param name="ev">The updated event. Must carry the existing <see cref="CalendarEvent.ExternalId"/>.</param>
    /// <param name="consent">User consent token.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated event as persisted by the provider.</returns>
    Task<CalendarEvent> UpdateEventAsync(
        CalendarEvent ev,
        UserConsentToken consent,
        CancellationToken cancellationToken);

    /// <summary>
    /// Deletes an event by Circle id. Requires <see cref="ConsentScope.CalendarWrite"/>.
    /// </summary>
    /// <param name="id">Circle identifier of the event.</param>
    /// <param name="consent">User consent token.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task DeleteEventAsync(
        Guid id,
        UserConsentToken consent,
        CancellationToken cancellationToken);
}
