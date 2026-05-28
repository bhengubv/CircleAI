// NullBiosignalSourceTests.cs

using CircleAI.Wearable.Biosignals;
using Xunit;

namespace CircleAI.Wearable.Biosignals.Tests;

public sealed class NullBiosignalSourceTests
{
    [Fact]
    public async Task StreamAsync_CompletesImmediately_WithNoItems()
    {
        var src = new NullBiosignalSource();
        var items = new List<BiosignalSample>();

        await foreach (var s in src.StreamAsync(CancellationToken.None))
        {
            items.Add(s);
        }

        Assert.Empty(items);
    }

    [Fact]
    public async Task IsSupportedAsync_ReturnsFalse_ForAllKinds()
    {
        var src = new NullBiosignalSource();

        foreach (var kind in Enum.GetValues<BiosignalKind>())
        {
            Assert.False(await src.IsSupportedAsync(kind, CancellationToken.None));
        }
    }

    [Fact]
    public void SupportedKinds_IsEmpty()
    {
        var src = new NullBiosignalSource();

        Assert.Empty(src.SupportedKinds);
    }
}
