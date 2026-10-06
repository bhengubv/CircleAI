// InMemoryToolsCatalog.cs
//
// (3.3.0) Real in-memory tools-catalog primitives. The provider
// catalog supports substring + tag search; credentials are encrypted
// at rest via AES-GCM with a host-supplied key. OAuth2 flow driver
// builds standards-compliant authorize URLs; CompleteAsync delegates
// the token-exchange HTTP call (which would be vendor-specific) to a
// host function. Quota guard enforces concurrent + per-minute caps.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Tools.Catalog;

public sealed class InMemoryProviderCatalog : IProviderCatalog
{
    private readonly ConcurrentDictionary<string, ProviderDescriptor> _items = new(StringComparer.OrdinalIgnoreCase);
    public string BackendId => "in-memory";

    public void Register(ProviderDescriptor p) { ArgumentNullException.ThrowIfNull(p); _items[p.ProviderId] = p; }

    public ValueTask<IReadOnlyList<ProviderDescriptor>> ListProvidersAsync(CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<ProviderDescriptor>>(_items.Values.OrderBy(p => p.ProviderId).ToArray());

    public ValueTask<ProviderDescriptor?> GetProviderAsync(string providerId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(providerId)) throw new ArgumentException("providerId required", nameof(providerId));
        _items.TryGetValue(providerId, out var p);
        return ValueTask.FromResult(p);
    }

    public ValueTask<IReadOnlyList<ProviderDescriptor>> SearchProvidersAsync(string query, int topK = 8, CancellationToken ct = default)
    {
        if (query is null) throw new ArgumentNullException(nameof(query));
        if (topK <= 0)     throw new ArgumentOutOfRangeException(nameof(topK));
        var hits = _items.Values
            .Select(p => new { p, s = Score(p, query) })
            .Where(x => x.s > 0)
            .OrderByDescending(x => x.s)
            .Take(topK)
            .Select(x => x.p)
            .ToArray();
        return ValueTask.FromResult<IReadOnlyList<ProviderDescriptor>>(hits);
    }

    private static int Score(ProviderDescriptor p, string q)
    {
        var s = 0;
        if (p.DisplayName?.Contains(q, StringComparison.OrdinalIgnoreCase) == true) s += 3;
        if (p.Description?.Contains(q, StringComparison.OrdinalIgnoreCase) == true) s += 1;
        if (p.Tags?.Any(t => t.Contains(q, StringComparison.OrdinalIgnoreCase)) == true) s += 2;
        if (p.Capabilities?.Any(c => c.Contains(q, StringComparison.OrdinalIgnoreCase)) == true) s += 2;
        return s;
    }
}

/// <summary>(3.3.0) AES-GCM-encrypted credential store. Host supplies the 32-byte key.</summary>
public sealed class AesGcmCredentialStore : ICredentialStore
{
    private readonly byte[] _key;
    private readonly ConcurrentDictionary<string, byte[]> _enc = new(StringComparer.Ordinal);

    public AesGcmCredentialStore(byte[] key32)
    {
        if (key32 is null || key32.Length != 32) throw new ArgumentException("key must be 32 bytes (AES-256-GCM)", nameof(key32));
        _key = key32;
    }

    public string BackendId => "aes-gcm";

    public ValueTask UpsertAsync(CredentialBundle bundle, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        var json = System.Text.Json.JsonSerializer.Serialize(bundle);
        var pt = Encoding.UTF8.GetBytes(json);

        var nonce = RandomNumberGenerator.GetBytes(12);
        var ctBuf = new byte[pt.Length];
        var tag   = new byte[16];
        using (var aes = new AesGcm(_key, 16)) { aes.Encrypt(nonce, pt, ctBuf, tag); }

        var combined = new byte[nonce.Length + tag.Length + ctBuf.Length];
        Buffer.BlockCopy(nonce, 0, combined, 0,                 nonce.Length);
        Buffer.BlockCopy(tag,   0, combined, nonce.Length,      tag.Length);
        Buffer.BlockCopy(ctBuf, 0, combined, nonce.Length + 16, ctBuf.Length);

        _enc[Key(bundle.ProviderId, bundle.UserId)] = combined;
        return ValueTask.CompletedTask;
    }

    public ValueTask<CredentialBundle?> GetAsync(string providerId, string userId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(providerId)) throw new ArgumentException("providerId required");
        if (string.IsNullOrWhiteSpace(userId))     throw new ArgumentException("userId required");
        if (!_enc.TryGetValue(Key(providerId, userId), out var combined)) return ValueTask.FromResult<CredentialBundle?>(null);

        var nonce = combined.AsSpan(0, 12).ToArray();
        var tag   = combined.AsSpan(12, 16).ToArray();
        var ctBuf = combined.AsSpan(28).ToArray();
        var pt    = new byte[ctBuf.Length];
        try
        {
            using (var aes = new AesGcm(_key, 16)) { aes.Decrypt(nonce, ctBuf, tag, pt); }
            var json = Encoding.UTF8.GetString(pt);
            var bundle = System.Text.Json.JsonSerializer.Deserialize<CredentialBundle>(json);
            return ValueTask.FromResult(bundle);
        }
        catch (CryptographicException) { return ValueTask.FromResult<CredentialBundle?>(null); }
    }

    public ValueTask DeleteAsync(string providerId, string userId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(providerId)) throw new ArgumentException("providerId required");
        if (string.IsNullOrWhiteSpace(userId))     throw new ArgumentException("userId required");
        _enc.TryRemove(Key(providerId, userId), out _);
        return ValueTask.CompletedTask;
    }

    private static string Key(string p, string u) => $"{p}/{u}";
}

