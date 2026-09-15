// MeshOffloadGatewayTests.cs
//
// The phone's borrow policy: it borrows from the hand-picked node only when
// consent is on AND this phone cannot serve chat itself, sends the raw question,
// and returns null (fall back to local) on anything else.

using System;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Assistant;
using CircleAI.Mesh;
using CircleAI.Mesh.Hosting;
using Xunit;

namespace CircleAI.Tests;

public class MeshOffloadGatewayTests
{
    private sealed class FakeClient : IMeshOffloadClient
    {
        public bool IsReady => true;
        public string? SawPeer;
        public OffloadTurn? SawTurn;
        public Func<OffloadTurn, OffloadResult> OnRequest = _ =>
            new OffloadResult(true, "peer answer", OffloadServedBy.RemotePeer, "B", 3, 5, null);

        public Task<OffloadResult> RequestAsync(string peerId, OffloadTurn turn, TimeSpan timeout, CancellationToken ct = default)
        {
            SawPeer = peerId;
            SawTurn = turn;
            return Task.FromResult(OnRequest(turn));
        }
    }

    private static readonly LocalChatStatus CannotServe = new(CanServe: false, IntendedModelId: null);

    private static MeshOffloadGateway Gateway(
        FakeClient client, LocalChatStatus local, OffloadConsent consent, MeshOffloadGatewayOptions? opts = null)
        => new(client, () => local, () => consent, opts);

    [Fact]
    public async Task Borrows_from_the_chosen_node_when_local_cannot_serve()
    {
        var client = new FakeClient();

        var answer = await Gateway(client, CannotServe, new OffloadConsent(true, "B")).TryBorrowAsync("capital of Japan?");

        Assert.Equal("peer answer", answer);
        Assert.Equal("B", client.SawPeer);
        Assert.Equal("capital of Japan?", client.SawTurn!.Prompt);   // RAW question sent
    }

    [Fact]
    public async Task Off_does_not_borrow()
    {
        var client = new FakeClient();
        Assert.Null(await Gateway(client, CannotServe, OffloadConsent.Off).TryBorrowAsync("hi"));
        Assert.Null(client.SawPeer);
    }

    [Fact]
    public async Task On_but_no_node_does_not_borrow()
    {
        var client = new FakeClient();
        Assert.Null(await Gateway(client, CannotServe, new OffloadConsent(true, null)).TryBorrowAsync("hi"));
        Assert.Null(client.SawPeer);
    }

    [Fact]
    public async Task Does_not_borrow_when_this_phone_can_serve()
    {
        var client = new FakeClient();
        var canServe = new LocalChatStatus(CanServe: true, IntendedModelId: "Qwen3-0.6B-MNN");
        Assert.Null(await Gateway(client, canServe, new OffloadConsent(true, "B")).TryBorrowAsync("hi"));
        Assert.Null(client.SawPeer);   // no borrow — local runs it
    }

    [Fact]
    public async Task A_failed_borrow_returns_null_so_the_turn_falls_back_local()
    {
        var client = new FakeClient { OnRequest = _ => OffloadResult.Fail("peer at capacity", OffloadServedBy.RemotePeer) };
        Assert.Null(await Gateway(client, CannotServe, new OffloadConsent(true, "B")).TryBorrowAsync("hi"));
        Assert.Equal("B", client.SawPeer);   // it did try
    }

    [Fact]
    public async Task The_intended_model_rides_along_when_known_else_the_fallback_marker()
    {
        var a = new FakeClient();
        await Gateway(a, new LocalChatStatus(false, "Qwen3-4B-MNN"), new OffloadConsent(true, "B")).TryBorrowAsync("q");
        Assert.Equal("Qwen3-4B-MNN", a.SawTurn!.ModelId);

        var b = new FakeClient();
        await Gateway(b, new LocalChatStatus(false, null), new OffloadConsent(true, "B"),
            new MeshOffloadGatewayOptions { FallbackModelId = "chat" }).TryBorrowAsync("q");
        Assert.Equal("chat", b.SawTurn!.ModelId);
    }
}
