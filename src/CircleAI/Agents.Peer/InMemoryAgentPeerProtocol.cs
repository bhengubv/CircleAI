// InMemoryAgentPeerProtocol.cs
//
// Reference implementation of IAgentPeerProtocol that uses an in-process
// AgentBus as its transport. Multiple instances sharing one bus simulate a
// small mesh of CircleAI devices.
//
// Real implementations (BLE, Wi-Fi Direct, Aether router) live in
// CircleAI.Aether and follow the same contract.

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using CircleAI.Core.Components;
using CircleAI.Core.Validation;

namespace CircleAI.Agents.Peer;

/// <summary>
/// In-memory reference implementation of <see cref="IAgentPeerProtocol"/>.
/// Backed by an <see cref="AgentBus"/> so multiple instances can simulate a
/// mesh of CircleAI peers in tests and samples.
/// </summary>
[Experimental("CIRCLEAI_PEER_001", UrlFormat = "https://github.com/bhengubv/CircleAI/blob/master/docs/experimental.md#{0}")]
[CircleAIVerificationStatus(VerificationLevel.Reference,
    Notes = "In-process channel-backed bus. Designed for tests and same-process simulations. Not transport-backed — use an Aether-backed IAgentPeerProtocol in production.")]
public sealed class InMemoryAgentPeerProtocol : CircleAIComponentBase, IAgentPeerProtocol, IDisposable
{
    private static readonly TimeSpan DefaultDiscoveryWindow = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan DefaultInvokeTimeout = TimeSpan.FromSeconds(5);

    private readonly string _ownUhid;
    private readonly AgentBus _bus;
    private readonly IReadOnlyList<AgentCapability> _ownCapabilities;
    private readonly byte[] _ownPublicKey;
    private readonly Func<byte[], byte[]>? _signer;
    private readonly Func<AgentCapability, byte[], byte[]>? _capabilityHandler;

    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastSeen =
        new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<AgentMessage>> _pendingInvocations =
        new();

    private readonly CancellationTokenSource _runCts = new();
    private readonly Task _pumpTask;
    private readonly Channel<AgentMessage> _externalInbox =
        Channel.CreateUnbounded<AgentMessage>(
            new UnboundedChannelOptions { SingleReader = false, SingleWriter = true });

    private int _disposed;

    /// <inheritdoc />
    public override string ComponentName => "InMemoryAgentPeerProtocol";

    /// <summary>
    /// Creates a new in-memory protocol instance, registers it on
    /// <paramref name="bus"/>, and begins pumping the inbox.
    /// </summary>
    /// <param name="ownUhid">Hashed UHID identity owned by this agent.</param>
    /// <param name="bus">Shared bus standing in for the Aether transport.</param>
    /// <param name="ownCapabilities">Capabilities this agent advertises to peers.</param>
    /// <param name="ownPublicKey">DER-encoded public key from the agent's UhidKeyRing.</param>
    /// <param name="signer">
    /// Optional delegate that signs outbound payloads. When <c>null</c>, outbound
    /// messages carry an empty <see cref="AgentMessage.Signature"/>. In production
    /// the signer is wired to the agent's UhidKeyRing.
    /// </param>
    /// <param name="capabilityHandler">
    /// Optional delegate invoked when a peer sends <see cref="AgentMessageKind.Invoke"/>.
    /// Returning a non-null byte array sends a <see cref="AgentMessageKind.Response"/>;
    /// returning <c>null</c> sends a <see cref="AgentMessageKind.Decline"/>.
    /// </param>
    public InMemoryAgentPeerProtocol(
        string ownUhid,
        AgentBus bus,
        IReadOnlyList<AgentCapability> ownCapabilities,
        byte[] ownPublicKey,
        Func<byte[], byte[]>? signer = null,
        Func<AgentCapability, byte[], byte[]>? capabilityHandler = null)
        : base(logger: null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownUhid);
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(ownCapabilities);
        ArgumentNullException.ThrowIfNull(ownPublicKey);

        _ownUhid = ownUhid;
        _bus = bus;
        _ownCapabilities = ownCapabilities;
        _ownPublicKey = ownPublicKey;
        _signer = signer;
        _capabilityHandler = capabilityHandler;

        _bus.Register(new PeerAgent(
            Id: Guid.NewGuid(),
            UhidIdentityId: ownUhid,
            DisplayName: ownUhid,
            Capabilities: _ownCapabilities,
            PublicKeyDer: _ownPublicKey,
            CurrentTransportId: "in-memory",
            LastSeenAt: DateTimeOffset.UtcNow));