/// <summary>(3.3.0) OAuth2 flow driver — builds authorise URL; token exchange delegated to host.</summary>
public sealed class OAuth2FlowDriver : IOAuth2FlowDriver
{
    private readonly IProviderCatalog _catalog;
    private readonly Func<string, string, string, string, CancellationToken, ValueTask<CredentialBundle>> _exchange;
    private readonly Func<string, string> _clientIdFor;

    public OAuth2FlowDriver(
        IProviderCatalog catalog,
        Func<string, string> clientIdFor,
        Func<string, string, string, string, CancellationToken, ValueTask<CredentialBundle>> exchange)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _clientIdFor = clientIdFor ?? throw new ArgumentNullException(nameof(clientIdFor));
        _exchange = exchange ?? throw new ArgumentNullException(nameof(exchange));
    }

    public string BackendId => "oauth2";

    public async ValueTask<string> StartAsync(string providerId, string userId, string redirectUri, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(providerId))  throw new ArgumentException("providerId required");
        if (string.IsNullOrWhiteSpace(userId))      throw new ArgumentException("userId required");
        if (string.IsNullOrWhiteSpace(redirectUri)) throw new ArgumentException("redirectUri required");

        var provider = await _catalog.GetProviderAsync(providerId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Unknown provider '{providerId}'.");
        if (provider.OAuth2 is null) throw new InvalidOperationException($"Provider '{providerId}' is not OAuth2.");

        var state = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var scopes = string.Join(" ", provider.OAuth2.Scopes);
        var clientId = _clientIdFor(providerId);
        var url =
            $"{provider.OAuth2.AuthorizeUrl}?response_type=code" +
            $"&client_id={WebUtility.UrlEncode(clientId)}" +
            $"&redirect_uri={WebUtility.UrlEncode(redirectUri)}" +
            $"&scope={WebUtility.UrlEncode(scopes)}" +
            $"&state={WebUtility.UrlEncode(state)}";
        return url;
    }

    public ValueTask<CredentialBundle> CompleteAsync(string providerId, string userId, string authorizationCode, string redirectUri, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(providerId)) throw new ArgumentException("providerId required");
        if (string.IsNullOrWhiteSpace(userId))     throw new ArgumentException("userId required");
        if (string.IsNullOrWhiteSpace(authorizationCode)) throw new ArgumentException("authorizationCode required");
        if (string.IsNullOrWhiteSpace(redirectUri))       throw new ArgumentException("redirectUri required");
        return _exchange(providerId, userId, authorizationCode, redirectUri, ct);
    }
}

/// <summary>(3.3.0) Sliding-window per-minute quota + max-concurrent semaphore.</summary>
public sealed class SlidingWindowQuotaGuard : IQuotaGuard
{
    private readonly ConcurrentDictionary<string, QuotaPolicy> _policies = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, List<DateTimeOffset>> _calls = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _inflight = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public string BackendId => "sliding-window";

    public ValueTask<bool> TryAcquireAsync(string providerId, string userId, CancellationToken ct = default)
    {
        var key = Key(providerId, userId);
        if (!_policies.TryGetValue(key, out var policy))
            return ValueTask.FromResult(true);  // no policy = unlimited
        var now = DateTimeOffset.UtcNow;
        lock (_lock)
        {
            // Per-minute cap.
            var list = _calls.GetOrAdd(key, _ => new List<DateTimeOffset>());
            list.RemoveAll(t => t < now.AddMinutes(-1));
            if (list.Count >= policy.PerMinuteCap) return ValueTask.FromResult(false);

            // Daily budget.
            if (list.Count(t => t >= now.AddDays(-1)) >= policy.DailyCallBudget) return ValueTask.FromResult(false);

            // Concurrency.
            var inflight = _inflight.GetOrAdd(key, 0);
            if (inflight >= policy.MaxConcurrent) return ValueTask.FromResult(false);

            list.Add(now);
            _inflight[key] = inflight + 1;
            return ValueTask.FromResult(true);
        }
    }

    public void Release(string providerId, string userId)
    {
        var key = Key(providerId, userId);
        lock (_lock)
        {
            if (_inflight.TryGetValue(key, out var n) && n > 0) _inflight[key] = n - 1;
        }
    }

    public ValueTask SetPolicyAsync(QuotaPolicy policy, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        _policies[Key(policy.ProviderId, policy.UserId)] = policy;
        return ValueTask.CompletedTask;
    }

    public ValueTask<QuotaPolicy?> GetPolicyAsync(string providerId, string userId, CancellationToken ct = default)
    {
        _policies.TryGetValue(Key(providerId, userId), out var p);
        return ValueTask.FromResult(p);
    }

    private static string Key(string p, string u) => $"{p}/{u}";
}

public sealed class InMemoryToolNamespaceStore : IToolNamespaceStore
{
    private readonly ConcurrentDictionary<string, ToolNamespace> _items = new(StringComparer.Ordinal);
    public string BackendId => "in-memory";

    public ValueTask UpsertAsync(ToolNamespace ns, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ns);
        if (string.IsNullOrWhiteSpace(ns.NamespaceId)) throw new ArgumentException("NamespaceId required");
        _items[ns.NamespaceId] = ns;
        return ValueTask.CompletedTask;
    }

    public ValueTask<ToolNamespace?> GetAsync(string namespaceId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(namespaceId)) throw new ArgumentException("namespaceId required", nameof(namespaceId));
        _items.TryGetValue(namespaceId, out var ns);
        return ValueTask.FromResult(ns);
    }

    public ValueTask<IReadOnlyList<ToolNamespace>> ListForUserAsync(string userId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId)) throw new ArgumentException("userId required", nameof(userId));
        return ValueTask.FromResult<IReadOnlyList<ToolNamespace>>(
            _items.Values.Where(n => n.OwnerUserId == userId).ToArray());
    }
}
