// Circle33DocAnalyticsTests.cs

using System;
using System.Threading.Tasks;
using CircleAI.DocAnalytics;
using Xunit;

namespace CircleAI.Tests;

public class Circle33DocAnalyticsTests
{
    [Fact]
    public async Task Record_AndList_RoundTrips()
    {
        var t = new InMemoryDocumentTracker();
        await t.RecordViewAsync(new DocumentView("d1", "u1", DateTimeOffset.UtcNow, TimeSpan.FromSeconds(30), 3));
        await t.RecordViewAsync(new DocumentView("d1", "u2", DateTimeOffset.UtcNow, TimeSpan.FromSeconds(60), 5));

        var views = await t.ListViewsAsync("d1");
        Assert.Equal(2, views.Count);
    }

    [Fact]
    public async Task Compute_AggregatesViewsAndUniqueViewers()
    {
        var t = new InMemoryDocumentTracker();
        await t.RecordViewAsync(new DocumentView("d1", "u1", DateTimeOffset.UtcNow, TimeSpan.FromSeconds(20), 1));
        await t.RecordViewAsync(new DocumentView("d1", "u1", DateTimeOffset.UtcNow, TimeSpan.FromSeconds(40), 2));
        await t.RecordViewAsync(new DocumentView("d1", "u2", DateTimeOffset.UtcNow, TimeSpan.FromSeconds(60), 3));

        var insight = await t.ComputeAsync("d1");
        Assert.NotNull(insight);
        Assert.Equal(3, insight!.TotalViews);
        Assert.Equal(2, insight.UniqueViewers);
        Assert.Equal(40, insight.AvgDurationSeconds, 1);
    }

    [Fact]
    public async Task Compute_UnknownDocument_ReturnsNull()
    {
        var t = new InMemoryDocumentTracker();
        Assert.Null(await t.ComputeAsync("ghost"));
    }

    [Fact]
    public async Task ListViews_Unknown_ReturnsEmpty()
    {
        var t = new InMemoryDocumentTracker();
        var v = await t.ListViewsAsync("ghost");
        Assert.Empty(v);
    }

    [Fact]
    public async Task Record_NullView_Throws()
    {
        var t = new InMemoryDocumentTracker();
        await Assert.ThrowsAsync<ArgumentNullException>(() => t.RecordViewAsync(null!).AsTask());
    }

    [Fact]
    public async Task Record_EmptyDocumentId_Throws()
    {
        var t = new InMemoryDocumentTracker();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            t.RecordViewAsync(new DocumentView("", "u", DateTimeOffset.UtcNow, TimeSpan.Zero, 0)).AsTask());
    }

    [Fact]
    public void BackendId_IsInMemory()
    {
        var t = new InMemoryDocumentTracker();
        Assert.Equal("in-memory", t.BackendId);
    }
}
