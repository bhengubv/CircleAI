// NullCalendarAdapterTests.cs

using CircleAI.Personal;
using CircleAI.Personal.Calendar;
using Xunit;

namespace CircleAI.Personal.Tests;

public sealed class NullCalendarAdapterTests
{
    private static UserConsentToken Consent(params ConsentScope[] scopes) =>
        new(
            Id: Guid.NewGuid(),
            UhidIdentityId: "uhid-test",
            Scopes: scopes,
            GrantedAt: DateTimeOffset.UtcNow.AddMinutes(-1),
            ExpiresAt: DateTimeOffset.UtcNow.AddHours(1),
            Signature: new byte[] { 0x01 });

    private static CalendarEvent SampleEvent() => new(
        Id: Guid.NewGuid(),
        ExternalId: "ext-evt-1",
        Title: "Standup",
        Description: null,
        StartUtc: DateTimeOffset.UtcNow.AddHours(1),
        EndUtc: DateTimeOffset.UtcNow.AddHours(2),
        Location: null,
        AttendeeEmails: new List<string> { "a@example.com" },
        IsAllDay: false,
        RecurrenceRule: null);

    [Fact]
    public async Task ListEventsAsync_WithoutCalendarRead_Throws()
    {
        var adapter = new NullCalendarAdapter();
        var bad = Consent(ConsentScope.EmailRead);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            adapter.ListEventsAsync(
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddDays(1),
                bad,
                CancellationToken.None));
    }

    [Fact]
    public async Task ListEventsAsync_WithCalendarRead_ReturnsEmpty()
    {
        var adapter = new NullCalendarAdapter();
        var ok = Consent(ConsentScope.CalendarRead);

        var events = await adapter.ListEventsAsync(
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddDays(1),
            ok,
            CancellationToken.None);

        Assert.Empty(events);
    }

    [Fact]
    public async Task CreateEventAsync_WithCalendarReadOnly_ThrowsUnauthorized()
    {
        var adapter = new NullCalendarAdapter();
        var readOnly = Consent(ConsentScope.CalendarRead);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            adapter.CreateEventAsync(SampleEvent(), readOnly, CancellationToken.None));
    }

    [Fact]
    public async Task UpdateEventAsync_WithCalendarWrite_ThrowsInvalidOperation_WithDescriptiveMessage()
    {
        var adapter = new NullCalendarAdapter();
        var write = Consent(ConsentScope.CalendarWrite);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.UpdateEventAsync(SampleEvent(), write, CancellationToken.None));

        Assert.Contains("NullCalendarAdapter", ex.Message);
        Assert.Contains("concrete adapter", ex.Message);
    }

    [Fact]
    public async Task DeleteEventAsync_WithoutCalendarWrite_Throws()
    {
        var adapter = new NullCalendarAdapter();
        var bad = Consent(ConsentScope.CalendarRead);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            adapter.DeleteEventAsync(Guid.NewGuid(), bad, CancellationToken.None));
    }
}
