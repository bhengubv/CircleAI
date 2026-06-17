// MeshCapabilityRegistryTests.cs
//
// (RT-12 v1) Tests for the in-memory mesh capability registry.

using System;
using System.Linq;
using System.Threading.Tasks;
using CircleAI.AetherNet;
using CircleAI.Core;
using Xunit;

namespace CircleAI.Tests;

public sealed class MeshCapabilityRegistryTests
{
    private static MeshCapabilityAdvertisement Ad(
        string peerId, string modelId, int freeKv,
        DeviceTier tier = DeviceTier.Phone,
        DateTimeOffset? at = null)
        => new(
            PeerId:              peerId,
            ModelId:             modelId,
            FreeKvTokens:        freeKv,
            Tier:                tier,
            ContextWindowTokens: 2048,
            AdvertisedAtUtc:     at ?? DateTimeOffset.UtcNow);

    [Fact]
    public async Task Upsert_ListReturnsLatest()
    {
        var reg = new InMemoryMeshCapabilityRegistry();
        await reg.UpsertAsync(Ad("p1", "Qwen3-1.7B-MNN", 1024));
        await reg.UpsertAsync(Ad("p2", "Qwen3-0.6B-MNN", 256));

        var all = reg.List();
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public async Task Upsert_SamePeerReplaces()
    {
        var reg = new InMemoryMeshCapabilityRegistry();
        await reg.UpsertAsync(Ad("p1", "Qwen3-1.7B-MNN", 1024));
        await reg.UpsertAsync(Ad("p1", "Qwen3-4B-MNN", 512));

        var found = reg.Find("Qwen3-4B-MNN");
        Assert.Single(found);
        Assert.Equal(512, found[0].FreeKvTokens);
    }

    [Fact]
    public async Task Find_SortsByFreeKvDescending()
    {
        var reg = new InMemoryMeshCapabilityRegistry();
        await reg.UpsertAsync(Ad("low",    "Qwen3-1.7B-MNN", 100));
        await reg.UpsertAsync(Ad("medium", "Qwen3-1.7B-MNN", 500));
        await reg.UpsertAsync(Ad("high",   "Qwen3-1.7B-MNN", 2000));

        var found = reg.Find("Qwen3-1.7B-MNN");
        Assert.Equal(3, found.Count);
        Assert.Equal("high", found[0].PeerId);
        Assert.Equal("medium", found[1].PeerId);
        Assert.Equal("low", found[2].PeerId);
    }

    [Fact]
    public async Task Find_FiltersByMinFreeKv()
    {
        var reg = new InMemoryMeshCapabilityRegistry();
        await reg.UpsertAsync(Ad("low",  "Qwen3-1.7B-MNN", 100));
        await reg.UpsertAsync(Ad("high", "Qwen3-1.7B-MNN", 2000));

        var found = reg.Find("Qwen3-1.7B-MNN", minFreeKvTokens: 1000);
        Assert.Single(found);
        Assert.Equal("high", found[0].PeerId);
    }

    [Fact]
    public async Task Find_WrongModel_ReturnsEmpty()
    {
        var reg = new InMemoryMeshCapabilityRegistry();
        await reg.UpsertAsync(Ad("p1", "Qwen3-1.7B-MNN", 1024));
        Assert.Empty(reg.Find("Qwen3-14B-MNN"));
    }

    [Fact]
    public async Task List_StaleAfter_FiltersOldEntries()
    {
        var now = DateTimeOffset.UtcNow;
        var reg = new InMemoryMeshCapabilityRegistry
        {
            NowUtc = () => now,
        };
        await reg.UpsertAsync(Ad("fresh", "Qwen3-1.7B-MNN", 500, at: now - TimeSpan.FromSeconds(10)));
        await reg.UpsertAsync(Ad("stale", "Qwen3-1.7B-MNN", 500, at: now - TimeSpan.FromMinutes(5)));

        var live = reg.List(staleAfter: TimeSpan.FromMinutes(1));
        Assert.Single(live);
        Assert.Equal("fresh", live[0].PeerId);
    }

    [Fact]
    public async Task Remove_ReturnsTrueOnce()
    {
        var reg = new InMemoryMeshCapabilityRegistry();
        await reg.UpsertAsync(Ad("p1", "Qwen3-1.7B-MNN", 1024));
        Assert.True(await reg.RemoveAsync("p1"));
        Assert.False(await reg.RemoveAsync("p1"));
    }

    [Fact]
    public async Task NullBroadcaster_NoOps()
    {
        await NullMeshCapabilityBroadcaster.Instance.BroadcastAsync(
            Ad("p1", "Qwen3-1.7B-MNN", 1024));
        // No throw; nothing to assert.
        Assert.True(true);
    }
}
