// NullContactsAdapterTests.cs

using CircleAI.Personal;
using CircleAI.Personal.Contacts;
using Xunit;

namespace CircleAI.Personal.Tests;

public sealed class NullContactsAdapterTests
{
    private static UserConsentToken Consent(params ConsentScope[] scopes) =>
        new(
            Id: Guid.NewGuid(),
            UhidIdentityId: "uhid-test",
            Scopes: scopes,
            GrantedAt: DateTimeOffset.UtcNow.AddMinutes(-1),
            ExpiresAt: DateTimeOffset.UtcNow.AddHours(1),
            Signature: new byte[] { 0x03 });

    [Fact]
    public async Task SearchAsync_WithoutContactsRead_Throws()
    {
        var adapter = new NullContactsAdapter();
        var bad = Consent(ConsentScope.EmailRead);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            adapter.SearchAsync("alice", bad, CancellationToken.None));
    }

    [Fact]
    public async Task SearchAsync_WithContactsRead_ReturnsEmpty()
    {
        var adapter = new NullContactsAdapter();
        var ok = Consent(ConsentScope.ContactsRead);

        var results = await adapter.SearchAsync("alice", ok, CancellationToken.None);

        Assert.Empty(results);
    }
}
