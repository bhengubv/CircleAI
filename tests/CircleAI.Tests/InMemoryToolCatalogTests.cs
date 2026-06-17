// InMemoryToolCatalogTests.cs
//
// (2.0.3) Tests for InMemoryToolCatalog + IToolProvider + extension.

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Hosting.Tools;
using Xunit;

namespace CircleAI.Tests;

public sealed class InMemoryToolCatalogTests
{
    private static ToolDescriptor Sample(string name, string provider = "local",
        string desc = "demo", params string[] tags) =>
        new(Name: name, Description: desc, Provider: provider, Tags: tags);

    [Fact]
    public async Task Upsert_AddsAndReplaces()
    {
        var c = new InMemoryToolCatalog();
        await c.UpsertAsync(Sample("a"));
        await c.UpsertAsync(Sample("a", desc: "updated"));
        Assert.Equal(1, c.Count);
        var t = await c.GetAsync("a");
        Assert.Equal("updated", t!.Description);
    }

    [Fact]
    public async Task Remove_IsIdempotent()
    {
        var c = new InMemoryToolCatalog();
        await c.UpsertAsync(Sample("a"));
        Assert.True(await c.RemoveAsync("a"));
        Assert.False(await c.RemoveAsync("a"));
    }

    [Fact]
    public async Task List_SortsByNameCaseInsensitive()
    {
        var c = new InMemoryToolCatalog();
        await c.UpsertAsync(Sample("Beta"));
        await c.UpsertAsync(Sample("alpha"));
        await c.UpsertAsync(Sample("Gamma"));

        var all = c.List();
        Assert.Equal(new[] { "alpha", "Beta", "Gamma" }, all.Select(t => t.Name).ToArray());
    }

    [Fact]
    public async Task ListByProvider_FiltersExactProvider()
    {
        var c = new InMemoryToolCatalog();
        await c.UpsertAsync(Sample("local-one",  provider: "local"));
        await c.UpsertAsync(Sample("gmail-send", provider: "gmail"));
        await c.UpsertAsync(Sample("local-two",  provider: "local"));

        var locals = c.ListByProvider("local");
        Assert.Equal(2, locals.Count);
        Assert.All(locals, d => Assert.Equal("local", d.Provider));
    }

    [Fact]
    public async Task Search_RanksNameMatchesHigher()
    {
        var c = new InMemoryToolCatalog();
        await c.UpsertAsync(Sample("send_email", desc: "ship a message"));
        await c.UpsertAsync(Sample("ship_package", desc: "send a shipment"));
        await c.UpsertAsync(Sample("noop"));

        var hits = c.Search("send", topK: 5);
        Assert.NotEmpty(hits);
        // send_email has "send" in name (+5) and desc has "ship"(0). ship_package has "send" only in desc (+2).
        Assert.Equal("send_email", hits[0].Name);
    }

    [Fact]
    public async Task Search_RespectsTopKAndReturnsEmptyOnNoMatch()
    {
        var c = new InMemoryToolCatalog();
        for (int i = 0; i < 25; i++)
            await c.UpsertAsync(Sample($"thing-{i}", desc: "matchme"));

        Assert.Equal(5, c.Search("matchme", topK: 5).Count);
        Assert.Empty(c.Search("nothingmatches"));
    }

    private sealed class FakeProvider : IToolProvider
    {
        public string ProviderId => "fake";
        public ValueTask<bool> IsAvailableAsync(CancellationToken ct = default)
            => ValueTask.FromResult(true);
        public ValueTask<IReadOnlyList<ToolDescriptor>> DiscoverAsync(CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<ToolDescriptor>>(new[]
            {
                new ToolDescriptor("fake.one", "first",  "fake"),
                new ToolDescriptor("fake.two", "second", "fake"),
            });
    }

    [Fact]
    public async Task ImportFromAsync_ReturnsCount()
    {
        var c = new InMemoryToolCatalog();
        var n = await c.ImportFromAsync(new FakeProvider());
        Assert.Equal(2, n);
        Assert.Equal(2, c.Count);
        Assert.NotNull(await c.GetAsync("fake.one"));
    }
}
