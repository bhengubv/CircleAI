// NetworkObservation.cs
//
// The seam a host/transport feeds into the monitor. One observation = one thing
// the device is about to do on the network (dial an IP, resolve a name, accept an
// inbound connection). The producer is platform-specific and OUT OF SCOPE here:
//   * Android  — a VpnService capturing connection metadata (no packet payloads),
//                or ConnectivityManager / DNS-resolver hooks.
//   * AetherNet — connection-establishment events from the mesh transport.
//   * Desktop  — ETW / pcap connection events.
// The monitor never needs payloads — only endpoint metadata — so this stays
// privacy-preserving and de-Googled.

using System.Net;

namespace CircleAI.Security.Defense;

/// <summary>
/// Direction of an observed network event.
/// </summary>
public enum ThreatDirection
{
    /// <summary>Direction not known.</summary>
    Unknown = 0,

    /// <summary>Device is initiating a connection to a remote endpoint.</summary>
    Outbound = 1,

    /// <summary>A remote endpoint is connecting to the device.</summary>
    Inbound = 2,

    /// <summary>A name-resolution (DNS) lookup.</summary>
    Lookup = 3,
}

/// <summary>
/// One network event to evaluate. Endpoint metadata only — never payload.
/// </summary>
/// <param name="Host">DNS name involved, if known (SNI / lookup / connect-by-name).</param>
/// <param name="RemoteAddress">Remote IP, if known (dialed / resolved address).</param>
/// <param name="RemotePort">Remote port, or 0 when not applicable.</param>
/// <param name="Direction">Direction of the event.</param>
/// <param name="Protocol">Wire protocol hint, e.g. "tcp", "udp", "dns", "tls-sni", "http".</param>
/// <param name="AppHint">Originating app/package identifier, if the host can supply it.</param>
/// <param name="ObservedAt">UTC timestamp of the observation.</param>
public sealed record NetworkObservation(
    string? Host,
    IPAddress? RemoteAddress,
    int RemotePort,
    ThreatDirection Direction,
    string Protocol,
    string? AppHint,
    DateTimeOffset ObservedAt)
{
    /// <summary>Convenience factory for an outbound connection observation.</summary>
    public static NetworkObservation Outbound(
        IPAddress address, int port, string protocol = "tcp",
        string? host = null, string? appHint = null) =>
        new(host, address, port, ThreatDirection.Outbound, protocol, appHint, DateTimeOffset.UtcNow);

    /// <summary>Convenience factory for a DNS lookup observation.</summary>
    public static NetworkObservation Dns(string host, string? appHint = null) =>
        new(host, null, 0, ThreatDirection.Lookup, "dns", appHint, DateTimeOffset.UtcNow);
}

/// <summary>
/// Implemented by a host/transport to stream network observations into the
/// always-on defence posture. The posture calls <see cref="ObserveAsync"/> once
/// and pumps every yielded observation through the monitor until cancelled.
/// </summary>
public interface INetworkObservationFeed
{
    /// <summary>Human-readable id for this feed (e.g. "android-vpn", "aethernet").</summary>
    string SourceId { get; }

    /// <summary>
    /// Yields observations until <paramref name="ct"/> is cancelled. Implementations
    /// should be resilient — a transient error should not terminate the stream.
    /// </summary>
    IAsyncEnumerable<NetworkObservation> ObserveAsync(CancellationToken ct);
}
