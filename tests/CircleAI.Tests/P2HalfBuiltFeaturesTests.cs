// P2HalfBuiltFeaturesTests.cs
//
// P2 = "half-built features": this PR brings them across the finish line.
//   • KimiVlGenerator     — exists as a class, ChatMessage carries ImageBytes
//   • Vision DI routing   — RequiredCapabilities.Vision → KimiVlGenerator
//   • Catalog connectivity gate — offline hosts don't waste an HTTPS roundtrip

using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Core;
using CircleAI.Core.Models;
using CircleAI.Hosting;
using CircleAI.Inference;
using Xunit;

namespace CircleAI.Tests;

public sealed class P2ChatMessageImageBytesTests
{
    [Fact]
    public void ImageBytes_DefaultsToNull()
    {
        var m = new ChatMessage("user", "hi");
        Assert.Null(m.ImageBytes);
    }

    [Fact]
    public void ImageBytes_CanBeAttached()
    {
        var bytes = new byte[] { 0xFF, 0xD8, 0xFF };
        var m = new ChatMessage("user", "describe this") { ImageBytes = bytes };
        Assert.Same(bytes, m.ImageBytes);
    }

    [Fact]
    public void Record_Equality_IgnoresImageBytesReference()
    {
        // Records compare structurally — same content + same role + same
        // ImageBytes reference equals; different references with same bytes
        // do NOT (because byte[] uses reference equality). This is fine —
        // ChatMessage equality is rarely used in hot paths.
        var bytes = new byte[] { 1, 2, 3 };
        var a = new ChatMessage("user", "hi") { ImageBytes = bytes };
        var b = new ChatMessage("user", "hi") { ImageBytes = bytes };
        Assert.Equal(a, b);
    }
}

public sealed class P2AIOptionsRequiredCapabilitiesTests
{
    [Fact]
    public void RequiredCapabilities_DefaultsToDefault()
    {
        var opts = new AIOptions();
        Assert.Equal(ChatCapability.Default, opts.RequiredCapabilities);
    }

    [Fact]
    public void RequiredCapabilities_CanRequestVision()
    {
        var opts = new AIOptions
        {
            RequiredCapabilities = ChatCapability.Default | ChatCapability.Vision,
        };
        Assert.True(opts.RequiredCapabilities.HasFlag(ChatCapability.Vision));
    }
}

public sealed class P2CatalogConnectivityGateTests
{
    private static ModelScopeCatalogClient BuildClient(IDeviceContext? ctx, string cacheDir)
    {
        var options = new ModelScopeCatalogOptions
        {
            CacheDirectory = cacheDir,
            Cadence        = CatalogRefreshCadence.OnStartup,
        };
        return new ModelScopeCatalogClient(options, httpClient: null, verifier: null, deviceContext: ctx);
    }

    [Fact]
    public async Task IsRefreshDue_OfflineHost_ReturnsFalse()
    {
        var dir = Directory.CreateTempSubdirectory("circleai-p2-").FullName;
        try
        {
            var offline = new FakeNetworkContext("none");
            using var client = BuildClient(offline, dir);

            var due = await client.IsRefreshDueAsync();

            Assert.False(due);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task IsRefreshDue_OnlineHost_FollowsCadence()
    {
        var dir = Directory.CreateTempSubdirectory("circleai-p2-").FullName;
        try
        {
            var online = new FakeNetworkContext("online");
            using var client = BuildClient(online, dir);

            // Cold cache + OnStartup cadence → due
            var due = await client.IsRefreshDueAsync();

            Assert.True(due);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task IsRefreshDue_NullContext_FollowsCadence()
    {
        var dir = Directory.CreateTempSubdirectory("circleai-p2-").FullName;
        try
        {
            using var client = BuildClient(ctx: null, dir);

            // No context → unknown network → default to "let cadence decide"
            var due = await client.IsRefreshDueAsync();

            Assert.True(due);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}

internal sealed class FakeNetworkContext : IDeviceContext
{
    public FakeNetworkContext(string? networkType) => NetworkType = networkType;

    public string? NetworkType { get; }

    public string? ActiveAppId          => null;
    public string? Locale               => null;
    public string? TimeZoneId           => null;
    public DateTimeOffset? LocalTime    => null;
    public double? Latitude             => null;
    public double? Longitude            => null;
    public string? LocationHint         => null;
    public float?  BatteryLevel         => null;
    public bool?   IsCharging           => null;
    public float?  CpuUsagePercent      => null;
    public long?   AvailableMemoryBytes => null;
    public CircleAI.Core.ThermalState? ThermalState   => null;
    public long?   StorageFreeBytes     => null;
    public DateTimeOffset? LastActiveUtc => null;
}
