// ModelDownloadGateTests.cs
//
// AIOptions.WifiOnlyModelDownload was declared, defaulted to true, and
// documented as "only downloads over Wi-Fi / Ethernet ... to protect mobile
// data" — while being read by absolutely nothing. The smallest catalogued
// bundle is 433 MB, so on a South African prepaid bundle the gap between the
// documentation and the behaviour was real money.
//
// These tests exist so the property cannot quietly become inert again.

using CircleAI.Core;
using CircleAI.Inference;
using Xunit;

namespace CircleAI.Tests;

public sealed class ModelDownloadGateTests
{
    /// <summary>A device context that reports exactly one NetworkType.</summary>
    private sealed class NetContext : IDeviceContext
    {
        public NetContext(string? networkType) => NetworkType = networkType;
        public string? NetworkType { get; }

        public string? Locale => null;
        public string? TimeZoneId => null;
        public System.DateTimeOffset? LocalTime => null;
        public double? Latitude => null;
        public double? Longitude => null;
        public string? LocationHint => null;
        public float? BatteryLevel => null;
        public bool? IsCharging => null;
        public string? ActiveAppId => null;
        public float? CpuUsagePercent => null;
        public long? AvailableMemoryBytes => null;
        public ThermalState? ThermalState => null;
        public long? StorageFreeBytes => null;
        public System.DateTimeOffset? LastActiveUtc => null;
    }

    private const long Bundle433Mb = 433L * 1024 * 1024;

    [Theory]
    [InlineData("cellular")]
    [InlineData("mobile")]
    [InlineData("metered")]
    [InlineData("CELLULAR")]   // normalisation
    [InlineData(" Cellular ")] // trimming
    public void MeteredConnection_IsBlocked(string networkType)
    {
        var gate = new MeteredNetworkDownloadGate(new NetContext(networkType), wifiOnly: true);

        var reason = gate.BlockReason(Bundle433Mb);

        Assert.NotNull(reason);
        Assert.Contains("mobile data", reason!, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("433", reason);   // tells the user the actual cost
        Assert.True(gate.IsEnforceable);
    }

    [Theory]
    [InlineData("wifi")]
    [InlineData("ethernet")]
    [InlineData("unmetered")]
    public void UnmeteredConnection_IsAllowed(string networkType)
    {
        var gate = new MeteredNetworkDownloadGate(new NetContext(networkType), wifiOnly: true);

        Assert.Null(gate.BlockReason(Bundle433Mb));
        Assert.True(gate.IsEnforceable);
    }

    [Fact]
    public void WifiOnlyOff_AllowsEvenOnMobileData()
    {
        // The user explicitly opted in; the gate must not second-guess them.
        var gate = new MeteredNetworkDownloadGate(new NetContext("cellular"), wifiOnly: false);

        Assert.Null(gate.BlockReason(Bundle433Mb));
        Assert.True(gate.IsEnforceable);
    }

    [Theory]
    [InlineData("online")]   // what DefaultDeviceContext actually returns
    [InlineData(null)]
    public void WhenTheHostCannotTell_ItAllowsButAdmitsItCannotEnforce(string? networkType)
    {
        // The honest case. DefaultDeviceContext answers "online" or "none" and
        // cannot distinguish metered links. Failing CLOSED would break every
        // desktop host; failing open SILENTLY would recreate the original bug.
        // So: allow, and report that the guarantee does not hold.
        var gate = new MeteredNetworkDownloadGate(new NetContext(networkType), wifiOnly: true);

        Assert.Null(gate.BlockReason(Bundle433Mb));
        Assert.False(gate.IsEnforceable);
    }

    [Fact]
    public void NoNetwork_IsBlockedWithAPlainReason()
    {
        var gate = new MeteredNetworkDownloadGate(new NetContext("none"), wifiOnly: true);

        var reason = gate.BlockReason(Bundle433Mb);

        Assert.NotNull(reason);
        Assert.Contains("network", reason!, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NullDeviceContext_DoesNotThrow()
    {
        // Hosts may register no IDeviceContext at all.
        var gate = new MeteredNetworkDownloadGate(device: null, wifiOnly: true);

        Assert.Null(gate.BlockReason(Bundle433Mb));
        Assert.False(gate.IsEnforceable);
    }
}
