// AetherMeshTransport.cs
//
// The optional AetherNet CARRIER for the mesh offload engine: an INetworkTransport
// (CircleAI.Networking) that carries CircleAI mesh payloads to a peer BY UHID,
// sealed peer-to-peer with the Signal double ratchet, over any AetherNet
// ITransportService (Wi-Fi Direct, a sealed internet node-relay, ...).
//
// This is the plug the PRODUCT never links. CircleAI.Mesh knows only the abstract
// INetworkTransport seam (see AetherNetIsolationTests); the device head wires this.
// It is interchangeable with an internet transport or the in-memory test loopback
// behind that same seam — the engine cannot tell which carrier it rode.
//
// Framing: every wire buffer is [1-byte kind][body]:
//   0 BundleRequest  — "send me your pre-key bundle so I can open a session"
//   1 BundleResponse — the bundle (JSON), used to open the session
//   2 Sealed         — the whole NetworkPayload, sealed inside the Signal envelope
// A sealed payload shows the transport only ciphertext plus the peer UHID it routes
// by — nothing of the payload rides in clear.
//
// Sessions (A1b): the handshake runs itself. SendAsync to a peer with no session
// first requests that peer's bundle, opens the session (ProcessPreKeyBundleAsync),
// then seals and sends — the reply direction bootstraps from the initiator keys the
// first sealed message carries, so the peer needs no prior bundle of ours. If the
// peer never answers, SendAsync fails (timeout) rather than leaking or hanging.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using AetherNet.Messaging;                 // EncryptedPayloadCodec (static)
using AetherNet.Security.Models;           // EncryptedPayload, PreKeyBundle
using AetherNet.Security.Services;         // ISignalProtocolService
using AetherNet.Transport.Abstractions;    // ITransportService
using CircleAI.Networking;                 // INetworkTransport, NetworkPayload, TransportKind, MessagePriority

namespace CircleAI.AetherNet;

/// <summary>
/// An <see cref="INetworkTransport"/> that rides an AetherNet <see cref="ITransportService"/>,
/// sealing every payload to a peer with the Signal protocol and opening the session
/// on demand. The optional carrier plug for CircleAI.Mesh; the product links none of this.
/// </summary>
public sealed class AetherMeshTransport : INetworkTransport, IAsyncDisposable, IDisposable
{
    private const byte KindBundleRequest = 0;
    private const byte KindBundleResponse = 1;
    private const byte KindSealed = 2;

    private readonly ITransportService _transport;
    private readonly ISignalProtocolService _signal;
    private readonly string _localUhid;
    private readonly TimeSpan _handshakeTimeout;
    private readonly Channel<NetworkPayload> _inbound = Channel.CreateUnbounded<NetworkPayload>();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _handshakes = new(StringComparer.Ordinal);
    private Action<string, byte[]>? _onData;
    private int _started;

    /// <param name="transport">The AetherNet byte carrier (Wi-Fi Direct, node-relay, ...).</param>
    /// <param name="signal">Seals/unseals payloads, opens and tracks the per-peer session.</param>
    /// <param name="localUhid">This device's AetherTag / UHID.</param>
    /// <param name="handshakeTimeout">How long to wait for a peer's bundle. Default 10s.</param>
    public AetherMeshTransport(
        ITransportService transport, ISignalProtocolService signal, string localUhid, TimeSpan? handshakeTimeout = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _signal = signal ?? throw new ArgumentNullException(nameof(signal));
        if (string.IsNullOrWhiteSpace(localUhid))
            throw new ArgumentException("A local UHID is required.", nameof(localUhid));
        _localUhid = localUhid;
        _handshakeTimeout = handshakeTimeout ?? TimeSpan.FromSeconds(10);
    }

    /// <inheritdoc/>
    public TransportKind Kind => TransportKind.Aether;

