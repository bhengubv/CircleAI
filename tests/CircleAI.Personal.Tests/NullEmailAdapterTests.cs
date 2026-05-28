// NullEmailAdapterTests.cs

using CircleAI.Personal;
using CircleAI.Personal.Email;
using Xunit;

namespace CircleAI.Personal.Tests;

public sealed class NullEmailAdapterTests
{
    private static UserConsentToken Consent(params ConsentScope[] scopes) =>
        new(
            Id: Guid.NewGuid(),
            UhidIdentityId: "uhid-test",
            Scopes: scopes,
            GrantedAt: DateTimeOffset.UtcNow.AddMinutes(-1),
            ExpiresAt: DateTimeOffset.UtcNow.AddHours(1),
            Signature: new byte[] { 0x02 });

    [Fact]
    public async Task ListRecentAsync_WithoutEmailRead_Throws()
    {
        var adapter = new NullEmailAdapter();
        var bad = Consent(ConsentScope.CalendarRead);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            adapter.ListRecentAsync(10, bad, CancellationToken.None));
    }

    [Fact]
    public async Task DraftReplyAsync_WithEmailReadOnly_Throws_RequiresEmailDraft()
    {
        var adapter = new NullEmailAdapter();
        var readOnly = Consent(ConsentScope.EmailRead);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            adapter.DraftReplyAsync("ext-msg-1", "hello", readOnly, CancellationToken.None));
    }

    [Fact]
    public async Task DraftReplyAsync_WithEmailDraft_ReturnsFreshGuidEachCall()
    {
        var adapter = new NullEmailAdapter();
        var ok = Consent(ConsentScope.EmailDraft);

        var a = await adapter.DraftReplyAsync("ext-msg-1", "hello", ok, CancellationToken.None);
        var b = await adapter.DraftReplyAsync("ext-msg-1", "hello", ok, CancellationToken.None);
        var c = await adapter.DraftReplyAsync("ext-msg-2", "again", ok, CancellationToken.None);

        Assert.NotEqual(Guid.Empty, a);
        Assert.NotEqual(Guid.Empty, b);
        Assert.NotEqual(Guid.Empty, c);
        Assert.NotEqual(a, b);
        Assert.NotEqual(b, c);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public async Task GetByIdAsync_WithEmailRead_ReturnsNull()
    {
        var adapter = new NullEmailAdapter();
        var ok = Consent(ConsentScope.EmailRead);

        var msg = await adapter.GetByIdAsync("ext-missing", ok, CancellationToken.None);

        Assert.Null(msg);
    }
}
