// CatalogueMeshCarrierTests.cs
//
// The model catalogue crossing AETHERNET, end to end with no device: node A
// shares its catalogue over an in-memory mesh, node B's receive pump dispatches
// it, and it reaches ModelCatalogue.Offer on B — the exact seam the runtime
// CatalogueUpdater already listens on. Plus the two rules that keep it sane:
// a mesh-sourced catalogue is never re-shared (no gossip storm), and sharing is
// point-to-point to known peers (so it crosses the sealed Aether carrier, which
// refuses broadcasts).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using CircleAI.Core;
using CircleAI.Core.Models;
using CircleAI.Mesh;
using CircleAI.Networking;
using Microsoft.Extensions.Options;
using Xunit;

namespace CircleAI.Tests;

public class CatalogueMeshCarrierTests
{
    // ── In-memory mesh (routes by DestinationId; broadcast when unaddressed) ──
    private sealed class Bus
    {
        private readonly List<Endpoint> _eps = new();
        public Endpoint Connect(string nodeId) { var e = new Endpoint(this, nodeId); lock (_eps) _eps.Add(e); return e; }
        internal void Publish(Endpoint from, NetworkPayload p)
        {
            lock (_eps)
                foreach (var e in _eps)
                {
                    if (ReferenceEquals(e, from)) continue;
                    if (p.DestinationId is { Length: > 0 } dest && !string.Equals(e.NodeId, dest, StringComparison.Ordinal)) continue;
                    e.Inbox.Writer.TryWrite(p);
                }
        }
    }

    private sealed class Endpoint : INetworkTransport
    {
        private readonly Bus _bus;
        public string NodeId { get; }
        public Channel<NetworkPayload> Inbox { get; } = Channel.CreateUnbounded<NetworkPayload>();
        public Endpoint(Bus bus, string nodeId) { _bus = bus; NodeId = nodeId; }
        public TransportKind Kind => TransportKind.Aether;
        public bool IsAvailable => true;
        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct = default) { Inbox.Writer.TryComplete(); return Task.CompletedTask; }
        public Task SendAsync(NetworkPayload payload, CancellationToken ct = default) { _bus.Publish(this, payload); return Task.CompletedTask; }
        public IAsyncEnumerable<NetworkPayload> ReceiveAsync([EnumeratorCancellation] CancellationToken ct = default)
            => Inbox.Reader.ReadAllAsync(ct);
    }

    // Records outbound payloads; never delivers anything inbound.
    private sealed class RecordingTransport : INetworkTransport
    {
        public List<NetworkPayload> Sent { get; } = new();
        public TransportKind Kind => TransportKind.Aether;
        public bool IsAvailable => true;
        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SendAsync(NetworkPayload payload, CancellationToken ct = default) { Sent.Add(payload); return Task.CompletedTask; }
        public async IAsyncEnumerable<NetworkPayload> ReceiveAsync([EnumeratorCancellation] CancellationToken ct = default)
        { await Task.CompletedTask; yield break; }
    }

    static IOptions<MeshOffloadOptions> Opts(string nodeId)
        => Options.Create(new MeshOffloadOptions { LocalNodeId = nodeId, StartTransport = false });

    static MeshCapabilityAdvertisement Peer(string id)
        => new(id, "any", 0, DeviceTier.Phone, 4096, DateTimeOffset.UtcNow);

    static ModelRegistry Registry(params string[] names)
    {
        var models = new List<ModelEntry>();
        foreach (var n in names)
            models.Add(new ModelEntry(n, "1.0", "MNN-Q4")
            {
                Engine = ModelEngine.Mnn, Modality = ModelModality.Chat, TotalBytes = 100_000_000,
                MinRamGb = 0.6, MinStorageGb = 0.1, QualityRank = 8, License = "Apache-2.0",
                Capabilities = new[] { "Default" },
                BundleFiles = new[] { new BundleFile("llm.mnn.weight", "abc123", 90_000_000) },
            });
        return new ModelRegistry("mesh://test", DateTime.UtcNow, models);
    }

    [Fact]
    public async Task A_shared_catalogue_crosses_the_mesh_and_reaches_a_peers_Offer()
    {
        const string unique = "mesh-e2e-Qwen9-Test";
        var bus = new Bus();
        var a = bus.Connect("A");
        var b = bus.Connect("B");

        // B receives; wire a real client + start its pump.
        await using var receiver = new MeshOffloadClient(
            b, new InMemoryMeshCapabilityRegistry(), NullLocalInferenceFallback.Instance, Opts("B"));
        await receiver.StartAsync(default);

        // A knows about B, so its point-to-point share is addressed to B.
        var aPeers = new InMemoryMeshCapabilityRegistry();
        await aPeers.UpsertAsync(Peer("B"));
        using var sender = new CatalogueMeshShare(a, aPeers, Opts("A"));

        var arrived = new TaskCompletionSource<(ModelRegistry Reg, string Source)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        void OnArrived(ModelRegistry r, string s)
        {
            if (r.Models.Any(m => m.Name == unique)) arrived.TrySetResult((r, s));
        }
        ModelCatalogue.CatalogueArrived += OnArrived;
        try
        {
            await sender.ShareAsync(Registry(unique));

            var (reg, source) = await arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("aethernet", source);                       // arrived over the mesh
            Assert.Contains(reg.Models, m => m.Name == unique);
            Assert.Equal("abc123", reg.Models.Single(m => m.Name == unique).BundleFiles![0].Sha256);  // SHA survived the wire
        }
        finally
        {
            ModelCatalogue.CatalogueArrived -= OnArrived;
            ModelCatalogue.Forget();
        }
    }

    [Fact]
    public async Task ShareAsync_sends_point_to_point_to_each_known_peer()
    {
        var transport = new RecordingTransport();
        var peers = new InMemoryMeshCapabilityRegistry();
        await peers.UpsertAsync(Peer("B"));
        using var share = new CatalogueMeshShare(transport, peers, Opts("A"));

        await share.ShareAsync(Registry("mesh-p2p-model"));

        Assert.Single(transport.Sent);
        Assert.Equal("B", transport.Sent[0].DestinationId);                 // directed, not broadcast
        Assert.Contains("model-catalogue", transport.Sent[0].ContentType);
    }

    [Fact]
    public async Task A_mesh_sourced_catalogue_is_not_re_shared()
    {
        var transport = new RecordingTransport();
        var peers = new InMemoryMeshCapabilityRegistry();
        await peers.UpsertAsync(Peer("B"));
        using var share = new CatalogueMeshShare(transport, peers, Opts("A"));
        share.Attach();
        try
        {
            ModelCatalogue.Offer(Registry("mesh-nogossip-model"));   // Offer fires source "aethernet"
            Assert.Empty(transport.Sent);                            // not re-shared → no gossip storm
        }
        finally
        {
            ModelCatalogue.Forget();
        }
    }
}
