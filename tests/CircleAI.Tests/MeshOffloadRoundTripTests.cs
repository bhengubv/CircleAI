// MeshOffloadRoundTripTests.cs
//
// The whole offload loop, end to end, with no device: a borrower asks, the
// request crosses an in-memory transport, the serving node runs it through the
// REAL BridgeLocalInferenceFallback over a stub brain, and the answer comes back
// correlated. This is the proof that the wired engine — MeshOffloadClient (both
// roles), MeshOffloadRouter, and the Phase-1 adapter — actually completes a turn
// across the wire. The transport here is a loopback; the AetherNet one is the
// device piece.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using CircleAI.AetherNet;
using CircleAI.Core;
using CircleAI.Hosting.InferenceBridge;
using CircleAI.Mesh;
using CircleAI.Mesh.Hosting;
using CircleAI.Networking;
using Microsoft.Extensions.Options;
using Xunit;

namespace CircleAI.Tests;

public class MeshOffloadRoundTripTests
{
    // ── An in-memory mesh: payloads are ferried opaquely between endpoints,
    //    routed by DestinationId (broadcast when unaddressed). ──────────────
    private sealed class Bus
    {
        private readonly List<Endpoint> _eps = new();

        public Endpoint Connect(string nodeId)
        {
            var e = new Endpoint(this, nodeId);
            lock (_eps) _eps.Add(e);
            return e;
        }

        internal void Publish(Endpoint from, NetworkPayload p)
        {
            lock (_eps)
            {
                foreach (var e in _eps)
                {
                    if (ReferenceEquals(e, from)) continue;
                    if (p.DestinationId is { Length: > 0 } dest
                        && !string.Equals(e.NodeId, dest, StringComparison.Ordinal)) continue;
                    e.Inbox.Writer.TryWrite(p);
                }
            }
        }
    }

    private sealed class Endpoint : INetworkTransport
    {
        private readonly Bus _bus;
        public string NodeId { get; }
        public Channel<NetworkPayload> Inbox { get; } = Channel.CreateUnbounded<NetworkPayload>();

        public Endpoint(Bus bus, string nodeId) { _bus = bus; NodeId = nodeId; }

        public TransportKind Kind => TransportKind.Aether;   // Kind is diagnostic-only here
        public bool IsAvailable => true;
        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct = default) { Inbox.Writer.TryComplete(); return Task.CompletedTask; }
        public Task SendAsync(NetworkPayload payload, CancellationToken ct = default) { _bus.Publish(this, payload); return Task.CompletedTask; }
        public IAsyncEnumerable<NetworkPayload> ReceiveAsync([EnumeratorCancellation] CancellationToken ct = default)
            => Inbox.Reader.ReadAllAsync(ct);
    }

    private sealed class StubBridge : IInferenceBridge
    {
        private readonly string _answer;
        public StubBridge(string answer) => _answer = answer;
        public Task<InferenceResponse> CompleteAsync(InferenceRequest r, CancellationToken ct = default)
            => Task.FromResult(new InferenceResponse(
                r.Id, r.ModelId, _answer, 3, 5, InferenceStatus.Completed, 7, null, DateTimeOffset.UtcNow));
        public IAsyncEnumerable<string> StreamCompletionAsync(InferenceRequest r, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> IsModelLoadedAsync(string id, CancellationToken ct = default) => Task.FromResult(true);
        public Task<IReadOnlyList<ModelDescriptor>> ListLoadedModelsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ModelDescriptor>>([]);
        public Task<DeviceCapabilities> GetDeviceCapabilitiesAsync(CancellationToken ct = default) => throw new NotSupportedException();
    }

    private static MeshOffloadClient Client(
        INetworkTransport transport, ILocalInferenceFallback fallback, string nodeId, bool serve)
    {
        var opts = Options.Create(new MeshOffloadOptions
        {
            LocalNodeId = nodeId,
            ServeInboundRequests = serve,
            StartTransport = false,   // the loopback endpoint is always available
        });
        return new MeshOffloadClient(transport, new InMemoryMeshCapabilityRegistry(), fallback, opts);
    }

    [Fact]
    public async Task A_borrower_gets_an_answer_served_by_a_remote_peer()
    {
        var bus = new Bus();
        var a = bus.Connect("A");
        var b = bus.Connect("B");

        await using var server = Client(b, new BridgeLocalInferenceFallback(new StubBridge("Tokyo.")), "B", serve: true);
        await using var borrower = Client(a, NullLocalInferenceFallback.Instance, "A", serve: false);
        await server.StartAsync(default);
        await borrower.StartAsync(default);

        var turn = OffloadTurn.Create("Qwen3-0.6B-MNN", "capital of Japan?");
        var res = await ((IMeshOffloadClient)borrower).RequestAsync("B", turn, TimeSpan.FromSeconds(5));

        Assert.True(res.Success);
        Assert.Equal("Tokyo.", res.OutputText);
        Assert.Equal(OffloadServedBy.RemotePeer, res.ServedBy);
        Assert.Equal("B", res.ServingPeerId);
    }

    [Fact]
    public async Task The_router_finds_the_advertised_peer_and_routes_to_it()
    {
        var bus = new Bus();
        var a = bus.Connect("A");
        var b = bus.Connect("B");

        var registry = new InMemoryMeshCapabilityRegistry();
        await registry.UpsertAsync(new MeshCapabilityAdvertisement(
            PeerId: "B",
            ModelId: "Qwen3-0.6B-MNN",
            FreeKvTokens: 100_000,
            Tier: DeviceTier.Phone,
            ContextWindowTokens: 4096,
            AdvertisedAtUtc: DateTimeOffset.UtcNow,
            LatencyHintMs: 20));

        await using var server = Client(b, new BridgeLocalInferenceFallback(new StubBridge("42")), "B", serve: true);
        await using var borrower = Client(a, NullLocalInferenceFallback.Instance, "A", serve: false);
        await server.StartAsync(default);
        await borrower.StartAsync(default);

        var router = new MeshOffloadRouter(
            registry, (IMeshOffloadClient)borrower, NullLocalInferenceFallback.Instance,
            Options.Create(new MeshOffloadOptions { LocalNodeId = "A" }));

        var res = await router.RouteAsync(OffloadTurn.Create("Qwen3-0.6B-MNN", "the answer?"));

        Assert.True(res.Success);
        Assert.Equal("42", res.OutputText);
        Assert.Equal(OffloadServedBy.RemotePeer, res.ServedBy);
        Assert.Equal("B", res.ServingPeerId);
    }

    [Fact]
    public async Task A_peer_that_cannot_serve_replies_with_a_failure_rather_than_hanging()
    {
        var bus = new Bus();
        var a = bus.Connect("A");
        var b = bus.Connect("B");

        // B answers inbound requests but has no real brain — it must reply a
        // failure fast, not leave the borrower to time out.
        await using var server = Client(b, NullLocalInferenceFallback.Instance, "B", serve: true);
        await using var borrower = Client(a, NullLocalInferenceFallback.Instance, "A", serve: false);
        await server.StartAsync(default);
        await borrower.StartAsync(default);

        var res = await ((IMeshOffloadClient)borrower).RequestAsync(
            "B", OffloadTurn.Create("Qwen3-0.6B-MNN", "hi"), TimeSpan.FromSeconds(5));

        Assert.False(res.Success);
        Assert.Equal(OffloadServedBy.RemotePeer, res.ServedBy);   // B replied — it did not hang
        Assert.False(string.IsNullOrWhiteSpace(res.FailureReason));
    }
}
