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
// Framing: the ENTIRE NetworkPayload (source, content-type, correlation metadata,
// bytes) is sealed inside the Signal envelope. The transport sees only ciphertext
// plus the peer UHID it needs to route — nothing of the payload rides in clear.
//
// Sessions (A1a): a Signal session with the peer must already exist — the borrower
// hand-picks a trusted node and the pre-key handshake has run. If none exists,
// SendAsync throws so MeshOffloadClient surfaces a clean failure and nothing leaves
// unsealed. Establishing the session over the wire (the pre-key handshake) is the
// next slice, A1b.

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using AetherNet.Messaging;                 // EncryptedPayloadCodec (static)
using AetherNet.Security.Models;           // EncryptedPayload
using AetherNet.Security.Services;         // ISignalProtocolService
using AetherNet.Transport.Abstractions;    // ITransportService
using CircleAI.Networking;                 // INetworkTransport, NetworkPayload, TransportKind, MessagePriority

namespace CircleAI.AetherNet;

/// <summary>
/// An <see cref="INetworkTransport"/> that rides an AetherNet <see cref="ITransportService"/>,
/// sealing every payload to a peer with the Signal protocol. The optional carrier
/// plug for CircleAI.Mesh; the product links none of this.
/// </summary>
public sealed class AetherMeshTransport : INetworkTransport, IAsyncDisposable
{
    private readonly ITransportService _transport;
    private readonly ISignalProtocolService _signal;
    private readonly string _localUhid;
    private readonly Channel<NetworkPayload> _inbound = Channel.CreateUnbounded<NetworkPayload>();
    private Action<string, byte[]>? _onData;
    private int _started;

    /// <param name="transport">The AetherNet byte carrier (Wi-Fi Direct, node-relay, ...).</param>
    /// <param name="signal">Seals/unseals payloads and tracks the per-peer session.</param>
    /// <param name="localUhid">This device's AetherTag / UHID.</param>
    public AetherMeshTransport(ITransportService transport, ISignalProtocolService signal, string localUhid)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _signal = signal ?? throw new ArgumentNullException(nameof(signal));
        if (string.IsNullOrWhiteSpace(localUhid))
            throw new ArgumentException("A local UHID is required.", nameof(localUhid));
        _localUhid = localUhid;
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
    public Task StopAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _started, 0) == 0) return Task.CompletedTask;
        if (_onData is not null) _transport.DataReceived -= _onData;
        _onData = null;
        _inbound.Writer.TryComplete();
        return Task.CompletedTask;
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

        // Refuse to send before a session exists — better a clean failure the router
        // can act on than a payload that leaves in clear.
        if (!_signal.HasSession(peer))
            throw new InvalidOperationException(
                $"No Signal session with peer '{peer}'; the pre-key handshake must run first.");

        var frameBytes = JsonSerializer.SerializeToUtf8Bytes(
            SealedFrame.From(payload), SealedFrameJson.Default.SealedFrame);

        EncryptedPayload sealed_ = await _signal.EncryptAsync(peer, frameBytes, ct).ConfigureAwait(false);
        byte[] wire = EncryptedPayloadCodec.Serialize(sealed_);

        await _transport.SendAsync(peer, wire, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<NetworkPayload> ReceiveAsync(CancellationToken ct = default)
        => _inbound.Reader.ReadAllAsync(ct);

    private async Task ReceiveOneAsync(string fromUhid, byte[] wire)
    {
        try
        {
            EncryptedPayload sealed_ = EncryptedPayloadCodec.Deserialize(wire);
            byte[] frameBytes = await _signal.DecryptAsync(fromUhid, sealed_, CancellationToken.None).ConfigureAwait(false);
            var frame = JsonSerializer.Deserialize(frameBytes, SealedFrameJson.Default.SealedFrame);
            if (frame is not null)
                _inbound.Writer.TryWrite(frame.ToPayload(fromUhid, _localUhid));
        }
        catch
        {
            // Undecodable / undecryptable: not ours (a shared transport carries other
            // traffic) or corrupt. Drop it; the receive pump keeps running.
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
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

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(SealedFrame))]
internal sealed partial class SealedFrameJson : JsonSerializerContext { }
