// AndroidConnectivity.cs
//
// Telling Core whether this phone is actually online.
//
// WHAT WENT WRONG. DeviceProbe read link state from
// NetworkInterface.GetIsNetworkAvailable(). On Linux and Android that is
// implemented by enumerating network interfaces through the SIOCGIFCONF ioctl,
// and Android's SELinux policy refuses that ioctl to an untrusted app. Measured
// on the P30 (Android 10) on 2026-10-08, in CircleAI's own service process:
//
//   avc: denied { ioctl } for pid=14525 comm=".NET TP Worker"
//        path="socket:[53100927]" ioctlcmd=0x8946 tclass=udp_socket permissive=0
//
// 0x8946 is SIOCGIFCONF. The call does not throw. It returns FALSE. So every
// phone CircleAI has ever run on has reported Connectivity.Offline while sitting
// on working Wi-Fi, and because Offline was also the enum's zero value there was
// nothing to distinguish "measured offline" from "never managed to look".
//
// WHAT THAT COST. Connectivity gates downloads. A model fetch that declines to
// start because the device is "offline" looks like correct, careful behaviour -
// it is the same code path as a genuinely offline phone - so the failure is
// invisible from the outside and unfalsifiable from the inside. This is the
// identical shape to a Pixel being told it was not a phone: the app asked
// ITSELF instead of asking the platform.
//
// THE FIX IS TO ASK ANDROID. ConnectivityManager is the supported source and
// needs no interface enumeration. NetworkCapabilities also answers the question
// that actually matters - VALIDATED, meaning traffic has been proven to reach
// the internet, not merely that an interface is associated. A captive portal is
// associated and not online, and only NET_CAPABILITY_VALIDATED can tell them
// apart.

using Android.Content;
using Android.Net;
using CircleAI.Core;

// Aliased for the same reason as AndroidDeviceMemory: this file shares one
// assembly with the MAUI-targeting android leg from 3.8.1, and MAUI's implicit
// usings bring Microsoft.Maui.Controls (and its own Application) into scope.
using Application = Android.App.Application;

namespace CircleAI.Device;

/// <summary>Reads this device's real link state from Android, and tells Core.</summary>
public static class AndroidConnectivity
{
    /// <summary>
    /// Installs the platform connectivity hook. Call once, as early as a head can
    /// — beside <see cref="AndroidDeviceMemory.Install"/>.
    /// </summary>
    /// <param name="context">Any context; the application context is used internally.</param>
    /// <remarks>
    /// Safe to call more than once and from any thread. Anything it cannot read
    /// becomes <see cref="Connectivity.Unknown"/> rather than
    /// <see cref="Connectivity.Offline"/>, because "I could not tell" and "there
    /// is no network" are different facts and only one of them should stop a
    /// download.
    /// </remarks>
    public static void Install(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var app = context.ApplicationContext ?? context;

        DeviceProbe.PlatformConnectivityProbe = () => Read(app);
    }

    /// <summary>What Android says about the active network right now.</summary>
    internal static Connectivity Read(Context app)
    {
        try
        {
            if (app.GetSystemService(Context.ConnectivityService) is not ConnectivityManager cm)
                return Connectivity.Unknown;

            var active = cm.ActiveNetwork;
            if (active is null)
            {
                // No active network is a real, readable answer from the platform
                // — unlike a denied ioctl — so this one IS offline.
                return Connectivity.Offline;
            }

            var caps = cm.GetNetworkCapabilities(active);
            if (caps is null) return Connectivity.Unknown;

            // VALIDATED means Android has proven traffic reaches the internet.
            // INTERNET alone is only a claim of intent: a captive portal and a
            // Wi-Fi Direct group both have it and neither can fetch a model.
            var internet  = caps.HasCapability(NetCapability.Internet);
            var validated = caps.HasCapability(NetCapability.Validated);

            if (internet && validated) return Connectivity.Online;

            // Associated but not validated. A peer-to-peer or Wi-Fi Direct
            // transport is exactly this, and it is Mesh rather than Offline -
            // reachable for AetherNet, not reachable for a download.
            if (caps.HasTransport(TransportType.WifiAware) ||
                caps.HasTransport(TransportType.Bluetooth))
            {
                return Connectivity.Mesh;
            }

            // Has a network, cannot confirm the internet. Not offline, not online.
            return internet ? Connectivity.Unknown : Connectivity.Mesh;
        }
        catch
        {
            // An OEM quirk or a policy denial. Never a basis for claiming offline:
            // that mistake is the whole reason this file exists.
            return Connectivity.Unknown;
        }
    }

    /// <summary>
    /// A one-line health check a head can log: what the probe reports, and whether
    /// anything installed it at all.
    /// </summary>
    /// <remarks>
    /// Exists because the failure it describes is silent. A head that never called
    /// <see cref="Install"/> is indistinguishable from one that did, right up until
    /// a download refuses to start on a phone that is plainly online. Reports a
    /// STATE, not a boolean, so "Unknown" cannot be mistaken for "Offline".
    /// </remarks>
    public static string Describe()
    {
        if (DeviceProbe.PlatformConnectivityProbe is null)
        {
            return "connectivity: NO PLATFORM PROBE INSTALLED - link state comes from "
                 + "interface enumeration, which Android's SELinux policy denies "
                 + "(SIOCGIFCONF). Call AndroidConnectivity.Install at startup.";
        }

        var state = DeviceProbe.Snapshot().Connectivity;
        return state switch
        {
            Connectivity.Online  => "connectivity: online (validated by Android)",
            Connectivity.Mesh    => "connectivity: mesh/peer transport only - no validated internet",
            Connectivity.Offline => "connectivity: offline - Android reports no active network",
            _                    => "connectivity: unknown - the platform would not say. Treat as "
                                  + "'try it', never as offline.",
        };
    }
}
