// XeroPrimitives.cs — (3.3.0)
//
// Xero integration primitives — token storage, tenant tracking,
// webhook recorder. HTTP plumbing is host-supplied.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace CircleAI.Commerce.Integration.Xero;

public sealed record XeroTokens(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAtUtc, string IdToken);
public sealed record XeroTenant(string TenantId, string TenantName, string TenantType);
public sealed record XeroWebhookEvent(string TenantId, string ResourceType, string ResourceId, DateTimeOffset AtUtc);

public interface IXeroBoard
{
    void StoreTokens(string userId, XeroTokens t);
    XeroTokens? GetTokens(string userId);
    bool TokensExpired(string userId, DateTimeOffset now);
    void AddTenant(string userId, XeroTenant t);
    IReadOnlyList<XeroTenant> TenantsFor(string userId);
    void RecordWebhook(XeroWebhookEvent e);
    IReadOnlyList<XeroWebhookEvent> RecentEvents(int limit = 20);
}

public sealed class InMemoryXeroBoard : IXeroBoard
{
    private readonly ConcurrentDictionary<string, XeroTokens> _tokens = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, List<XeroTenant>> _tenants = new(StringComparer.Ordinal);
    private readonly List<XeroWebhookEvent> _events = new();
    private readonly object _lock = new();

    public void StoreTokens(string userId, XeroTokens t) { ArgumentNullException.ThrowIfNull(t); _tokens[userId] = t; }
    public XeroTokens? GetTokens(string userId) => _tokens.GetValueOrDefault(userId);
    public bool TokensExpired(string userId, DateTimeOffset now)
    {
        if (!_tokens.TryGetValue(userId, out var t)) return true;
        return now >= t.ExpiresAtUtc;
    }
    public void AddTenant(string userId, XeroTenant t)
    {
        ArgumentNullException.ThrowIfNull(t);
        lock (_lock)
        {
            var list = _tenants.GetOrAdd(userId, _ => new List<XeroTenant>());
            if (!list.Any(x => x.TenantId == t.TenantId)) list.Add(t);
        }
    }
    public IReadOnlyList<XeroTenant> TenantsFor(string userId)
    { lock (_lock) return _tenants.TryGetValue(userId, out var l) ? l.ToArray() : Array.Empty<XeroTenant>(); }
    public void RecordWebhook(XeroWebhookEvent e) { ArgumentNullException.ThrowIfNull(e); lock (_lock) _events.Add(e); }
    public IReadOnlyList<XeroWebhookEvent> RecentEvents(int limit = 20)
    { lock (_lock) return _events.OrderByDescending(e => e.AtUtc).Take(limit).ToArray(); }
}
