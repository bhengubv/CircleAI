// Circle25ContractTests.cs
//
// (2.5.0) Contract surface tests for Tools.Catalog, Inputs, Spatial.

using System;
using System.Threading.Tasks;
using CircleAI.Inputs;
using CircleAI.Spatial;
using CircleAI.Tools.Catalog;
using Xunit;

namespace CircleAI.Tests;

public sealed class Circle25ContractTests
{
    // ── Tools.Catalog ────────────────────────────────────────────────

    [Fact]
    public async Task NullProviderCatalog_AllReturnEmpty()
    {
        Assert.Empty(await NullProviderCatalog.Instance.ListProvidersAsync());
        Assert.Null(await NullProviderCatalog.Instance.GetProviderAsync("x"));
        Assert.Empty(await NullProviderCatalog.Instance.SearchProvidersAsync("x"));
    }

    [Fact]
    public async Task NullCredentialStore_RoundtripsNothing()
    {
        var b = new CredentialBundle("p", "u", new Dictionary<string, string> { ["k"] = "v" });
        await NullCredentialStore.Instance.UpsertAsync(b);
        Assert.Null(await NullCredentialStore.Instance.GetAsync("p", "u"));
    }

    [Fact]
    public async Task NullOAuth2_StartReturnsBlank_CompleteThrows()
    {
        Assert.Equal("about:blank", await NullOAuth2FlowDriver.Instance.StartAsync("p", "u", "https://x"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NullOAuth2FlowDriver.Instance.CompleteAsync("p", "u", "code", "https://x").AsTask());
    }

    [Fact]
    public async Task NullQuotaGuard_DeniesByDefault()
        => Assert.False(await NullQuotaGuard.Instance.TryAcquireAsync("p", "u"));

    [Fact]
    public async Task NullToolNamespaceStore_AllReturnEmpty()
    {
        await NullToolNamespaceStore.Instance.UpsertAsync(new ToolNamespace("ns1", "u1", new[] { "p" }));
        Assert.Null(await NullToolNamespaceStore.Instance.GetAsync("ns1"));
        Assert.Empty(await NullToolNamespaceStore.Instance.ListForUserAsync("u1"));
    }

    // ── Inputs ───────────────────────────────────────────────────────

    [Fact]
    public async Task NullWebScraper_ReturnsEmptyText()
    {
        var u = new Uri("https://example.com");
        var p = await NullWebScraper.Instance.FetchAsync(u);
        Assert.Equal("", p.Text);
        Assert.Equal(u, p.Url);
    }

    [Fact]
    public async Task NullVideoIngest_ReturnsZero()
    {
        var r = await NullVideoIngest.Instance.IngestAsync("nope.mp4");
        Assert.Empty(r.Transcript);
        Assert.Equal(TimeSpan.Zero, r.Duration);
    }

    [Fact]
    public async Task NullTerminalCast_ReturnsEmpty()
    {
        var c = await NullTerminalCast.Instance.LoadAsync("x.cast");
        Assert.Empty(c.Segments);
        Assert.Equal("", await NullTerminalCast.Instance.RenderTranscriptAsync(c));
    }

    // ── Spatial ──────────────────────────────────────────────────────

    [Fact]
    public async Task NullGeoTileSource_ReturnsEmptyTile()
    {
        var t = await NullGeoTileSource.Instance.GetTileAsync(0, 0, 0);
        Assert.True(t.ImageBytes.IsEmpty);
        Assert.Empty(await NullGeoTileSource.Instance.SearchPlacesAsync("here"));
    }

    [Fact]
    public async Task NullRadarReadout_ReturnsEmptyReading()
    {
        var r = await NullRadarReadout.Instance.GetCurrentReadingAsync(new LatLon(0, 0));
        Assert.Empty(r.Returns);
    }

    [Fact]
    public async Task NullSkyTracker_ReturnsEmpty()
        => Assert.Empty(await NullSkyTracker.Instance.VisibleAsync(new LatLon(0, 0), DateTimeOffset.MinValue));

    [Fact]
    public async Task Null3DSceneRenderer_ReturnsEmptyScene()
    {
        var s = await Null3DSceneRenderer.Instance.RenderAsync("noop");
        Assert.True(s.Encoded.IsEmpty);
        Assert.Equal("gltf", s.Format);
    }
}
