// Circle33VisualizationTests.cs

using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CircleAI.Visualization;
using Xunit;

namespace CircleAI.Tests;

public class Circle33VisualizationTests
{
    [Fact]
    public async Task DashboardStore_UpsertGetList_RoundTrips()
    {
        var s = new InMemoryDashboardStore();
        await s.UpsertAsync(new DashboardDefinition("d1", "Sales", "{}"));
        await s.UpsertAsync(new DashboardDefinition("d2", "Ops",   "{}"));

        Assert.Equal("Sales", (await s.GetAsync("d1"))!.Title);
        Assert.Equal(2, (await s.ListAsync()).Count);
    }

    [Fact]
    public async Task DashboardStore_GetUnknown_ReturnsNull()
    {
        var s = new InMemoryDashboardStore();
        Assert.Null(await s.GetAsync("ghost"));
    }

    [Fact]
    public async Task DashboardStore_Upsert_NullThrows()
    {
        var s = new InMemoryDashboardStore();
        await Assert.ThrowsAsync<ArgumentNullException>(() => s.UpsertAsync(null!).AsTask());
    }

    [Fact]
    public async Task ApiDocBuilder_ExtractsTitle()
    {
        var b = new JsonApiDocBuilder();
        var d = await b.BuildAsync("""{"info":{"title":"Pet Store API","version":"1.0"},"paths":{}}""");
        Assert.Equal("Pet Store API", d.Title);
        Assert.Equal("pet-store-api", d.DocId);
    }

    [Fact]
    public async Task ApiDocBuilder_NoTitle_DefaultsToApi()
    {
        var b = new JsonApiDocBuilder();
        var d = await b.BuildAsync("""{"paths":{}}""");
        Assert.Equal("API", d.Title);
    }

    [Fact]
    public async Task SiteBuilder_ProducesFilesFromPages()
    {
        var b = new StaticSiteBuilder();
        var site = await b.BuildAsync("""
        {"pages":[{"path":"index.html","html":"<h1>Home</h1>"},{"path":"about.html","html":"<p>About</p>"}]}
        """);
        Assert.Equal(2, site.Files.Count);
        Assert.Equal("<h1>Home</h1>", Encoding.UTF8.GetString(site.Files["index.html"].Span));
    }

    [Fact]
    public async Task SiteBuilder_MissingPages_Throws()
    {
        var b = new StaticSiteBuilder();
        await Assert.ThrowsAsync<ArgumentException>(() => b.BuildAsync("""{}""").AsTask());
    }

    [Fact]
    public async Task SiteBuilder_PagesWithoutPath_AreSkipped()
    {
        var b = new StaticSiteBuilder();
        var site = await b.BuildAsync("""
        {"pages":[{"html":"orphan"},{"path":"x.html","html":"ok"}]}
        """);
        Assert.Single(site.Files);
        Assert.True(site.Files.ContainsKey("x.html"));
    }
}
