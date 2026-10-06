// IIndicatorSource.cs
//
// The known-bad indicator index the monitor consults. Kept separate from the
// monitor so the indicator store can be swapped (bundled blocklist today; a
// mesh-delivered feed, or a signed catalogue, tomorrow) without touching detection.

using System.Net;

namespace CircleAI.Security.Defense;

/// <summary>The result of matching an observation against the indicator index.</summary>
/// <param name="Indicator">The matched indicator (address, CIDR, or domain).</param>
/// <param name="Kind">Which kind of indicator matched.</param>
/// <param name="Reason">Short machine-readable reason tag (e.g. "known-bad-ip").</param>
public readonly record struct IndicatorMatch(string Indicator, IndicatorKind Kind, string Reason);

/// <summary>
/// A queryable set of known-bad network indicators. Implementations must be safe
/// to call concurrently from the monitor hot path while a refresh runs.
/// </summary>
public interface IIndicatorSource
{
    /// <summary>Total indicators currently indexed.</summary>
    int IndicatorCount { get; }

    /// <summary>When the index was last (re)built. <see cref="DateTimeOffset.MinValue"/> if never.</summary>
    DateTimeOffset LastUpdated { get; }

    /// <summary>
    /// Returns the first matching indicator for the given address and/or host, or
    /// <c>null</c> if neither is known-bad. Address is matched exactly (IPv4/IPv6)
    /// and by IPv4 CIDR; host is matched exactly and by parent-domain suffix.
    /// </summary>
    IndicatorMatch? Match(IPAddress? address, string? host);

    /// <summary>
    /// Rebuilds (or extends) the index from <paramref name="reader"/>. Works fully
    /// offline; a host may point this at any CC0/MIT feed it manages to fetch.
    /// The swap is atomic — concurrent <see cref="Match"/> calls never see a
    /// half-built index.
    /// </summary>
    /// <param name="reader">Indicator text in any format <see cref="BlocklistParser"/> accepts.</param>
    /// <param name="replace"><c>true</c> to replace the current set; <c>false</c> to merge into it.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of new indicators added.</returns>
    Task<int> RefreshFromAsync(TextReader reader, bool replace = true, CancellationToken ct = default);
}
