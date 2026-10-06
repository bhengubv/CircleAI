// PacaAuth.cs
//
// (3.3.0) Auth primitives ported from paca: JWT (access + refresh)
// + API-key validation. Issuance and verification use HMAC-SHA256.
// API keys live in an in-memory store keyed by hashed prefix.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CircleAI.Workflows;

/// <summary>(3.3.0) Token-shaped JWT result.</summary>
public sealed record JwtPair(string AccessToken, string RefreshToken, DateTimeOffset AccessExpiresAtUtc, DateTimeOffset RefreshExpiresAtUtc);

/// <summary>(3.3.0) Verified JWT payload.</summary>
public sealed record JwtPayload(string Subject, IReadOnlyDictionary<string, string> Claims, DateTimeOffset ExpiresAtUtc);

/// <summary>(3.3.0) HMAC-SHA256 JWT issuer + verifier.</summary>
public sealed class HmacJwtAuthenticator
{
    private readonly byte[] _secret;
    private readonly TimeSpan _accessLifetime;
    private readonly TimeSpan _refreshLifetime;
    private readonly Func<DateTimeOffset> _clock;

    public HmacJwtAuthenticator(
        string                signingSecret,
        TimeSpan?             accessLifetime  = null,
        TimeSpan?             refreshLifetime = null,
        Func<DateTimeOffset>? clock           = null)
    {
        if (string.IsNullOrWhiteSpace(signingSecret) || signingSecret.Length < 16)
        {
            throw new ArgumentException("Signing secret must be at least 16 characters.", nameof(signingSecret));
        }
        _secret          = Encoding.UTF8.GetBytes(signingSecret);
        _accessLifetime  = accessLifetime  ?? TimeSpan.FromMinutes(15);
        _refreshLifetime = refreshLifetime ?? TimeSpan.FromDays(7);
        _clock           = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>(3.3.0) Issue access + refresh tokens for <paramref name="subject"/>.</summary>
    public JwtPair Issue(string subject, IReadOnlyDictionary<string, string>? claims = null)
    {
        if (string.IsNullOrWhiteSpace(subject)) throw new ArgumentException("subject required", nameof(subject));
        var now = _clock();
        var accessExp  = now + _accessLifetime;
        var refreshExp = now + _refreshLifetime;
        var access  = EncodeToken(subject, "access",  accessExp,  claims);
        var refresh = EncodeToken(subject, "refresh", refreshExp, null);
        return new JwtPair(access, refresh, accessExp, refreshExp);
    }

    /// <summary>(3.3.0) Verify a token; returns the payload or null if invalid/expired.</summary>
    public JwtPayload? Verify(string token, string expectedType = "access")
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var parts = token.Split('.');
        if (parts.Length != 3) return null;

        var header  = parts[0];
        var payload = parts[1];
        var sig     = parts[2];
        var signing = $"{header}.{payload}";
        var expected = SignBase64Url(signing);
        if (!FixedTimeEquals(expected, sig)) return null;

        Dictionary<string, JsonElement> json;
        try
        {
            var jsonBytes = Base64UrlDecode(payload);
            json = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(jsonBytes) ?? new();
        }
        catch
        {
            return null;
        }

        if (!json.TryGetValue("typ", out var typEl) || typEl.GetString() != expectedType) return null;
        if (!json.TryGetValue("sub", out var subEl) || subEl.GetString() is not string subject) return null;
        if (!json.TryGetValue("exp", out var expEl) || !expEl.TryGetInt64(out var expSeconds)) return null;
        var exp = DateTimeOffset.FromUnixTimeSeconds(expSeconds);
        if (exp <= _clock()) return null;

        var extraClaims = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in json)
        {
            if (k is "typ" or "sub" or "exp") continue;
            extraClaims[k] = v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString();
        }
        return new JwtPayload(subject, extraClaims, exp);
    }

    private string EncodeToken(string subject, string type, DateTimeOffset expires, IReadOnlyDictionary<string, string>? claims)
    {
        var header  = """{"alg":"HS256","typ":"JWT"}""";
        var payload = new Dictionary<string, object>
        {
            ["sub"] = subject,
            ["typ"] = type,
            ["exp"] = expires.ToUnixTimeSeconds(),
        };
        if (claims is not null)
        {
            foreach (var (k, v) in claims) payload[k] = v;
        }
        var headerB  = Base64UrlEncode(Encoding.UTF8.GetBytes(header));
        var payloadB = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(payload));
        var signing  = $"{headerB}.{payloadB}";
        var sig      = SignBase64Url(signing);
        return $"{signing}.{sig}";
    }

    private string SignBase64Url(string signing)
    {
        using var hmac = new HMACSHA256(_secret);
        var sig = hmac.ComputeHash(Encoding.UTF8.GetBytes(signing));
        return Base64UrlEncode(sig);
    }

    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string input)
    {
        var s = input.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "=";  break;
        }
        return Convert.FromBase64String(s);
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        var ba = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        if (ba.Length != bb.Length) return false;
        int diff = 0;
        for (int i = 0; i < ba.Length; i++) diff |= ba[i] ^ bb[i];
        return diff == 0;
    }
}

/// <summary>(3.3.0) Issued API key — store hashes only.</summary>
public sealed record PacaApiKeyRecord(string KeyId, string Label, string HashedSecret, DateTimeOffset CreatedAtUtc, DateTimeOffset? RevokedAtUtc);

/// <summary>(3.3.0) API-key registry separate from JWT user auth.</summary>
public sealed class PacaApiKeyAuthenticator
{
    private readonly ConcurrentDictionary<string, PacaApiKeyRecord> _keys = new();
    private readonly Func<DateTimeOffset> _clock;

    public PacaApiKeyAuthenticator(Func<DateTimeOffset>? clock = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>(3.3.0) Generate a fresh key; the raw <c>secret</c> is returned ONCE for the caller to store.</summary>
    public (PacaApiKeyRecord Record, string RawSecret) Issue(string label)
    {
        if (string.IsNullOrWhiteSpace(label)) throw new ArgumentException("label required", nameof(label));
        var keyId  = Guid.NewGuid().ToString("n");
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=');
        var hashed = Hash(secret);
        var record = new PacaApiKeyRecord(keyId, label, hashed, _clock(), null);
        _keys[keyId] = record;
        return (record, secret);
    }

    /// <summary>(3.3.0) Verify an incoming key. Returns the record if valid and live.</summary>
    public PacaApiKeyRecord? Verify(string keyId, string presentedSecret)
    {
        if (!_keys.TryGetValue(keyId, out var record)) return null;
        if (record.RevokedAtUtc is not null) return null;
        var hashed = Hash(presentedSecret);
        return SlowEquals(hashed, record.HashedSecret) ? record : null;
    }

    /// <summary>(3.3.0) Revoke a key. Idempotent.</summary>
    public void Revoke(string keyId)
    {
        if (!_keys.TryGetValue(keyId, out var existing) || existing.RevokedAtUtc is not null) return;
        _keys[keyId] = existing with { RevokedAtUtc = _clock() };
    }

    private static string Hash(string secret)
        => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(secret))).TrimEnd('=');

    private static bool SlowEquals(string a, string b)
    {
        var ba = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        if (ba.Length != bb.Length) return false;
        int diff = 0;
        for (int i = 0; i < ba.Length; i++) diff |= ba[i] ^ bb[i];
        return diff == 0;
    }
}
