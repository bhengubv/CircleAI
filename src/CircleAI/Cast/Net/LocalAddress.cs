// LocalAddress.cs — (3.5.0) Find the local IPv4 address a renderer can actually reach
// us on. A media host bound to 127.0.0.1 is useless to a TV; we need the egress LAN
// interface address. Pure BCL, no name resolution (offline/LAN-safe).

using System;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace CircleAI.Cast.Net;

/// <summary>Resolves the local IPv4 address usable for LAN-facing media serving.</summary>
public static class LocalAddress
{
    /// <summary>
    /// The egress interface IPv4 toward <paramref name="target"/>. Uses a connected
    /// (but silent — no datagram is sent) UDP socket to consult the OS routing table,
    /// then falls back to the first private NIC address, then loopback.
    /// </summary>
    public static IPAddress ForRoute(IPAddress target)
    {
        ArgumentNullException.ThrowIfNull(target);
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.Connect(target, 65530); // UDP "connect" sets the route; sends nothing
            if (s.LocalEndPoint is IPEndPoint ep && !IPAddress.IsLoopback(ep.Address))
                return ep.Address;
        }
        catch (SocketException) { /* fall through to NIC enumeration */ }

        return FirstPrivateV4() ?? IPAddress.Loopback;
    }

    /// <summary>First up, non-loopback, private (RFC 1918) IPv4 across all interfaces, or null.</summary>
    /// <remarks>
    /// ENUMERATION IS THE PART ANDROID MAY REFUSE. A socket ioctl of 0x8946
    /// (SIOCGIFCONF — "list the interfaces") was observed denied by SELinux to this
    /// app's own process on the P30. So this is guarded, and on failure falls back
    /// to <see cref="RouteDerivedV4"/>, which asks the routing table instead of the
    /// interface list and needs no such permission.
    ///
    /// The fallback matters because the caller of last resort is
    /// <c>IPAddress.Loopback</c>, and handing a renderer 127.0.0.1 as "the address
    /// to stream from" is not a degraded answer, it is a wrong one that cannot work.
    /// </remarks>
    public static IPAddress? FirstPrivateV4()
    {
        try
        {
            var enumerated = EnumeratePrivateV4();
            if (enumerated is not null) return enumerated;
        }
        catch
        {
            // Denied or unsupported. Fall through — never report "no address".
        }

        return RouteDerivedV4();
    }

    /// <summary>
    /// The local address the OS would use to reach the internet, derived from the
    /// routing table rather than the interface list.
    /// </summary>
    /// <remarks>
    /// A UDP socket's <c>Connect</c> sends nothing — it only fixes the route — so
    /// this reveals the outbound source address with no traffic, no permission and
    /// no reachability requirement. 192.0.2.1 is TEST-NET-1 (RFC 5737): reserved,
    /// never routed to a real host, and chosen so this cannot accidentally touch
    /// somebody's machine.
    /// </remarks>
    private static IPAddress? RouteDerivedV4()
    {
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.Connect(new IPAddress(new byte[] { 192, 0, 2, 1 }), 65530);
            if (s.LocalEndPoint is IPEndPoint ep
                && !IPAddress.IsLoopback(ep.Address)
                && ep.Address.AddressFamily == AddressFamily.InterNetwork)
            {
                return ep.Address;
            }
        }
        catch (SocketException) { /* genuinely no route */ }
        return null;
    }

    private static IPAddress? EnumeratePrivateV4()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                var a = ua.Address;
                if (a.AddressFamily != AddressFamily.InterNetwork) continue;
                if (IPAddress.IsLoopback(a)) continue;

                var b = a.GetAddressBytes();
                if (b[0] == 169 && b[1] == 254) continue; // APIPA / link-local
                if (IsPrivate(b)) return a;
            }
        }
        return null;
    }

    private static bool IsPrivate(byte[] b) =>
        b[0] == 10 ||
        (b[0] == 172 && b[1] >= 16 && b[1] <= 31) ||
        (b[0] == 192 && b[1] == 168);
}
