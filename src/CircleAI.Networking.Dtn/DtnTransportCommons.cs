// DtnTransportCommons.cs
//
// (3.3.0) Delay-tolerant-networking primitives that complement the
// existing DtnBundle.cs in this package: priority enum + custody-record
// + in-memory bundle store. The bundle type itself lives in DtnBundle.cs.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace CircleAI.Networking.Dtn;

public enum DtnPriority { Bulk, Normal, Expedited }

public sealed record DtnCustodyRecord(string BundleId, string CustodianNode, DateTimeOffset AcceptedAtUtc);

public sealed class InMemoryDtnBundleStore
{
    private readonly ConcurrentDictionary<string, DtnBundle> _bundles = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DtnCustodyRecord> _custody = new(StringComparer.Ordinal);

    public void Store(DtnBundle b) { ArgumentNullException.ThrowIfNull(b); _bundles[b.BundleId] = b; }
    public DtnBundle? Get(string bundleId) => _bundles.GetValueOrDefault(bundleId);
    public IReadOnlyList<DtnBundle> All => _bundles.Values.ToArray();
    public void AcceptCustody(DtnCustodyRecord r) { ArgumentNullException.ThrowIfNull(r); _custody[r.BundleId] = r; }
    public DtnCustodyRecord? GetCustody(string bundleId) => _custody.GetValueOrDefault(bundleId);

    public bool IsExpired(string bundleId, DateTimeOffset now)
    {
        if (!_bundles.TryGetValue(bundleId, out var b)) return true;
        return now > b.ExpiresAt;
    }

    public int Purge(DateTimeOffset now)
    {
        var dead = _bundles.Where(kv => now > kv.Value.ExpiresAt).Select(kv => kv.Key).ToArray();
        foreach (var id in dead) { _bundles.TryRemove(id, out _); _custody.TryRemove(id, out _); }
        return dead.Length;
    }

    public IReadOnlyList<DtnBundle> InFlightTo(string destinationNodeId)
        => _bundles.Values.Where(b => b.DestinationNodeId == destinationNodeId).ToArray();
}
