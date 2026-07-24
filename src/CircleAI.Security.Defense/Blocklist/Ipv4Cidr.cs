// Ipv4Cidr.cs
//
// A compact IPv4 CIDR range using uint arithmetic — no allocations on the match
// path, no external dependency. IPv6 ranges are intentionally not modelled here;
// IPv6 indicators are matched exactly (normalised string) by the indicator source,
// which is sufficient for a bundled blocklist and keeps the hot path branch-light.

using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace CircleAI.Security.Defense;

/// <summary>
/// An immutable IPv4 CIDR block (network + mask) with an O(1) containment test.
/// </summary>
public readonly struct Ipv4Cidr : IEquatable<Ipv4Cidr>
{
    /// <summary>Network address (host bits zeroed), big-endian in a <see cref="uint"/>.</summary>
    public uint Network { get; }

    /// <summary>Subnet mask, big-endian in a <see cref="uint"/>.</summary>
    public uint Mask { get; }

    /// <summary>Prefix length in bits, 0–32.</summary>
    public int PrefixLength { get; }

    private Ipv4Cidr(uint network, uint mask, int prefixLength)
    {
        Network = network;
        Mask = mask;
        PrefixLength = prefixLength;
    }

    /// <summary>
    /// Parses "a.b.c.d/prefix" (or a bare "a.b.c.d", treated as /32). Returns
    /// <c>false</c> for anything that is not a valid IPv4 CIDR.
    /// </summary>
    public static bool TryParse(string? text, out Ipv4Cidr cidr)
    {
        cidr = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        int slash = text.IndexOf('/');
        string ipPart = slash < 0 ? text : text[..slash];
        int prefix = 32;

        if (slash >= 0)
        {
            string prefixPart = text[(slash + 1)..].Trim();
            if (!int.TryParse(prefixPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out prefix)
                || prefix < 0 || prefix > 32)
                return false;
        }

        if (!IPAddress.TryParse(ipPart.Trim(), out IPAddress? ip)
            || ip.AddressFamily != AddressFamily.InterNetwork)
            return false;

        uint addr = ToUInt32(ip);
        uint mask = prefix == 0 ? 0u : 0xFFFFFFFFu << (32 - prefix);
        cidr = new Ipv4Cidr(addr & mask, mask, prefix);
        return true;
    }

    /// <summary>Returns <c>true</c> when <paramref name="ip"/> is an IPv4 address inside this block.</summary>
    public bool Contains(IPAddress ip)
    {
        ArgumentNullException.ThrowIfNull(ip);
        if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
        return (ToUInt32(ip) & Mask) == Network;
    }

    /// <summary>Converts an IPv4 <see cref="IPAddress"/> to a big-endian <see cref="uint"/>.</summary>
    internal static uint ToUInt32(IPAddress ip)
    {
        Span<byte> bytes = stackalloc byte[4];
        if (ip.TryWriteBytes(bytes, out int written) && written == 4)
            return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];

        // Defensive fallback (should not happen for a validated IPv4 address).
        byte[] fallback = ip.GetAddressBytes();
        return ((uint)fallback[0] << 24) | ((uint)fallback[1] << 16) | ((uint)fallback[2] << 8) | fallback[3];
    }

    /// <inheritdoc/>
    public bool Equals(Ipv4Cidr other) =>
        Network == other.Network && Mask == other.Mask && PrefixLength == other.PrefixLength;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is Ipv4Cidr other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Network, Mask, PrefixLength);

    /// <inheritdoc/>
    public override string ToString()
    {
        uint n = Network;
        return $"{(n >> 24) & 0xFF}.{(n >> 16) & 0xFF}.{(n >> 8) & 0xFF}.{n & 0xFF}/{PrefixLength}";
    }
}
