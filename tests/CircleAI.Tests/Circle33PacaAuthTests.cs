// Circle33PacaAuthTests.cs
//
// (3.3.0) Tests for HMAC JWT + API-key auth.

using System;
using System.Collections.Generic;
using CircleAI.Workflows;
using Xunit;

namespace CircleAI.Tests;

public class Circle33PacaAuthTests
{
    private const string Secret = "this-is-a-test-signing-secret-32-chars";

    [Fact]
    public void Issue_AndVerify_AccessToken()
    {
        var auth = new HmacJwtAuthenticator(Secret);
        var pair = auth.Issue("user-1", new Dictionary<string, string> { ["role"] = "admin" });

        var payload = auth.Verify(pair.AccessToken);
        Assert.NotNull(payload);
        Assert.Equal("user-1", payload!.Subject);
        Assert.Equal("admin", payload.Claims["role"]);
    }

    [Fact]
    public void Verify_AccessTokenAsRefresh_Fails()
    {
        var auth = new HmacJwtAuthenticator(Secret);
        var pair = auth.Issue("user-1");
        Assert.Null(auth.Verify(pair.AccessToken, expectedType: "refresh"));
    }

    [Fact]
    public void Verify_TamperedToken_Fails()
    {
        var auth = new HmacJwtAuthenticator(Secret);
        var pair = auth.Issue("user-1");
        var parts = pair.AccessToken.Split('.');
        var tampered = parts[0] + "." + parts[1] + ".AAAAAAAAAA";
        Assert.Null(auth.Verify(tampered));
    }

    [Fact]
    public void Verify_ExpiredToken_Fails()
    {
        var now  = DateTimeOffset.UtcNow;
        var auth = new HmacJwtAuthenticator(Secret, accessLifetime: TimeSpan.FromSeconds(1), clock: () => now);
        var pair = auth.Issue("user-1");
        now = now + TimeSpan.FromSeconds(2);
        Assert.Null(auth.Verify(pair.AccessToken));
    }

    [Fact]
    public void Verify_RefreshToken_Succeeds()
    {
        var auth = new HmacJwtAuthenticator(Secret);
        var pair = auth.Issue("user-1");
        var payload = auth.Verify(pair.RefreshToken, "refresh");
        Assert.NotNull(payload);
        Assert.Equal("user-1", payload!.Subject);
    }

    [Fact]
    public void Constructor_WeakSecret_Throws()
    {
        Assert.Throws<ArgumentException>(() => new HmacJwtAuthenticator("short"));
    }

    [Fact]
    public void Issue_EmptySubject_Throws()
    {
        var auth = new HmacJwtAuthenticator(Secret);
        Assert.Throws<ArgumentException>(() => auth.Issue(""));
    }

    [Fact]
    public void ApiKey_IssueAndVerify_Roundtrips()
    {
        var keys = new PacaApiKeyAuthenticator();
        var (record, secret) = keys.Issue("ci-runner");

        var verified = keys.Verify(record.KeyId, secret);
        Assert.NotNull(verified);
        Assert.Equal("ci-runner", verified!.Label);
    }

    [Fact]
    public void ApiKey_WrongSecret_Fails()
    {
        var keys = new PacaApiKeyAuthenticator();
        var (record, _) = keys.Issue("ci-runner");
        Assert.Null(keys.Verify(record.KeyId, "wrong-secret"));
    }

    [Fact]
    public void ApiKey_RevokedKey_Fails()
    {
        var keys = new PacaApiKeyAuthenticator();
        var (record, secret) = keys.Issue("ci-runner");
        keys.Revoke(record.KeyId);
        Assert.Null(keys.Verify(record.KeyId, secret));
    }

    [Fact]
    public void ApiKey_UnknownKeyId_Fails()
    {
        var keys = new PacaApiKeyAuthenticator();
        Assert.Null(keys.Verify("ghost-key", "anything"));
    }

    [Fact]
    public void ApiKey_EmptyLabel_Throws()
    {
        var keys = new PacaApiKeyAuthenticator();
        Assert.Throws<ArgumentException>(() => keys.Issue(""));
    }
}
