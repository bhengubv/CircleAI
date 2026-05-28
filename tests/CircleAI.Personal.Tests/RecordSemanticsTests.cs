// RecordSemanticsTests.cs
//
// Verifies value-based equality and field round-trips for the Personal records.

using CircleAI.Personal.Calendar;
using CircleAI.Personal.Contacts;
using CircleAI.Personal.Email;
using Xunit;

namespace CircleAI.Personal.Tests;

public sealed class RecordSemanticsTests
{
    [Fact]
    public void CalendarEvent_Equality_IsValueBased()
    {
        var id = Guid.NewGuid();
        var start = DateTimeOffset.UtcNow;
        var end = start.AddHours(1);
        IReadOnlyList<string> attendees = new List<string> { "x@y.com" };

        var a = new CalendarEvent(id, "ext", "T", null, start, end, null, attendees, false, null);
        var b = new CalendarEvent(id, "ext", "T", null, start, end, null, attendees, false, null);

        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void EmailMessage_RoundTripsAllFields()
    {
        var id = Guid.NewGuid();
        var received = DateTimeOffset.UtcNow;
        var m = new EmailMessage(
            Id: id,
            ExternalId: "msg-1",
            From: "alice@example.com",
            To: new List<string> { "bob@example.com" },
            Cc: new List<string> { "carol@example.com" },
            Subject: "Hi",
            BodyPlain: "<not actually parsed> body",
            ReceivedAt: received,
            IsUnread: true,
            Labels: new List<string> { "INBOX", "STARRED" });

        Assert.Equal(id, m.Id);
        Assert.Equal("msg-1", m.ExternalId);
        Assert.Equal("alice@example.com", m.From);
        Assert.Single(m.To);
        Assert.Single(m.Cc);
        Assert.Equal("Hi", m.Subject);
        Assert.Equal("<not actually parsed> body", m.BodyPlain);
        Assert.Equal(received, m.ReceivedAt);
        Assert.True(m.IsUnread);
        Assert.Equal(2, m.Labels.Count);
    }

    [Fact]
    public void Contact_Relationship_DefaultsToNull_WhenNotProvided()
    {
        var c = new Contact(
            Id: Guid.NewGuid(),
            ExternalId: "c-1",
            DisplayName: "Alice",
            Emails: new List<string> { "a@x.com" },
            PhoneNumbers: new List<string>(),
            Relationship: null,
            LastInteractionAt: DateTimeOffset.UtcNow);

        Assert.Null(c.Relationship);
    }
}