    /// <inheritdoc/>
    public bool IsAvailable => _transport.IsAvailable;

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return Task.CompletedTask;
        _onData = (fromUhid, bytes) => _ = ReceiveOneAsync(fromUhid, bytes);
        _transport.DataReceived += _onData;
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken ct = default) { Cleanup(); return Task.CompletedTask; }

    // Cleanup is fully synchronous (unsubscribe, cancel pending handshakes, complete
    // the inbound channel), so Dispose, DisposeAsync and StopAsync all share it and
    // any host disposal path — sync or async — is safe. Idempotent.
    private void Cleanup()
    {
        if (Interlocked.Exchange(ref _started, 0) == 0) return;
        if (_onData is not null) _transport.DataReceived -= _onData;
        _onData = null;
        foreach (var kv in _handshakes)
            kv.Value.TrySetCanceled();
        _handshakes.Clear();
        _inbound.Writer.TryComplete();
    }

    /// <inheritdoc/>
    public async Task SendAsync(NetworkPayload payload, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var peer = payload.DestinationId;
        if (string.IsNullOrWhiteSpace(peer))
            throw new InvalidOperationException(
                "AetherMeshTransport carries point-to-point offload to a chosen node; a broadcast " +
                "payload (no DestinationId) has no addressee, and capability adverts are not carried here.");

        if (!_signal.HasSession(peer))
            await EnsureSessionAsync(peer, ct).ConfigureAwait(false);

        var frameBytes = JsonSerializer.SerializeToUtf8Bytes(
            SealedFrame.From(payload), AetherMeshJson.Default.SealedFrame);

        EncryptedPayload sealed_ = await _signal.EncryptAsync(peer, frameBytes, ct).ConfigureAwait(false);
        byte[] wire = Frame(KindSealed, EncryptedPayloadCodec.Serialize(sealed_));

        await _transport.SendAsync(peer, wire, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<NetworkPayload> ReceiveAsync(CancellationToken ct = default)
        => _inbound.Reader.ReadAllAsync(ct);

    // ── Session handshake ────────────────────────────────────────────────────

    private async Task EnsureSessionAsync(string peer, CancellationToken ct)
    {
        if (_signal.HasSession(peer)) return;

        var tcs = _handshakes.GetOrAdd(peer, _ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
        try
        {
            // Ask the peer for its bundle; our UHID rides so the reply is addressed back.
            await _transport.SendAsync(peer, Frame(KindBundleRequest, Encoding.UTF8.GetBytes(_localUhid)), ct).ConfigureAwait(false);
            await tcs.Task.WaitAsync(_handshakeTimeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _handshakes.TryRemove(peer, out _);
            throw new TimeoutException(
                $"Peer '{peer}' did not return a pre-key bundle within {_handshakeTimeout.TotalSeconds:0.#}s; cannot open a sealed session.");
        }
    }

    private async Task ReceiveOneAsync(string fromUhid, byte[] wire)
    {
        if (wire is null || wire.Length < 1) return;
        byte kind = wire[0];
        byte[] body = wire.Length > 1 ? wire[1..] : Array.Empty<byte>();
        try
        {
            switch (kind)
            {
                case KindBundleRequest:
                    await RespondWithBundleAsync(fromUhid).ConfigureAwait(false);
                    break;

                case KindBundleResponse:
                    await OpenSessionFromBundleAsync(fromUhid, body).ConfigureAwait(false);
                    break;

                case KindSealed:
                    await AcceptSealedAsync(fromUhid, body).ConfigureAwait(false);
                    break;

                default:
                    break; // not ours
            }
        }
        catch
        {
            // Undecodable / undecryptable / unreachable peer: drop it; the pump lives on.
        }
    }

    private async Task RespondWithBundleAsync(string toUhid)
    {
        PreKeyBundle bundle = await _signal.GeneratePreKeyBundleAsync(_localUhid).ConfigureAwait(false);
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(BundleWire.From(bundle), AetherMeshJson.Default.BundleWire);
        await _transport.SendAsync(toUhid, Frame(KindBundleResponse, json), CancellationToken.None).ConfigureAwait(false);
    }

    private async Task OpenSessionFromBundleAsync(string fromUhid, byte[] body)
    {
        var wire = JsonSerializer.Deserialize(body, AetherMeshJson.Default.BundleWire);
        if (wire is null) return;
        await _signal.ProcessPreKeyBundleAsync(wire.ToBundle()).ConfigureAwait(false);
        if (_handshakes.TryRemove(fromUhid, out var tcs))
            tcs.TrySetResult(true);
    }

    private async Task AcceptSealedAsync(string fromUhid, byte[] body)
    {
        EncryptedPayload sealed_ = EncryptedPayloadCodec.Deserialize(body);
        byte[] frameBytes = await _signal.DecryptAsync(fromUhid, sealed_, CancellationToken.None).ConfigureAwait(false);
        var frame = JsonSerializer.Deserialize(frameBytes, AetherMeshJson.Default.SealedFrame);
        if (frame is not null)
            _inbound.Writer.TryWrite(frame.ToPayload(fromUhid, _localUhid));
    }

    private static byte[] Frame(byte kind, byte[] body)
    {
        var buf = new byte[body.Length + 1];
        buf[0] = kind;
        Buffer.BlockCopy(body, 0, buf, 1, body.Length);
        return buf;
    }

    /// <inheritdoc/>
    public void Dispose() => Cleanup();

    /// <inheritdoc/>
    public ValueTask DisposeAsync() { Cleanup(); return ValueTask.CompletedTask; }
}

// ── Wire frame: the whole NetworkPayload, carried inside the sealed envelope ──
internal sealed record SealedFrame(
    string? SourceId,
    string? DestinationId,
    string ContentType,
    Dictionary<string, string> Metadata,
    string Id,
    long CreatedAtUnixMs,
    int Priority,
    long? TtlMs,
    string DataBase64)
{
    public static SealedFrame From(NetworkPayload p) => new(
        SourceId: p.SourceId,
        DestinationId: p.DestinationId,
        ContentType: p.ContentType,
        Metadata: new Dictionary<string, string>(p.Metadata),
        Id: p.Id,
        CreatedAtUnixMs: p.CreatedAt.ToUnixTimeMilliseconds(),
        Priority: (int)p.Priority,
        TtlMs: p.Ttl is { } t ? (long)t.TotalMilliseconds : null,
        DataBase64: Convert.ToBase64String(p.Data.Span));

    public NetworkPayload ToPayload(string fromUhid, string localUhid) => new(
        Id: Id,
        SourceId: SourceId ?? fromUhid,
        DestinationId: DestinationId ?? localUhid,
        Data: Convert.FromBase64String(DataBase64),
        Priority: (MessagePriority)Priority,
        Ttl: TtlMs is { } ms ? TimeSpan.FromMilliseconds(ms) : null,
        ContentType: ContentType,
        Metadata: Metadata,
        CreatedAt: DateTimeOffset.FromUnixTimeMilliseconds(CreatedAtUnixMs));
}

// ── Wire form of a pre-key bundle (base64 byte fields), for the handshake ──
internal sealed record BundleWire(
    string Uhid,
    string IdentityKey,
    string IdentityKeyX25519,
    int PreKeyId,
    string PreKey,
    int SignedPreKeyId,
    string SignedPreKey,
    string SignedPreKeySignature)
{
    public static BundleWire From(PreKeyBundle b) => new(
        Uhid: b.Uhid,
        IdentityKey: B64(b.IdentityKey),
        IdentityKeyX25519: B64(b.IdentityKeyX25519),
        PreKeyId: b.PreKeyId,
        PreKey: B64(b.PreKey),
        SignedPreKeyId: b.SignedPreKeyId,
        SignedPreKey: B64(b.SignedPreKey),
        SignedPreKeySignature: B64(b.SignedPreKeySignature));

    public PreKeyBundle ToBundle() => new(
        Uhid, U(IdentityKey), U(IdentityKeyX25519), PreKeyId, U(PreKey),
        SignedPreKeyId, U(SignedPreKey), U(SignedPreKeySignature));

    private static string B64(byte[]? b) => Convert.ToBase64String(b ?? Array.Empty<byte>());
    private static byte[] U(string s) => Convert.FromBase64String(s);
}

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(SealedFrame))]
[JsonSerializable(typeof(BundleWire))]
internal sealed partial class AetherMeshJson : JsonSerializerContext { }
