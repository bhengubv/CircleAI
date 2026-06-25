// Circle33LocalDevTunnelTests.cs
//
// (3.3.0) Tests for local-dev tunnel resolvers.

using System;
using System.Threading.Tasks;
using CircleAI.Telephony;
using Xunit;

namespace CircleAI.Tests;

public class Circle33LocalDevTunnelTests
{
    [Fact]
    public async Task Null_NotAvailable_Throws()
    {
        var t = NullLocalDevTunnel.Instance;
        Assert.False(t.IsAvailable);
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await t.GetPublicUrlAsync(8080));
    }

    [Fact]
    public async Task Static_ReturnsConfiguredUrl()
    {
        var t = new StaticLocalDevTunnel(new Uri("https://example.com/webhook"));
        var url = await t.GetPublicUrlAsync(8080);
        Assert.Equal("https://example.com/webhook", url.ToString());
    }

    [Fact]
    public void Static_RelativeUri_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new StaticLocalDevTunnel(new Uri("/relative", UriKind.Relative)));
    }

    [Fact]
    public async Task Cloudflare_DelegatesToResolver()
    {
        var t = new CloudflareTunnel((port, ct) =>
            ValueTask.FromResult(new Uri($"https://cf-{port}.tunnel.example.com")));
        var url = await t.GetPublicUrlAsync(5000);
        Assert.Equal("https://cf-5000.tunnel.example.com/", url.ToString());
        Assert.Equal("cloudflare", t.ProviderId);
    }

    [Fact]
    public async Task Ngrok_DelegatesToResolver()
    {
        var t = new NgrokTunnel((port, ct) =>
            ValueTask.FromResult(new Uri($"https://abc.ngrok.app")));
        var url = await t.GetPublicUrlAsync(3000);
        Assert.Equal("https://abc.ngrok.app/", url.ToString());
        Assert.Equal("ngrok", t.ProviderId);
    }

    [Fact]
    public void Cloudflare_NullResolver_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new CloudflareTunnel(null!));
    }

    [Fact]
    public void Ngrok_NullResolver_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new NgrokTunnel(null!));
    }

    [Fact]
    public void All_ProviderIds_AreUnique()
    {
        Assert.Equal("null",        NullLocalDevTunnel.Instance.ProviderId);
        Assert.Equal("static",      new StaticLocalDevTunnel(new Uri("https://x")).ProviderId);
        Assert.Equal("cloudflare",  new CloudflareTunnel((_, _) => default).ProviderId);
        Assert.Equal("ngrok",       new NgrokTunnel((_, _) => default).ProviderId);
    }
}
