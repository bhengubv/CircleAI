// UserConsentTokenTests.cs

using Circle.AI.Personal;
using Xunit;

namespace Circle.AI.Personal.Tests;

public sealed class UserConsentTokenTests
{
    private static UserConsentToken MakeToken(
        IReadOnlyList<ConsentScope> scopes,
        DateTimeOffset? expiresAt = null,
        byte[]? signature = null) =>
        new(
            Id: Guid.NewGuid(),
            UhidIdentityId: "uhid-test-identity",
            Scopes: scopes,
            GrantedAt: DateTimeOffset.UtcNow.AddMinutes(-1),
            ExpiresAt: expiresAt ?? DateTimeOffset.UtcNow.AddHours(1),
            Signature: signature ?? new byte[] { 0x01, 0x02, 0x03, 0x04 });

    [Fact]
    public void IsValidFor_GrantedScopeBeforeExpiry_ReturnsTrue()
    {
        var t = MakeToken(new[] { ConsentScope.CalendarRead });

        Assert.True(t.IsValidFor(ConsentScope.CalendarRead, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void IsValidFor_GrantedScopeAfterExpiry_ReturnsFalse()
    {
        var t = MakeToken(
            new[] { ConsentScope.CalendarRead },
            expiresAt: DateTimeOffset.UtcNow.AddHours(-1));

        Assert.False(t.IsValidFor(ConsentScope.CalendarRead, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void IsValidFor_UngrantedScope_ReturnsFalse()
    {
        var t = MakeToken(new[] { ConsentScope.CalendarRead });

        Assert.False(t.IsValidFor(ConsentScope.CalendarWrite, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Signature_IsPreservedVerbatim()
    {
        var sig = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x42, 0x00, 0x99 };
        var t = MakeToken(new[] { ConsentScope.CalendarRead }, signature: sig);

        Assert.Equal(sig, t.Signature);
        Assert.Same(sig, t.Signature);
    }
}
