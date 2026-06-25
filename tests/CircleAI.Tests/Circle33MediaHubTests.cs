// Circle33MediaHubTests.cs

using System;
using System.Threading.Tasks;
using CircleAI.MediaHub;
using Xunit;

namespace CircleAI.Tests;

public class Circle33MediaHubTests
{
    [Fact]
    public async Task Library_AddGet_RoundTrips()
    {
        var lib = new InMemoryMediaLibrary();
        lib.Add(new MediaItem("m1", "Hello World", "video", TimeSpan.FromMinutes(5), "video/mp4"));
        var got = await lib.GetAsync("m1");
        Assert.Equal("Hello World", got!.Title);
    }

    [Fact]
    public async Task Library_Search_MatchesSubstring()
    {
        var lib = new InMemoryMediaLibrary();
        lib.Add(new MediaItem("m1", "Hello World", "video", TimeSpan.Zero, "video/mp4"));
        lib.Add(new MediaItem("m2", "Goodbye Cruel World", "video", TimeSpan.Zero, "video/mp4"));
        lib.Add(new MediaItem("m3", "Unrelated", "audio", TimeSpan.Zero, "audio/mp3"));

        var hits = await lib.SearchAsync("world");
        Assert.Equal(2, hits.Count);
    }

    [Fact]
    public async Task Library_SearchTopK_Bounds()
    {
        var lib = new InMemoryMediaLibrary();
        for (int i = 0; i < 5; i++) lib.Add(new MediaItem($"m{i}", $"alpha {i}", "audio", TimeSpan.Zero, "audio/mp3"));
        var hits = await lib.SearchAsync("alpha", topK: 3);
        Assert.Equal(3, hits.Count);
    }

    [Fact]
    public async Task Library_GetUnknown_Null()
    {
        var lib = new InMemoryMediaLibrary();
        Assert.Null(await lib.GetAsync("ghost"));
    }

    [Fact]
    public async Task SyncedPlayback_BroadcastDeliversToSubscribers()
    {
        var p = new InMemorySyncedPlayback();
        await p.JoinSessionAsync("s1", "u1");
        await p.JoinSessionAsync("s1", "u2");

        var received = 0;
        using var sub = p.Subscribe("s1", _ => { received++; return ValueTask.CompletedTask; });

        await p.BroadcastPositionAsync("s1", new PlaybackPosition("m1", TimeSpan.FromSeconds(10), DateTimeOffset.UtcNow));
        await p.BroadcastPositionAsync("s1", new PlaybackPosition("m1", TimeSpan.FromSeconds(20), DateTimeOffset.UtcNow));

        Assert.Equal(2, received);
    }

    [Fact]
    public async Task SyncedPlayback_DisposeSubscription_StopsDelivery()
    {
        var p = new InMemorySyncedPlayback();
        var got = 0;
        var sub = p.Subscribe("s1", _ => { got++; return ValueTask.CompletedTask; });
        await p.BroadcastPositionAsync("s1", new PlaybackPosition("m1", TimeSpan.Zero, DateTimeOffset.UtcNow));
        sub.Dispose();
        await p.BroadcastPositionAsync("s1", new PlaybackPosition("m1", TimeSpan.FromSeconds(1), DateTimeOffset.UtcNow));

        Assert.Equal(1, got);
    }

    [Fact]
    public async Task SyncedPlayback_BroadcastToUnknownSession_DoesNothing()
    {
        var p = new InMemorySyncedPlayback();
        await p.BroadcastPositionAsync("ghost", new PlaybackPosition("m", TimeSpan.Zero, DateTimeOffset.UtcNow));
    }
}
