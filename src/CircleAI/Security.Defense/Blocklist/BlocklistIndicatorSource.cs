// BlocklistIndicatorSource.cs
//
// The default IIndicatorSource: an in-memory index built from the bundled
// EmbeddedResource blocklist and refreshable from any TextReader. Thread-safety
// uses an immutable-snapshot swap — Match() reads a single volatile reference to a
// fully-built, never-mutated Index; RefreshFromAsync() builds a new Index and
// publishes it with one atomic reference assignment. No locks on the hot path.

using System.Net;
using System.Net.Sockets;
using CircleAI.Core.Validation;

namespace CircleAI.Security.Defense;

/// <summary>
/// In-memory known-bad indicator index sourced from the bundled offline blocklist
/// (and, optionally, runtime feed refreshes).
/// </summary>
[CircleAIVerificationStatus(VerificationLevel.WireProven,
    Notes = "Deterministic in-memory IOC index: exact IPv4 (uint) / IPv6 (normalised string) / " +
            "IPv4-CIDR containment / domain exact + parent-suffix match. Immutable-snapshot swap makes " +
            "Match lock-free and refresh atomic. Single-process; refresh is not persisted across restarts " +
            "and is not multi-replica coordinated (each device indexes its own copy).")]
public sealed class BlocklistIndicatorSource : IIndicatorSource
{
    private volatile IndexSnapshot _index = IndexSnapshot.Empty;

    /// <inheritdoc/>
    public int IndicatorCount => _index.Count;

    /// <inheritdoc/>
    public DateTimeOffset LastUpdated => _index.UpdatedAt;

    /// <summary>Creates an empty source. Call <see cref="LoadBundledAsync"/> to seed it.</summary>
    public BlocklistIndicatorSource()
    {
    }

    /// <summary>Creates a source pre-loaded from the bundled offline blocklist.</summary>
    public static async Task<BlocklistIndicatorSource> CreateFromBundledAsync(CancellationToken ct = default)
    {
        var source = new BlocklistIndicatorSource();
        await source.LoadBundledAsync(ct).ConfigureAwait(false);
        return source;
    }

    /// <summary>Loads (replacing) the index from the bundled EmbeddedResource blocklist.</summary>
    /// <returns>The number of indicators loaded.</returns>
    public async Task<int> LoadBundledAsync(CancellationToken ct = default)
    {
        await using Stream stream = OpenBundledBlocklist();
        using var reader = new StreamReader(stream);
        return await RefreshFromAsync(reader, replace: true, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public Task<int> RefreshFromAsync(TextReader reader, bool replace = true, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reader);

        IndexSnapshot current = _index;
        var ipv4 = replace ? new HashSet<uint>() : new HashSet<uint>(current.Ipv4);
        var cidrs = replace ? new List<Ipv4Cidr>() : new List<Ipv4Cidr>(current.Cidrs);
        var ipv6 = replace ? new HashSet<string>(StringComparer.Ordinal)
                           : new HashSet<string>(current.Ipv6, StringComparer.Ordinal);
        var domains = replace ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                              : new HashSet<string>(current.Domains, StringComparer.OrdinalIgnoreCase);

        int added = 0;
        foreach (ParsedIndicator indicator in BlocklistParser.Parse(reader))
        {
            ct.ThrowIfCancellationRequested();
            switch (indicator.Kind)
            {
                case IndicatorKind.Ipv4:
                    if (IPAddress.TryParse(indicator.Value, out IPAddress? v4) && ipv4.Add(Ipv4Cidr.ToUInt32(v4)))
                        added++;
                    break;

                case IndicatorKind.Ipv4Cidr:
                    if (Ipv4Cidr.TryParse(indicator.Value, out Ipv4Cidr cidr))
                    {
                        cidrs.Add(cidr);
                        added++;
                    }
                    break;

                case IndicatorKind.Ipv6:
                    if (ipv6.Add(indicator.Value)) added++;
                    break;

                case IndicatorKind.Domain:
                    if (domains.Add(indicator.Value)) added++;
                    break;
            }
        }

        // Atomic publish of the fully-built snapshot.
        _index = new IndexSnapshot
        {
            Ipv4 = ipv4,
            Cidrs = cidrs,
            Ipv6 = ipv6,
            Domains = domains,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        return Task.FromResult(added);
    }

    /// <inheritdoc/>
    public IndicatorMatch? Match(IPAddress? address, string? host)
    {
        IndexSnapshot index = _index; // single volatile read → stable snapshot for this call

        if (address is not null)
        {
            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                uint value = Ipv4Cidr.ToUInt32(address);
                if (index.Ipv4.Contains(value))
                    return new IndicatorMatch(address.ToString(), IndicatorKind.Ipv4, "known-bad-ip");

                // CIDR list is small (bundled ranges); linear scan is fine and allocation-free.
                foreach (Ipv4Cidr cidr in index.Cidrs)
                {
                    if (cidr.Contains(address))
                        return new IndicatorMatch(cidr.ToString(), IndicatorKind.Ipv4Cidr, "known-bad-range");
                }
            }
            else if (address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                string canonical = address.ToString();
                if (index.Ipv6.Contains(canonical))
                    return new IndicatorMatch(canonical, IndicatorKind.Ipv6, "known-bad-ip");
            }
        }

        if (!string.IsNullOrWhiteSpace(host))
        {
            string h = host.Trim().TrimEnd('.').ToLowerInvariant();
            if (index.Domains.Contains(h))
                return new IndicatorMatch(h, IndicatorKind.Domain, "known-bad-domain");

            // Parent-domain suffix match: a.b.evil.com matches indicator evil.com.
            int dot = h.IndexOf('.');
            while (dot >= 0 && dot < h.Length - 1)
            {
                string parent = h[(dot + 1)..];
                if (index.Domains.Contains(parent))
                    return new IndicatorMatch(parent, IndicatorKind.Domain, "known-bad-parent-domain");
                dot = h.IndexOf('.', dot + 1);
            }
        }

        return null;
    }

    private static Stream OpenBundledBlocklist()
    {
        var assembly = typeof(BlocklistIndicatorSource).Assembly;
        string name = Array.Find(
                          assembly.GetManifestResourceNames(),
                          n => n.EndsWith("defense-blocklist.txt", StringComparison.OrdinalIgnoreCase))
                      ?? throw new InvalidOperationException(
                          "Bundled 'defense-blocklist.txt' embedded resource was not found. " +
                          "Ensure Data/defense-blocklist.txt is included as an <EmbeddedResource>.");

        return assembly.GetManifestResourceStream(name)
               ?? throw new InvalidOperationException($"Embedded resource '{name}' could not be opened.");
    }

    // Immutable index snapshot. Built once, published atomically, never mutated after publish.
    private sealed class IndexSnapshot
    {
        public required HashSet<uint> Ipv4 { get; init; }
        public required List<Ipv4Cidr> Cidrs { get; init; }
        public required HashSet<string> Ipv6 { get; init; }
        public required HashSet<string> Domains { get; init; }
        public required DateTimeOffset UpdatedAt { get; init; }

        public int Count => Ipv4.Count + Cidrs.Count + Ipv6.Count + Domains.Count;

        public static IndexSnapshot Empty => new()
        {
            Ipv4 = new HashSet<uint>(),
            Cidrs = new List<Ipv4Cidr>(),
            Ipv6 = new HashSet<string>(StringComparer.Ordinal),
            Domains = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            UpdatedAt = DateTimeOffset.MinValue,
        };
    }
}
