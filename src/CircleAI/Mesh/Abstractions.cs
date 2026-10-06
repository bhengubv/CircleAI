// Abstractions.cs
//
// The three seams that make the router testable and host-pluggable:
//   IOffloadRouter          - what callers use ("run this turn, wherever").
//   ILocalInferenceFallback - the local brain (host adapts its own engine).
//   IMeshOffloadClient      - the transport mechanism (send request, await reply).

namespace CircleAI.Mesh;

/// <summary>
/// The mesh hand-off router. Given a turn the local device cannot serve well,
/// it finds a capable peer, delegates the completion, and falls back to local
/// inference when no peer is available or the chosen peer fails.
/// </summary>
public interface IOffloadRouter
{
    /// <summary>
    /// Route <paramref name="turn"/> to the best available brain and return
    /// the completion. Never throws for routing failures - inspect
    /// <see cref="OffloadResult.Success"/> and <see cref="OffloadResult.ServedBy"/>.
    /// Only <see cref="OperationCanceledException"/> (from <paramref name="ct"/>)
    /// propagates.
    /// </summary>
    Task<OffloadResult> RouteAsync(OffloadTurn turn, CancellationToken ct = default);
}

/// <summary>
/// The local brain the router falls back to, and the same brain that answers
/// inbound peer requests when this node advertises a model. The host implements
/// this by adapting whatever inference engine it owns. Implementations SHOULD
/// serve <see cref="OffloadTurn.ModelId"/> exactly when they have it loaded, and
/// MAY downshift to a smaller local model otherwise - the router treats either
/// as a local fallback.
/// </summary>
public interface ILocalInferenceFallback
{
    /// <summary>
    /// Complete <paramref name="turn"/> on the local device. Should return a
    /// failed <see cref="OffloadResult"/> (not throw) when it cannot serve.
    /// </summary>
    Task<OffloadResult> CompleteAsync(OffloadTurn turn, CancellationToken ct = default);
}

/// <summary>
/// Default fallback for a thin node that can only borrow, never serve. Always
/// reports failure, which makes the router surface the real reason a turn
/// could not be routed instead of pretending it produced an answer.
/// </summary>
public sealed class NullLocalInferenceFallback : ILocalInferenceFallback
{
    /// <summary>Shared stateless instance.</summary>
    public static readonly NullLocalInferenceFallback Instance = new();

    /// <inheritdoc/>
    public Task<OffloadResult> CompleteAsync(OffloadTurn turn, CancellationToken ct = default)
        => Task.FromResult(OffloadResult.Fail(
            "No local inference fallback is registered; this node can borrow a peer's brain but cannot serve locally.",
            OffloadServedBy.None));
}

/// <summary>
/// The transport-facing half of the router: serialises a turn onto an
/// <c>INetworkTransport</c>, addresses it to a specific peer, and correlates
/// the reply that streams back. Implementations own the single receive pump for
/// the shared transport (see <c>MeshOffloadClient</c>).
/// </summary>
public interface IMeshOffloadClient
{
    /// <summary>
    /// True when the transport is available and the receive pump is running, so
    /// a request stands a chance of getting a reply. Diagnostic only - the
    /// router still degrades gracefully when this is false.
    /// </summary>
    bool IsReady { get; }

    /// <summary>
    /// Send <paramref name="turn"/> to <paramref name="peerId"/> and await the
    /// peer's completion, up to <paramref name="timeout"/>. Returns a failed
    /// <see cref="OffloadResult"/> (never throws) on transport error, remote
    /// failure, or timeout, so the router can move to the next peer or fall
    /// back locally. Cancellation via <paramref name="ct"/> propagates.
    /// </summary>
    Task<OffloadResult> RequestAsync(
        string peerId,
        OffloadTurn turn,
        TimeSpan timeout,
        CancellationToken ct = default);
}
