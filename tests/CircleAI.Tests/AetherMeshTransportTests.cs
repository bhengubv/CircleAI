// AetherMeshTransportTests.cs
//
// The AetherNet carrier plug, proven end-to-end with no device: a borrower asks
// over TWO AetherMeshTransport instances joined by an in-memory ITransportService
// bus, the session OPENS ITSELF (pre-key handshake), each payload is sealed by the
// (faked) Signal cipher, and the REAL BridgeLocalInferenceFallback serves the turn
// on the far node. This mirrors MeshOffloadRoundTripTests — but where that used a
// plaintext loopback, this proves the AetherNet-backed INetworkTransport: the
// handshake, framing, UHID routing, and that nothing of the payload rides in clear.
//
// The cipher is faked on purpose: real Signal crypto is AetherNet's own tested
// concern. The fake models the three facts the adapter relies on — opening a
// session from a peer's bundle, and a received sealed message opening the reply
// direction — so the adapter's contract (handshake, seal, route) is what is pinned.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AetherNet.Security.Models;
using AetherNet.Security.Services;
using AetherNet.Transport.Abstractions;
using AetherNet.Transport.Models;
using CircleAI.AetherNet;
using CircleAI.Hosting.InferenceBridge;
using CircleAI.Mesh;
using CircleAI.Mesh.Hosting;
using CircleAI.Networking;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace CircleAI.Tests;

public class AetherMeshTransportTests
{
    // ── An in-memory AetherNet transport: SendAsync(peer, bytes) is delivered to
    //    that peer's DataReceived, and every wire buffer is captured. ──────────
    private sealed class Bus
    {
        private readonly Dictionary<string, FakeTransport> _byUhid = new(StringComparer.Ordinal);
        public readonly List<byte[]> Wire = new();

        public void Register(string uhid, FakeTransport t) { lock (_byUhid) _byUhid[uhid] = t; }

        public void Deliver(string toUhid, string fromUhid, byte[] data)
        {
            lock (Wire) Wire.Add(data);
            FakeTransport? t;
            lock (_byUhid) _byUhid.TryGetValue(toUhid, out t);
            t?.Raise(fromUhid, data);
        }
    }

    private sealed class FakeTransport : ITransportService
    {
        private readonly Bus _bus;
        private readonly string _uhid;
        public FakeTransport(Bus bus, string uhid) { _bus = bus; _uhid = uhid; bus.Register(uhid, this); }

        public event Action<string, byte[]>? DataReceived;
        public void Raise(string fromUhid, byte[] data) => DataReceived?.Invoke(fromUhid, data);

        public Task<bool> SendAsync(string peerUhid, byte[] data, CancellationToken cancellationToken = default)
        { _bus.Deliver(peerUhid, _uhid, data); return Task.FromResult(true); }

        public Task<bool> SendStreamAsync(string peerUhid, Stream stream, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public bool IsConnected(string peerUhid) => true;
        public string Name => "in-memory";
        public bool IsAvailable => true;
        public long MaxBandwidthBps => 0;
        public int MaxRangeMeters => 0;
        public int PowerCostRelative => 0;
        public int MaxConcurrentPeers => 0;
        public PerTransportMetrics Metrics { get; } = new();
    }

    // ── A stand-in Signal cipher: reversible XOR. A session opens by processing a
    //    peer's bundle OR by receiving (decrypting) a sealed message from them —
    //    which is how the real X3DH bootstraps the reply direction. ─────────────
    private sealed class FakeSignal : ISignalProtocolService
    {
        private const byte Mask = 0x5A;
        private readonly string _me;
        private readonly HashSet<string> _sessions;
        public FakeSignal(string me, params string[] peersWithSession)
        { _me = me; _sessions = new HashSet<string>(peersWithSession, StringComparer.Ordinal); }

        public bool HasSession(string peerUhid) { lock (_sessions) return _sessions.Contains(peerUhid); }

        public Task<EncryptedPayload> EncryptAsync(string peerUhid, byte[] plaintext, CancellationToken ct = default)
        {
            var c = (byte[])plaintext.Clone();
            for (int i = 0; i < c.Length; i++) c[i] ^= Mask;
            return Task.FromResult(new EncryptedPayload(
                Ciphertext: c, Nonce: new byte[12], MessageType: 1, SenderUhid: _me, Counter: 0,
                InitiatorIdentityKeyX25519: new byte[32], InitiatorEphemeralKeyX25519: new byte[32],
                UsedSignedPreKeyId: 1, UsedOneTimePreKeyId: 0, SenderEphemeralKeyX25519: new byte[32],
                PreviousChainCount: 0));
        }

        public Task<byte[]> DecryptAsync(string peerUhid, EncryptedPayload payload, CancellationToken ct = default)
        {
            lock (_sessions) _sessions.Add(peerUhid);   // receiving opens the reply session
            var p = (byte[])payload.Ciphertext.Clone();
            for (int i = 0; i < p.Length; i++) p[i] ^= Mask;
            return Task.FromResult(p);
        }

        public Task<PreKeyBundle> GeneratePreKeyBundleAsync(string localUhid, CancellationToken ct = default)
            => Task.FromResult(new PreKeyBundle(
                localUhid, new byte[32], new byte[32], 1, new byte[32], 1, new byte[64], new byte[64]));

