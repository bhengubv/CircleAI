// ConnectivityHonestyTests.cs
//
// The probe is not allowed to report "offline" when it simply could not look.
//
// WHAT THESE PIN. On 2026-10-08, on the P30, CircleAI's own service process was
// refused a socket ioctl by SELinux:
//
//   avc: denied { ioctl } for pid=14525 comm=".NET TP Worker"
//        ioctlcmd=0x8946 tclass=udp_socket permissive=0
//
// 0x8946 is SIOCGIFCONF — "list the network interfaces", which is what
// NetworkInterface.GetIsNetworkAvailable() needs. It does not throw when
// refused; it returns FALSE. DeviceProbe turned that into
// Connectivity.Offline, which was also the enum's ZERO value, so a phone with
// working Wi-Fi reported offline and nothing could tell that apart from a real
// measurement. Connectivity gates downloads, so a model fetch then declined to
// start for a reason that read as careful behaviour.
//
// These tests are about the SHAPE of the answer, not about networking: they
// never touch a real interface. Each one fails if a future change collapses
// "could not tell" back into "offline".

using CircleAI.Core;
using Xunit;

namespace CircleAI.Tests;

[Collection("ConnectivityProbe")]
public class ConnectivityHonestyTests : IDisposable
{
    private readonly Func<Connectivity>? _saved = DeviceProbe.PlatformConnectivityProbe;

    public void Dispose() => DeviceProbe.PlatformConnectivityProbe = _saved;

    [Fact]
    public void Unknown_is_not_the_default_value()
    {
        // Offline = 0 is the whole reason this was invisible: every uninitialised
        // or failed read landed on it and looked like an answer. If someone
        // renumbers the enum so Unknown becomes 0 the bug returns wearing a new
        // name, so pin the numbers rather than the names.
        Assert.Equal(0, (int)Connectivity.Offline);
        Assert.NotEqual(0, (int)Connectivity.Unknown);

        // The original three keep their values: these cross a link and are
        // persisted in the catalogue.
        Assert.Equal(1, (int)Connectivity.Mesh);
        Assert.Equal(2, (int)Connectivity.Online);
    }

    [Fact]
    public void A_platform_probe_that_throws_reports_Unknown_not_Offline()
    {
        DeviceProbe.PlatformConnectivityProbe = () => throw new InvalidOperationException("denied");

        Assert.Equal(Connectivity.Unknown, DeviceProbe.ReadConnectivity());
    }

    [Theory]
    [InlineData(Connectivity.Online)]
    [InlineData(Connectivity.Mesh)]
    [InlineData(Connectivity.Offline)]
    [InlineData(Connectivity.Unknown)]
    public void The_platform_probe_is_believed_when_a_head_installs_one(Connectivity stated)
    {
        // A head that CAN read the truth (Android's ConnectivityManager) must win
        // over Core's interface enumeration, including when it says Offline:
        // "Android reports no active network" is a real measurement, unlike a
        // refused ioctl.
        DeviceProbe.PlatformConnectivityProbe = () => stated;

        Assert.Equal(stated, DeviceProbe.ReadConnectivity());
    }

    [Fact]
    public void NetworkType_has_a_word_for_not_knowing()
    {
        DeviceProbe.PlatformConnectivityProbe = () => Connectivity.Unknown;

        // "none" was what this said when it could not tell, and "none" is what a
        // screen renders as "no connection". The string must differ.
        Assert.Equal("unknown", new DefaultDeviceContext().NetworkType);
    }

    [Theory]
    [InlineData(Connectivity.Online,  "online")]
    [InlineData(Connectivity.Mesh,    "mesh")]
    [InlineData(Connectivity.Offline, "none")]
    public void NetworkType_reads_the_same_source_as_the_probe(Connectivity state, string expected)
    {
        // One fact, one owner. DefaultDeviceContext used to call
        // GetIsNetworkAvailable() itself, which is how one device ended up with
        // two different answers about whether it was online.
        DeviceProbe.PlatformConnectivityProbe = () => state;

        Assert.Equal(expected, new DefaultDeviceContext().NetworkType);
    }

    [Fact]
    public void A_snapshot_carries_the_platform_answer()
    {
        DeviceProbe.PlatformConnectivityProbe = () => Connectivity.Mesh;

        Assert.Equal(Connectivity.Mesh, DeviceProbe.Snapshot().Connectivity);
    }

    [Fact]
    public void Nothing_reports_Offline_without_being_told_so()
    {
        // The guard on the whole class of bug: with no platform probe installed,
        // Core may answer Online (a real interface), or Unknown (it could not
        // tell), or Offline (enumeration worked and found nothing, which only
        // happens off Android). What it must never do is claim Offline on
        // Android, where a negative is indistinguishable from a refusal.
        DeviceProbe.PlatformConnectivityProbe = null;

        var state = DeviceProbe.ReadConnectivity();

        if (OperatingSystem.IsAndroid())
            Assert.NotEqual(Connectivity.Offline, state);
        else
            Assert.Contains(state, new[] { Connectivity.Online, Connectivity.Offline, Connectivity.Unknown });
    }
}
