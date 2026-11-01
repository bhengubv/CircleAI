// CascadeTransportSelector.cs
//
// The selector ITransportSelector has described since it was written, and nothing implemented
// (OPEN-GAPS A10): eleven transports stood behind a contract with no implementation in the
// whole tree, so nothing could choose between them.
//
// THE CASCADE IS THE ONE THE CONTRACT STATES - gRPC, WebSocket, HTTP, MQTT, TCP, then the
// links that need no infrastructure (WiFi, Bluetooth, NearLink, the Aether mesh), then DTN's
// store-and-forward, then the local queue - narrowed by what the moment allows:
//
//   what is up        only transports the context reports available; DTN and the local
//                     queue are always offered as the floor (the queue only if policy keeps it)
//   connectivity      offline keeps only the floor; mesh-only keeps the mesh; local-only
//                     drops the internet transports
//   policy            Permits per transport, ForceTransport, MeshFirst, AllowCloudTransports
//   an emergency      goes mesh first, as MeshFirst does: an SOS must not wait on a cloud
//                     round trip that may be the thing that is down
//
// UDP IS NEVER CHOSEN FOR A PAYLOAD. It is not in the stated cascade and has no delivery at
// all; a caller that wants datagrams names the transport outright.

using System;
using System.Collections.Generic;
using System.Linq;

namespace CircleAI.Networking;

/// <summary>Picks transports in the documented cascade, narrowed by context and policy.</summary>
public sealed class CascadeTransportSelector : ITransportSelector
{
    /// <summary>The order the contract states, best first.</summary>
    public static IReadOnlyList<TransportKind> DefaultCascade { get; } =
    [
        TransportKind.Grpc, TransportKind.WebSocket, TransportKind.Http, TransportKind.Mqtt,
        TransportKind.Tcp, TransportKind.WiFi, TransportKind.Bluetooth, TransportKind.NearLink,
        TransportKind.Aether, TransportKind.Dtn, TransportKind.LocalStore,
    ];

    // Need the internet: the first four reach a server.
    private static readonly HashSet<TransportKind> Cloud =
        [TransportKind.Grpc, TransportKind.WebSocket, TransportKind.Http, TransportKind.Mqtt];

    // Need nothing but the phones themselves.
    private static readonly HashSet<TransportKind> Mesh =
        [TransportKind.WiFi, TransportKind.Bluetooth, TransportKind.NearLink, TransportKind.Aether];

    // Always offered last: they hold a payload until a path exists.
    private static readonly HashSet<TransportKind> Floor = [TransportKind.Dtn, TransportKind.LocalStore];

    private readonly INetworkPolicy _policy;

    /// <param name="policy">The rules to honour; null is <see cref="DefaultNetworkPolicy"/>.</param>
    public CascadeTransportSelector(INetworkPolicy? policy = null) => _policy = policy ?? DefaultNetworkPolicy.Instance;

    /// <inheritdoc />
    public TransportKind SelectBest(NetworkPayload payload, NetworkContext context)
    {
        var cascade = GetCascade(payload, context);
        return cascade.Count > 0 ? cascade[0] : TransportKind.LocalStore;
    }

    /// <inheritdoc />
    public IReadOnlyList<TransportKind> GetCascade(NetworkPayload payload, NetworkContext context)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(context);

        if (_policy.ForceTransport is { } forced)
            return _policy.Permits(forced, payload) ? [forced] : [];

        var up = context.AvailableTransports.ToHashSet();
        IEnumerable<TransportKind> order = DefaultCascade;

        if (_policy.MeshFirst || payload.Priority == MessagePriority.Emergency)
            order = order.Where(Mesh.Contains).Concat(order.Where(k => !Mesh.Contains(k)));

        return [.. order.Where(kind =>
            (Floor.Contains(kind) || up.Contains(kind))
            && Allowed(kind, context.State)
            && (_policy.AllowCloudTransports || !Cloud.Contains(kind))
            && (kind != TransportKind.LocalStore || _policy.OfflineQueueEnabled)
            && _policy.Permits(kind, payload))];
    }

    // What the moment's connectivity leaves open.
    private static bool Allowed(TransportKind kind, ConnectivityState state) => state switch
    {
        ConnectivityState.Offline   => Floor.Contains(kind),
        ConnectivityState.MeshOnly  => Mesh.Contains(kind) || Floor.Contains(kind),
        ConnectivityState.LocalOnly => !Cloud.Contains(kind),
        _                           => true,
    };
}
