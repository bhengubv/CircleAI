// SsdpClient.cs — (3.5.0) SSDP M-SEARCH discovery over UDP multicast. Pure BCL: send
// the search datagram to 239.255.255.250:1900 and collect unicast responses within a
// window. Returns each responder's LOCATION + ST + USN. This is the open, de-Googled
// equivalent of "find a screen" — standard UPnP, no vendor SDK.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Cast.Dlna;

/// <summary>One SSDP responder — a device advertising itself on the LAN.</summary>
public readonly record struct SsdpResponse(Uri Location, string SearchTarget, string UniqueServiceName);

/// <summary>Issues SSDP M-SEARCH and yields responders as they answer.</summary>
public static class SsdpClient
{
    private static readonly IPAddress MulticastV4 = IPAddress.Parse("239.255.255.250");
    private const int SsdpPort = 1900;

    /// <summary>Search target for AV renderers — what smart TVs advertise for control.</summary>
    public const string MediaRendererTarget = "urn:schemas-upnp-org:device:MediaRenderer:1";

    /// <summary>
    /// Multicast an M-SEARCH for <paramref name="searchTarget"/> and stream every
    /// unicast 200-OK response until <paramref name="window"/> elapses or cancelled.
    /// </summary>
    public static async IAsyncEnumerable<SsdpResponse> SearchAsync(
        string searchTarget,
        TimeSpan window,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(searchTarget);

        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 2);

        int mx = Math.Clamp((int)window.TotalSeconds, 1, 5);
        string request =
            "M-SEARCH * HTTP/1.1\r\n" +
            "HOST: 239.255.255.250:1900\r\n" +
            "MAN: \"ssdp:discover\"\r\n" +
            "MX: " + mx.ToString(CultureInfo.InvariantCulture) + "\r\n" +
            "ST: " + searchTarget + "\r\n" +
            "\r\n";
        byte[] datagram = Encoding.ASCII.GetBytes(request);
        var target = new IPEndPoint(MulticastV4, SsdpPort);

        // UDP is lossy; send the query twice.
        bool sent = true;
        try
        {
            await udp.SendAsync(datagram, target, ct).ConfigureAwait(false);
            await udp.SendAsync(datagram, target, ct).ConfigureAwait(false);
        }
        catch (SocketException) { sent = false; }
        if (!sent) yield break;

        using var windowCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        windowCts.CancelAfter(window);

        while (!windowCts.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await udp.ReceiveAsync(windowCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { break; }

            var parsed = ParseResponse(result.Buffer);
            if (parsed is not null) yield return parsed.Value;
        }
    }

    private static SsdpResponse? ParseResponse(byte[] buffer)
    {
        string text = Encoding.ASCII.GetString(buffer);
        if (!text.StartsWith("HTTP/1.1", StringComparison.OrdinalIgnoreCase)) return null;

        string? location = null, st = null, usn = null;
        foreach (var raw in text.Split("\r\n"))
        {
            int c = raw.IndexOf(':');
            if (c <= 0) continue;
            var key = raw.AsSpan(0, c).Trim();
            var val = raw[(c + 1)..].Trim();

            if (key.Equals("LOCATION", StringComparison.OrdinalIgnoreCase)) location = val;
            else if (key.Equals("ST", StringComparison.OrdinalIgnoreCase)) st = val;
            else if (key.Equals("USN", StringComparison.OrdinalIgnoreCase)) usn = val;
        }

        if (string.IsNullOrEmpty(location) || !Uri.TryCreate(location, UriKind.Absolute, out var loc))
            return null;
        return new SsdpResponse(loc, st ?? string.Empty, usn ?? string.Empty);
    }
}