        _pumpTask = Task.Run(PumpInboxAsync);
    }

    /// <summary>The UHID identity owned by this agent.</summary>
    public string OwnUhid => _ownUhid;

    /// <inheritdoc/>
    public Task<IReadOnlyList<PeerAgent>> DiscoverPeersAsync(CancellationToken cancellationToken)
    {
        return RunOperationAsync(
            "DiscoverPeersAsync",
            () => DiscoverPeersImplAsync(cancellationToken),
            cancellationToken);
    }

    /// <inheritdoc/>
    public Task<PeerAgent?> GreetAsync(string targetUhid, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetUhid);

        return RunOperationAsync(
            "GreetAsync",
            () => GreetImplAsync(targetUhid),
            cancellationToken,
            correlationId: targetUhid);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<AgentCapability>> QueryCapabilitiesAsync(
        string targetUhid,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetUhid);

        return RunOperationAsync(
            "QueryCapabilitiesAsync",
            () => QueryCapabilitiesImplAsync(targetUhid),
            cancellationToken,
            correlationId: targetUhid);
    }

    /// <inheritdoc/>
    public Task<AgentMessage> InvokeAsync(
        string targetUhid,
        AgentCapability capability,
        byte[] requestPayload,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetUhid);
        ArgumentNullException.ThrowIfNull(capability);
        ArgumentNullException.ThrowIfNull(requestPayload);

        return RunOperationAsync(
            "InvokeAsync",
            () => InvokeImplAsync(targetUhid, capability, requestPayload, cancellationToken),
            cancellationToken,
            correlationId: targetUhid);
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<AgentMessage> StreamInboxAsync(CancellationToken cancellationToken)
    {
        return RunStreamAsync<AgentMessage>(
            "StreamInboxAsync",
            innerCt => StreamInboxImplAsync(innerCt),
            cancellationToken);
    }

    /// <summary>
    /// Tears down the protocol, unregisters from the bus, and stops the
    /// inbox pump.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _runCts.Cancel();
        try
        {
            _pumpTask.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
            // Expected when the pump observes cancellation.
        }
        _bus.Unregister(_ownUhid);
        _externalInbox.Writer.TryComplete();
        _runCts.Dispose();
    }

    // ── Private impls (wrapped above by RunOperationAsync / RunStreamAsync) ──

    private async Task<IReadOnlyList<PeerAgent>> DiscoverPeersImplAsync(CancellationToken cancellationToken)
    {
        // Broadcast a Discover so peers can refresh their view of us.
        var announcement = AgentMessage.Create(
            AgentMessageKind.Discover,
            _ownUhid,
            "*",
            "application/json",
            payload: [],
            signature: Sign([]));
        _bus.Send(announcement);

        // Brief listen window so any registered peer's responses can land.
        using var window = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        window.CancelAfter(DefaultDiscoveryWindow);
        try
        {
            await Task.Delay(DefaultDiscoveryWindow, window.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Window elapsed or caller cancelled — both fine.
        }

        return _bus.RegisteredPeers
            .Where(p => !string.Equals(p.UhidIdentityId, _ownUhid, StringComparison.Ordinal))
            .Select(WithLastSeen)
            .ToList();
    }

    private Task<PeerAgent?> GreetImplAsync(string targetUhid)
    {
        if (!_bus.TryGetPeer(targetUhid, out var peer))
        {
            return Task.FromResult<PeerAgent?>(null);
        }

        var greet = AgentMessage.Create(
            AgentMessageKind.Greet,
            _ownUhid,
            targetUhid,
            "application/json",
            payload: [],
            signature: Sign([]));
        _bus.Send(greet);

        return Task.FromResult<PeerAgent?>(WithLastSeen(peer));
    }

    private Task<IReadOnlyList<AgentCapability>> QueryCapabilitiesImplAsync(string targetUhid)
    {
        if (!_bus.TryGetPeer(targetUhid, out var peer))
        {
            return Task.FromResult<IReadOnlyList<AgentCapability>>([]);
        }

        return Task.FromResult(peer.Capabilities);
    }

    private async Task<AgentMessage> InvokeImplAsync(
        string targetUhid,
        AgentCapability capability,
        byte[] requestPayload,
        CancellationToken cancellationToken)
    {
        if (!_bus.TryGetPeer(targetUhid, out var unusedPeer))
        {
            throw new AgentInvocationException(
                $"Peer '{targetUhid}' is not reachable on the current transport.", targetUhid);
        }
        _ = unusedPeer;

        var invoke = AgentMessage.Create(
            AgentMessageKind.Invoke,
            _ownUhid,
            targetUhid,
            "application/octet-stream",
            payload: requestPayload,
            signature: Sign(requestPayload));

        var tcs = new TaskCompletionSource<AgentMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingInvocations[invoke.Id] = tcs;

        _bus.Send(invoke);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DefaultInvokeTimeout);

        await using var timeoutRegistration = timeout.Token.Register(static state =>
        {
            ((TaskCompletionSource<AgentMessage>)state!).TrySetCanceled();
        }, tcs).ConfigureAwait(false);

        AgentMessage reply;
        try
        {
            reply = await tcs.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _pendingInvocations.TryRemove(invoke.Id, out var _);
            cancellationToken.ThrowIfCancellationRequested();
            throw new AgentInvocationException(
                $"Invocation of '{capability.Name}' on peer '{targetUhid}' timed out.", targetUhid);
        }

        _pendingInvocations.TryRemove(invoke.Id, out var _);

        if (reply.Kind == AgentMessageKind.Decline)
        {
            throw new AgentInvocationException(
                $"Peer '{targetUhid}' declined '{capability.Name}'.", targetUhid, reply);
        }

        return reply;
    }

    private async IAsyncEnumerable<AgentMessage> StreamInboxImplAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (await _externalInbox.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (_externalInbox.Reader.TryRead(out var message))
            {
                yield return message;
            }
        }
    }

    // ── Private helpers ─────────────────────────────────────────────────────

    private async Task PumpInboxAsync()
    {
        try
        {
            await foreach (var message in _bus.Receive(_ownUhid, _runCts.Token).ConfigureAwait(false))
            {
                _lastSeen[message.FromUhid] = message.SentAt;
                await HandleIncomingAsync(message).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown path.
        }
    }

    private async Task HandleIncomingAsync(AgentMessage message)
    {
        switch (message.Kind)
        {
            case AgentMessageKind.Response:
            case AgentMessageKind.Decline:
                CompletePending(message);
                break;

            case AgentMessageKind.Invoke:
                await RouteInvokeAsync(message).ConfigureAwait(false);
                break;

            default:
                break;
        }

        // Every inbound message is also surfaced to external consumers.
        await _externalInbox.Writer.WriteAsync(message, _runCts.Token).ConfigureAwait(false);
    }

    private void CompletePending(AgentMessage message)
    {
        // Convention: Response/Decline carry the original Invoke's Id in the
        // first 16 bytes of the payload when generated by RouteInvokeAsync.
        if (message.Payload.Length < 16)
        {
            return;
        }
        var correlationId = new Guid(message.Payload.AsSpan(0, 16));
        if (_pendingInvocations.TryGetValue(correlationId, out var tcs))
        {
            tcs.TrySetResult(message);
        }
    }

    private async Task RouteInvokeAsync(AgentMessage invoke)
    {
        if (_capabilityHandler is null)
        {
            return;
        }

        // Best-effort: a real implementation negotiates which capability is
        // being invoked by carrying its name in the payload. The in-memory
        // mock simply hands the first advertised capability to the handler.
        var capability = _ownCapabilities.Count > 0
            ? _ownCapabilities[0]
            : new AgentCapability("unknown", "0.0.0", 0m, "SDPKT");

        byte[]? result;
        try
        {
            result = _capabilityHandler(capability, invoke.Payload);
        }
        catch
        {
            result = null;
        }

        var correlationPrefix = invoke.Id.ToByteArray();

        if (result is null)
        {
            var decline = AgentMessage.Create(
                AgentMessageKind.Decline,
                _ownUhid,
                invoke.FromUhid,
                "application/octet-stream",
                payload: correlationPrefix,
                signature: Sign(correlationPrefix));
            _bus.Send(decline);
            return;
        }

        var responsePayload = new byte[correlationPrefix.Length + result.Length];
        Buffer.BlockCopy(correlationPrefix, 0, responsePayload, 0, correlationPrefix.Length);
        Buffer.BlockCopy(result, 0, responsePayload, correlationPrefix.Length, result.Length);

        var response = AgentMessage.Create(
            AgentMessageKind.Response,
            _ownUhid,
            invoke.FromUhid,
            "application/octet-stream",
            payload: responsePayload,
            signature: Sign(responsePayload));
        _bus.Send(response);

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private byte[] Sign(byte[] data) => _signer is null ? [] : _signer(data);

    private PeerAgent WithLastSeen(PeerAgent peer)
    {
        var lastSeen = _lastSeen.TryGetValue(peer.UhidIdentityId, out var ts)
            ? ts
            : peer.LastSeenAt;
        return peer with { LastSeenAt = lastSeen };
    }
}