        public Task ProcessPreKeyBundleAsync(PreKeyBundle bundle, CancellationToken ct = default)
        {
            lock (_sessions) _sessions.Add(bundle.Uhid);   // opening from a bundle establishes the session
            return Task.CompletedTask;
        }

        public Task<byte[]> SignDataAsync(byte[] data, CancellationToken ct = default) => throw new NotSupportedException();
        public bool VerifySignature(byte[] publicKey, byte[] data, byte[] signature) => throw new NotSupportedException();
        public byte[] DeriveEridRoutingKey() => throw new NotSupportedException();
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
        => new(transport, new InMemoryMeshCapabilityRegistry(), fallback,
               Options.Create(new MeshOffloadOptions { LocalNodeId = nodeId, ServeInboundRequests = serve, StartTransport = false }));

    private static bool Contains(byte[] hay, byte[] needle)
    {
        if (needle.Length == 0 || hay.Length < needle.Length) return false;
        for (int i = 0; i <= hay.Length - needle.Length; i++)
        {
            bool ok = true;
            for (int j = 0; j < needle.Length; j++) if (hay[i + j] != needle[j]) { ok = false; break; }
            if (ok) return true;
        }
        return false;
    }

    [Fact]
    public async Task A_borrowed_turn_auto_handshakes_seals_and_is_served_remotely()
    {
        var bus = new Bus();
        // No pre-declared sessions: the pre-key handshake must open them itself.
        var aT = new AetherMeshTransport(new FakeTransport(bus, "A"), new FakeSignal("A"), "A");
        var bT = new AetherMeshTransport(new FakeTransport(bus, "B"), new FakeSignal("B"), "B");
        await aT.StartAsync(); await bT.StartAsync();

        await using var server = Client(bT, new BridgeLocalInferenceFallback(new StubBridge("Tokyo.")), "B", serve: true);
        await using var borrower = Client(aT, NullLocalInferenceFallback.Instance, "A", serve: false);
        await server.StartAsync(default);
        await borrower.StartAsync(default);

        const string question = "capital of Japan?";
        var res = await ((IMeshOffloadClient)borrower).RequestAsync(
            "B", OffloadTurn.Create("Qwen3-0.6B-MNN", question), TimeSpan.FromSeconds(5));

        Assert.True(res.Success);
        Assert.Equal("Tokyo.", res.OutputText);
        Assert.Equal(OffloadServedBy.RemotePeer, res.ServedBy);
        Assert.Equal("B", res.ServingPeerId);

        // Privacy, pinned: neither the question nor the answer is on the wire in clear.
        Assert.NotEmpty(bus.Wire);
        var q = Encoding.UTF8.GetBytes(question);
        var a = Encoding.UTF8.GetBytes("Tokyo.");
        Assert.All(bus.Wire, w => Assert.False(Contains(w, q), "question leaked in clear"));
        Assert.All(bus.Wire, w => Assert.False(Contains(w, a), "answer leaked in clear"));
    }

    [Fact]
    public async Task An_unreachable_peer_times_out_rather_than_hanging_or_leaking()
    {
        var bus = new Bus();
        // "A" is alone on the bus; "B" never answers the bundle request.
        var t = new AetherMeshTransport(
            new FakeTransport(bus, "A"), new FakeSignal("A"), "A", TimeSpan.FromMilliseconds(300));
        await t.StartAsync();

        const string secret = "do not leak me";
        var payload = NetworkPayload.Create(Encoding.UTF8.GetBytes(secret), destinationId: "B", contentType: "x");

        await Assert.ThrowsAsync<TimeoutException>(() => t.SendAsync(payload));

        // Only the (plaintext-free) bundle request went out; the secret never did.
        var s = Encoding.UTF8.GetBytes(secret);
        Assert.All(bus.Wire, w => Assert.False(Contains(w, s), "payload leaked before a session existed"));
    }

    [Fact]
    public async Task A_broadcast_payload_has_no_addressee_on_a_point_to_point_carrier()
    {
        var bus = new Bus();
        var t = new AetherMeshTransport(new FakeTransport(bus, "A"), new FakeSignal("A", "B"), "A");
        await t.StartAsync();

        // No DestinationId → broadcast; this carrier is point-to-point.
        var advert = NetworkPayload.Create(new byte[] { 9 }, destinationId: null, contentType: "advert");
        await Assert.ThrowsAsync<InvalidOperationException>(() => t.SendAsync(advert));
    }

    [Fact]
    public void The_DI_extension_resolves_the_carrier_as_the_mesh_INetworkTransport()
    {
        var bus = new Bus();
        var services = new ServiceCollection();
        services.AddSingleton<ITransportService>(new FakeTransport(bus, "me"));
        services.AddSingleton<ISignalProtocolService>(new FakeSignal("me"));
        services.AddCircleAiMeshAetherTransport(_ => "me");

        using var sp = services.BuildServiceProvider();
        var transport = sp.GetRequiredService<INetworkTransport>();

        Assert.IsType<AetherMeshTransport>(transport);
        Assert.Equal(TransportKind.Aether, transport.Kind);
    }
}
